using System;
using System.Collections.Generic;
using System.IO;
using TradingPlatform.BusinessLayer;

namespace SafeTrailing
{
    /// <summary>
    /// SafeTrailing v1.1
    ///
    /// Исправления vs v1.0:
    /// 1. Флаг _replacing блокирует CheckAndRestoreSL во время замены SL.
    ///    Устраняет спам "Восстанавливаем SL" (сотни раз в секунду).
    /// 2. Задержка 500ms между Cancel и PlaceOrder — Binance обрабатывает
    ///    Cancel асинхронно, без паузы следующий Place получает -4130.
    /// 3. SlPrice обновляется ТОЛЬКО после успешного PlaceOrder —
    ///    восстановление всегда идёт на актуальную подтверждённую цену.
    /// 4. TrailBestPrice обновляется только при успешном ReplaceSL —
    ///    если Place упал с Failure, экстремум не сдвигается.
    /// 5. Throttle вынесен на уровень тика (не внутри ReplaceSL) —
    ///    предотвращает очередь из вызовов при быстром движении цены.
    /// </summary>
    public class SafeTrailing : Strategy
    {
        private const string VERSION = "v1.1";

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
            public string SlOrderId;       // актуальный ID SL на бирже
            public double SlPrice;         // цена SL подтверждённая биржей
            public bool IsLong;
            public double TrailBestPrice;  // лучшая цена с момента активации
            public bool TrailActive;
            public DateTime LastSlUpdate;    // время последнего успешного ReplaceSL

            // Флаг: идёт замена SL (Cancel+delay+Place).
            // Пока true — CheckAndRestoreSL не вызывается.
            public bool Replacing;
        }

        // Ключ — Position.Id (в Hedge Mode Long и Short имеют разные Id)
        private readonly Dictionary<string, TrailedPos> _managed = new();

        private StreamWriter _logFile;

        // ── Конструктор ────────────────────────────────────────────────
        public SafeTrailing() : base()
        {
            this.Name = $"SafeTrailing {VERSION}";
            this.Description = "Trailing SL for specific symbol. Hedge Mode. No manual override.";
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

                // Имя экземпляра = символ (видно в Strategy Manager)
                this.Name = $"SafeTrailing {symbol.Name} {VERSION}";

                WriteLog($"[{VERSION}] ✅ SafeTrailing запущен");
                WriteLog($"[{VERSION}] 📋 Symbol:{symbol.Name} | Account:{account.Name}");
                WriteLog($"[{VERSION}] ⚙ Trail@{TrailTriggerPct}% | Distance:{TrailDistPct}%");

                // Подписаться на тики символа
                symbol.NewLast += OnNewLast;

                // Подхватить уже открытые позиции
                foreach (var pos in Core.Instance.Positions)
                    if (pos.Account == account && pos.Symbol == symbol)
                        TryAttach(pos, "pre-existing");

                Core.PositionAdded += OnPositionAdded;
                Core.PositionRemoved += OnPositionRemoved;
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ OnRun: {ex}");
            }
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

            var existingSl = FindExistingSlOrder(pos);

            var tp = new TrailedPos
            {
                PositionId = pos.Id,
                IsLong = pos.Side == Side.Buy,
                TrailBestPrice = pos.OpenPrice,
                TrailActive = false,
                LastSlUpdate = DateTime.MinValue,
                Replacing = false,
                SlOrderId = existingSl?.Id ?? string.Empty,
                SlPrice = existingSl?.TriggerPrice ?? 0
            };

            _managed[pos.Id] = tp;

