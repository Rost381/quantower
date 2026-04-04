using System;
using System.Collections.Generic;
using System.IO;
using TradingPlatform.BusinessLayer;

namespace SafeTrailing
{
    /// <summary>
    /// SafeTrailing v2.0
    ///
    /// Ключевое изменение архитектуры:
    /// PLACE NEW → затем CANCEL OLD (не наоборот).
    ///
    /// Это гарантирует что в любой момент времени на бирже
    /// присутствует минимум один защитный ордер. Нет "окна"
    /// между удалением старого и выставлением нового.
    ///
    /// Тип ордера: Limit reduce-only (PositionId = decrease only).
    /// SL пользователя не трогается — стратегия управляет
    /// только своими Limit ордерами.
    ///
    /// Throttle: новый ордер выставляется не чаще раза в 1 сек.
    /// После успешного Place — пауза 800ms перед Cancel старого,
    /// чтобы биржа успела обработать новый ордер.
    /// </summary>
    public class SafeTrailing : Strategy
    {
        private const string VERSION = "v2.0";

        // ── Параметры ──────────────────────────────────────────────────
        [InputParameter("Symbol", 10)]
        public Symbol symbol;

        [InputParameter("Account", 20)]
        public Account account;

        [InputParameter("Trailing trigger %", 30, 0.1, 20, 0.1, 1)]
        public double TrailTriggerPct = 2.0;

        [InputParameter("Trailing distance %", 40, 0.1, 20, 0.1, 1)]
        public double TrailDistPct = 1.0;

        // ── Состояние позиции ──────────────────────────────────────────
        private class TrailedPos
        {
            public string PositionId;
            public bool IsLong;
            public double OpenPrice;

            // Текущий активный Limit ордер стратегии
            public string ActiveOrderId;
            public double ActiveOrderPrice;

            // Предыдущий ордер ожидающий отмены после паузы
            // (выставлен новый, старый ещё не отменён)
            public string PendingCancelId;

            public double TrailBestPrice;
            public bool TrailActive;

            // Время последнего успешного Place — throttle
            public DateTime LastPlaceTime;
        }

        private readonly Dictionary<string, TrailedPos> _managed = new();
        private StreamWriter _logFile;

        public SafeTrailing() : base()
        {
            this.Name = $"SafeTrailing {VERSION}";
            this.Description = "Trailing via Limit reduce-only. Place-then-Cancel. Hedge Mode.";
        }

        // ── Запуск ─────────────────────────────────────────────────────
        protected override void OnRun()
        {
            try
            {
                if (symbol == null || account == null)
                {
                    Log($"[{VERSION}] ❌ Symbol или Account не выбран!", StrategyLoggingLevel.Error);
                    Stop();
                    return;
                }

                InitLogFile();
                _managed.Clear();
                this.Name = $"SafeTrailing {symbol.Name} {VERSION}";

                WriteLog($"[{VERSION}] ✅ Запущен | Symbol:{symbol.Name} | Account:{account.Name}");
                WriteLog($"[{VERSION}] ⚙ Trail@{TrailTriggerPct}% | Distance:{TrailDistPct}%");

                symbol.NewLast += OnNewLast;

                // Подхватить открытые позиции
                foreach (var pos in Core.Instance.Positions)
                    if (pos.Account == account && pos.Symbol == symbol)
                        TryAttach(pos, "pre-existing");

                Core.PositionAdded += OnPositionAdded;
                Core.PositionRemoved += OnPositionRemoved;
            }
            catch (Exception ex) { WriteLog($"[{VERSION}] ❌ OnRun: {ex}"); }
        }

        // ── Остановка ──────────────────────────────────────────────────
        protected override void OnStop()
        {
            Core.PositionAdded -= OnPositionAdded;
            Core.PositionRemoved -= OnPositionRemoved;
            if (symbol != null) symbol.NewLast -= OnNewLast;
            _managed.Clear();
            WriteLog($"[{VERSION}] ■ Остановлен.");
            CloseLogFile();
        }

        // ── Подключить позицию ─────────────────────────────────────────
        private void TryAttach(Position pos, string reason)
        {
            if (_managed.ContainsKey(pos.Id)) return;

            var tp = new TrailedPos
            {
                PositionId = pos.Id,
                IsLong = pos.Side == Side.Buy,
                OpenPrice = pos.OpenPrice,
                ActiveOrderId = string.Empty,
                ActiveOrderPrice = 0,
                PendingCancelId = string.Empty,
                TrailBestPrice = pos.OpenPrice,
                TrailActive = false,
                LastPlaceTime = DateTime.MinValue
            };

            // Найти существующий Limit reduce-only ордер стратегии если есть
            var existing = FindOurLimitOrder(pos);
            if (existing != null)
            {
                tp.ActiveOrderId = existing.Id;
                tp.ActiveOrderPrice = existing.Price;
                WriteLog($"[{VERSION}] 📌 [{pos.Symbol.Name}] {pos.Side} найден Limit ордер: {tp.ActiveOrderPrice:F6} ID:{tp.ActiveOrderId}");
            }

            _managed[pos.Id] = tp;
            WriteLog($"[{VERSION}] 🔗 [{pos.Symbol.Name}] {pos.Side} подключена ({reason}) @ {pos.OpenPrice:F6} ID:{pos.Id}");
        }