            WriteLog($"[{VERSION}] 🔗 [{pos.Symbol.Name}] {pos.Side} подключена ({reason}) @ {pos.OpenPrice:F6} ID:{pos.Id}");
            if (existingSl != null)
                WriteLog($"[{VERSION}] 📌 [{pos.Symbol.Name}] {pos.Side} SL: {tp.SlPrice:F6} ID:{tp.SlOrderId}");
            else
                WriteLog($"[{VERSION}] ℹ [{pos.Symbol.Name}] {pos.Side} SL не найден — выставим при активации.");
        }

        // ── Новая позиция ──────────────────────────────────────────────
        private void OnPositionAdded(Position pos)
        {
            try
            {
                if (pos.Account != account) return;
                if (pos.Symbol != symbol) return;
                if (_managed.ContainsKey(pos.Id)) return;
                WriteLog($"[{VERSION}] 📥 Новая позиция: [{pos.Symbol.Name}] {pos.Side} @ {pos.OpenPrice:F6} ID:{pos.Id}");
                TryAttach(pos, "new");
            }
            catch (Exception ex) { WriteLog($"[{VERSION}] ❌ OnPositionAdded: {ex}"); }
        }

        // ── Позиция закрыта ────────────────────────────────────────────
        private void OnPositionRemoved(Position pos)
        {
            try
            {
                if (!_managed.ContainsKey(pos.Id)) return;
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

                    // Пока идёт замена SL — пропускаем тик полностью
                    if (tp.Replacing) continue;

                    // Throttle на уровне тика: не чаще раза в 300ms
                    if ((DateTime.UtcNow - tp.LastSlUpdate).TotalMilliseconds < 300) continue;

                    double pnlPct = tp.IsLong
                        ? (price - pos.OpenPrice) / pos.OpenPrice * 100.0
                        : (pos.OpenPrice - price) / pos.OpenPrice * 100.0;

                    // Позиция в убытке — трейлинг не активен, SL не трогаем
                    if (pnlPct < 0)
                    {
                        if (tp.TrailActive)
                        {
                            tp.TrailActive = false;
                            WriteLog($"[{VERSION}] ⚠ [{symbol.Name}] {pos.Side} PnL={pnlPct:F3}% — убыток, трейлинг приостановлен.");
                        }
                        continue;
                    }

                    // Активировать трейлинг
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

                    if (newExtreme)
                    {
                        // Цена обновила экстремум — двигаем SL
                        double newSl = RoundToTick(
                            CalcPrice(price, tp.IsLong, -TrailDistPct),
                            symbol.TickSize);

                        WriteLog($"[{VERSION}] 📈 [{symbol.Name}] {pos.Side} Trail: {price:F6} → SL={newSl:F6}");
                        ReplaceSL(pos, tp, price, newSl);
                    }
                    else
                    {
                        // Экстремум не обновился — проверяем целостность SL
                        // CheckAndRestoreSL вызывается только если НЕ идёт замена
                        CheckAndRestoreSL(pos, tp);
                    }
                }
            }
            catch (Exception ex) { WriteLog($"[{VERSION}] ❌ OnNewLast: {ex}"); }
        }

        // ── Заменить SL ────────────────────────────────────────────────
        // Cancel + 500ms пауза + PlaceOrder.
        // Пауза обязательна: Binance обрабатывает Cancel асинхронно,
        // без паузы новый Place получает -4130 (ордер ещё существует).
        private void ReplaceSL(Position pos, TrailedPos tp, double newBestPrice, double newSlPrice)
        {
            // Устанавливаем флаг — блокируем все тики пока не завершим
            tp.Replacing = true;

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    Side closeSide = tp.IsLong ? Side.Sell : Side.Buy;

                    // Шаг 1: Отменить старый SL
                    if (!string.IsNullOrEmpty(tp.SlOrderId))
                    {
                        var old = FindOrder(tp.SlOrderId);
                        if (old != null)
                        {
                            WriteLog($"[{VERSION}] 📤 [{pos.Symbol.Name}] {pos.Side} Cancel SL ID:{tp.SlOrderId}");
                            var cancelRes = old.Cancel();
                            WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] {pos.Side} Cancel: {cancelRes.Status} {cancelRes.Message ?? "OK"}");
                        }
                        else
                        {
                            WriteLog($"[{VERSION}] ℹ [{pos.Symbol.Name}] {pos.Side} SL ID:{tp.SlOrderId} не найден (уже исполнен?).");
                        }
                    }

                    // Шаг 2: Пауза — дать бирже время обработать Cancel
                    await System.Threading.Tasks.Task.Delay(500);

                    // Проверить что позиция ещё открыта
                    if (!_managed.ContainsKey(pos.Id))
                    {
                        WriteLog($"[{VERSION}] ℹ [{pos.Symbol.Name}] {pos.Side} позиция закрыта во время замены SL — отмена.");
                        return;
                    }

                    // Шаг 3: Выставить новый SL
                    WriteLog($"[{VERSION}] 📤 [{pos.Symbol.Name}] {pos.Side} Place SL@{newSlPrice:F6}");
                    var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                    {
                        Symbol = pos.Symbol,
                        Account = pos.Account,
                        Side = closeSide,
                        OrderTypeId = OrderType.Stop,
                        TriggerPrice = newSlPrice,
                        Quantity = pos.Quantity,
                        TimeInForce = TimeInForce.GTC,
                        PositionId = pos.Id
                    });

                    WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] {pos.Side} Place SL: {result.Status} {result.Message ?? "OK"} ID:{result.OrderId ?? "null"}");

                    if (result.Status == TradingOperationResultStatus.Success)
                    {
                        // Обновляем состояние ТОЛЬКО при успехе
                        tp.SlOrderId = result.OrderId;
                        tp.SlPrice = newSlPrice;
                        tp.TrailBestPrice = newBestPrice; // фиксируем новый экстремум
                        tp.LastSlUpdate = DateTime.UtcNow;
                        WriteLog($"[{VERSION}] ✅ [{pos.Symbol.Name}] {pos.Side} SL установлен: {newSlPrice:F6} ID:{tp.SlOrderId}");
                    }
                    else
                    {
                        // Place упал — оставляем SlPrice и TrailBestPrice без изменений
                        // Следующий тик попробует снова
                        WriteLog($"[{VERSION}] ❌ [{pos.Symbol.Name}] {pos.Side} Place SL ошибка: {result.Message}");
                    }
                }
                catch (Exception ex)
                {
                    WriteLog($"[{VERSION}] ❌ ReplaceSL: {ex}");
                }
                finally
                {
                    // Снимаем флаг в любом случае — разблокируем тики
                    tp.Replacing = false;
                }
            });
        }

        // ── Проверить и восстановить SL ───────────────────────────────
        // Вызывается только когда цена не обновляет экстремум
        // и флаг Replacing = false.
        private void CheckAndRestoreSL(Position pos, TrailedPos tp)
        {
            // Нет данных о SL — нечего проверять
            if (string.IsNullOrEmpty(tp.SlOrderId) || tp.SlPrice <= 0) return;

            var slOrder = FindOrder(tp.SlOrderId);
            bool needRestore = false;
            string reason = "";

            if (slOrder == null)
            {
                needRestore = true;
                reason = $"SL ID:{tp.SlOrderId} исчез";
            }
            else
            {
                double tolerance = symbol.TickSize * 2;
                if (Math.Abs(slOrder.TriggerPrice - tp.SlPrice) > tolerance)
                {
                    needRestore = true;
                    reason = $"цена изменена: {tp.SlPrice:F6}→{slOrder.TriggerPrice:F6}";
                }
            }

            if (!needRestore) return;

            // Восстанавливаем на tp.SlPrice — последнюю успешно установленную цену
            WriteLog($"[{VERSION}] 🔧 [{symbol.Name}] {pos.Side} Восстанавливаем SL ({reason}) → {tp.SlPrice:F6}");
            ReplaceSL(pos, tp, tp.TrailBestPrice, tp.SlPrice);
        }

        // ── Найти существующий SL ордер ────────────────────────────────
        private Order FindExistingSlOrder(Position pos)
        {
            Side expectedSide = pos.Side == Side.Buy ? Side.Sell : Side.Buy;
            foreach (var o in Core.Instance.Orders)
            {
                if (o.Account != pos.Account) continue;
                if (o.Symbol != pos.Symbol) continue;
                if (o.Side != expectedSide) continue;
                if (o.OrderTypeId != OrderType.Stop) continue;
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
                string name = symbol?.Name ?? "Unknown";
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    $"SafeTrailing_{name}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
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