        private void OnPositionAdded(Position pos)
        {
            try
            {
                if (pos.Account != account) return;
                if (pos.Symbol != symbol) return;
                if (_managed.ContainsKey(pos.Id)) return;
                WriteLog($"[{VERSION}] 📥 Новая: [{pos.Symbol.Name}] {pos.Side} @ {pos.OpenPrice:F6} ID:{pos.Id}");
                TryAttach(pos, "new");
            }
            catch (Exception ex) { WriteLog($"[{VERSION}] ❌ OnPositionAdded: {ex}"); }
        }

        private void OnPositionRemoved(Position pos)
        {
            try
            {
                if (!_managed.ContainsKey(pos.Id)) return;
                var tp = _managed[pos.Id];

                // Отменить наш активный ордер при закрытии позиции
                CancelIfExists(tp.ActiveOrderId, pos.Symbol.Name, pos.Side.ToString(), "active");
                CancelIfExists(tp.PendingCancelId, pos.Symbol.Name, pos.Side.ToString(), "pending-cancel");

                _managed.Remove(pos.Id);
                WriteLog($"[{VERSION}] ■ [{pos.Symbol.Name}] {pos.Side} закрыта. ID:{pos.Id}");
            }
            catch (Exception ex) { WriteLog($"[{VERSION}] ❌ OnPositionRemoved: {ex}"); }
        }

        // ── Каждый тик ────────────────────────────────────────────────
        private void OnNewLast(Symbol sym, Last last)
        {
            try
            {
                double price = last.Price;

                foreach (var pos in Core.Instance.Positions)
                {
                    if (pos.Account != account) continue;
                    if (pos.Symbol != symbol) continue;
                    if (!_managed.ContainsKey(pos.Id)) continue;

                    var tp = _managed[pos.Id];

                    // Throttle: не чаще раза в 1 сек
                    if ((DateTime.UtcNow - tp.LastPlaceTime).TotalMilliseconds < 1000) continue;

                    double pnlPct = tp.IsLong
                        ? (price - tp.OpenPrice) / tp.OpenPrice * 100.0
                        : (tp.OpenPrice - price) / tp.OpenPrice * 100.0;

                    // Убыток — трейлинг не активен, ордер не трогаем
                    if (pnlPct < 0)
                    {
                        if (tp.TrailActive)
                        {
                            tp.TrailActive = false;
                            WriteLog($"[{VERSION}] ⚠ [{symbol.Name}] {pos.Side} PnL={pnlPct:F3}% — убыток, трейлинг приостановлен.");
                        }
                        continue;
                    }

                    // Активация трейлинга
                    if (!tp.TrailActive && pnlPct >= TrailTriggerPct)
                    {
                        tp.TrailActive = true;
                        tp.TrailBestPrice = price;
                        WriteLog($"[{VERSION}] 🚀 [{symbol.Name}] {pos.Side} Трейлинг активирован: PnL={pnlPct:F3}% @ {price:F6}");
                    }

                    if (!tp.TrailActive) continue;

                    bool newExtreme = tp.IsLong
                        ? price > tp.TrailBestPrice
                        : price < tp.TrailBestPrice;

                    if (!newExtreme) continue;

                    // Новый экстремум — выставить новый ордер
                    double newOrderPrice = RoundToTick(
                        CalcPrice(price, tp.IsLong, -TrailDistPct),
                        symbol.TickSize);

                    // Не двигать ордер если цена изменилась меньше чем на 2 тика
                    double minMove = symbol.TickSize * 2;
                    if (tp.ActiveOrderPrice > 0 &&
                        Math.Abs(newOrderPrice - tp.ActiveOrderPrice) < minMove)
                        continue;

                    WriteLog($"[{VERSION}] 📈 [{symbol.Name}] {pos.Side} Trail: {price:F6} → Limit={newOrderPrice:F6}");
                    PlaceThenCancel(pos, tp, price, newOrderPrice);
                }
            }
            catch (Exception ex) { WriteLog($"[{VERSION}] ❌ OnNewLast: {ex}"); }
        }

        // ── Place нового ордера → Cancel старого ──────────────────────
        // Архитектура: сначала Place нового, затем Cancel старого.
        // Между ними пауза 800ms — биржа успевает обработать Place.
        // В любой момент времени на бирже есть минимум один ордер.
        private void PlaceThenCancel(Position pos, TrailedPos tp, double newBestPrice, double newPrice)
        {
            try
            {
                tp.LastPlaceTime = DateTime.UtcNow;
                Side closeSide = tp.IsLong ? Side.Sell : Side.Buy;

                // ШАГ 1: Выставить НОВЫЙ Limit reduce-only ордер
                WriteLog($"[{VERSION}] 📤 [{pos.Symbol.Name}] {pos.Side} Place Limit@{newPrice:F6}");
                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                {
                    Symbol = pos.Symbol,
                    Account = pos.Account,
                    Side = closeSide,
                    OrderTypeId = OrderType.Limit,
                    Price = newPrice,
                    Quantity = pos.Quantity,
                    TimeInForce = TimeInForce.GTC,
                    PositionId = pos.Id          // reduce-only, закрывает только эту позицию
                });

                WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] {pos.Side} Place: {result.Status} {result.Message ?? "OK"} ID:{result.OrderId ?? "null"}");

                if (result.Status != TradingOperationResultStatus.Success)
                {
                    WriteLog($"[{VERSION}] ❌ [{pos.Symbol.Name}] {pos.Side} Place failed — старый ордер сохранён.");
                    return;
                }

                // Place успешен — обновляем состояние
                string oldOrderId = tp.ActiveOrderId;
                tp.ActiveOrderId = result.OrderId;
                tp.ActiveOrderPrice = newPrice;
                tp.TrailBestPrice = newBestPrice;

                WriteLog($"[{VERSION}] ✅ [{pos.Symbol.Name}] {pos.Side} Новый ордер: {newPrice:F6} ID:{tp.ActiveOrderId}");

                // ШАГ 2: Отменить СТАРЫЙ ордер с паузой 800ms
                // Пауза нужна чтобы биржа успела обработать новый Place
                // и не путала его со старым ордером
                if (!string.IsNullOrEmpty(oldOrderId))
                {
                    string capturedOldId = oldOrderId;
                    string capturedPosId = pos.Id;
                    string capturedSym = pos.Symbol.Name;
                    string capturedSide = pos.Side.ToString();

                    System.Threading.Tasks.Task.Delay(800).ContinueWith(_ =>
                    {
                        try
                        {
                            // Проверить что позиция ещё открыта
                            if (!_managed.ContainsKey(capturedPosId)) return;

                            CancelIfExists(capturedOldId, capturedSym, capturedSide, "old");
                        }
                        catch (Exception ex) { WriteLog($"[{VERSION}] ❌ DelayedCancel: {ex}"); }
                    });
                }
            }
            catch (Exception ex) { WriteLog($"[{VERSION}] ❌ PlaceThenCancel: {ex}"); }
        }

        // ── Отменить ордер если существует ────────────────────────────
        private void CancelIfExists(string orderId, string sym, string side, string label)
        {
            if (string.IsNullOrEmpty(orderId)) return;
            var order = FindOrder(orderId);
            if (order == null)
            {
                WriteLog($"[{VERSION}] ℹ [{sym}] {side} {label} ID:{orderId} не в кэше (возможно уже отменён).");
                return;
            }
            WriteLog($"[{VERSION}] 📤 [{sym}] {side} Cancel {label} ID:{orderId}");
            var r = order.Cancel();
            WriteLog($"[{VERSION}] 📨 [{sym}] {side} Cancel {label}: {r.Status} {r.Message ?? "OK"}");
        }

        // ── Найти наш Limit reduce-only ордер для позиции ─────────────
        // Ищем Limit ордер закрывающего направления с PositionId позиции
        private Order FindOurLimitOrder(Position pos)
        {
            Side expectedSide = pos.Side == Side.Buy ? Side.Sell : Side.Buy;
            foreach (var o in Core.Instance.Orders)
            {
                if (o.Account != pos.Account) continue;
                if (o.Symbol != pos.Symbol) continue;
                if (o.Side != expectedSide) continue;
                if (o.OrderTypeId != OrderType.Limit) continue;
                // Проверяем что ордер привязан к нашей позиции
                if (o.PositionId != pos.Id) continue;
                return o;
            }
            return null;
        }

        private Order FindOrder(string id) { if (string.IsNullOrEmpty(id)) return null; foreach (var o in Core.Instance.Orders) if (o.Id == id) return o; return null; }
        private double RoundToTick(double p, double t) => t > 0 ? Math.Round(p / t) * t : p;
        private double CalcPrice(double b, bool l, double pct) => l ? b * (1 + pct / 100.0) : b * (1 - pct / 100.0);

        private void InitLogFile()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    $"SafeTrailing_{symbol?.Name ?? "Unknown"}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
                _logFile = new StreamWriter(path) { AutoFlush = true };
                WriteLog($"[{VERSION}] 📝 Log: {path}");
            }
            catch (Exception ex) { Log($"[{VERSION}] ⚠ Log: {ex.Message}", StrategyLoggingLevel.Error); }
        }
        private void CloseLogFile() { try { _logFile?.Close(); _logFile = null; } catch { } }
        private void WriteLog(string msg, StrategyLoggingLevel lvl = StrategyLoggingLevel.Trading)
        {
            Log(msg, lvl);
            try { _logFile?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}"); } catch { }
        }
    }
}