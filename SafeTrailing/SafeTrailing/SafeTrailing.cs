// ═══════════════════════════════════════════════════════════════════
// SafeTrailing v1.0
// Задача: трейлинг SL для конкретного символа.
// - Управляет ТОЛЬКО прибыльной позицией (pnl >= TrailTrigger %)
// - Если позиция в убытке — не трогает SL
// - Пользователь НЕ может перехватить управление (стратегия
//   всегда восстанавливает SL согласно параметрам)
// - Поддержка Hedge Mode: Long и Short независимо
// - Имя экземпляра = Symbol автоматически
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.IO;
using TradingPlatform.BusinessLayer;

namespace SafeTrailing
{
    public class SafeTrailing : Strategy
    {
        private const string VERSION = "v1.0";

        // ── Параметры ──────────────────────────────────────────────────
        [InputParameter("Symbol", 10)]
        public Symbol symbol;

        [InputParameter("Account", 20)]
        public Account account;

        [InputParameter("Trailing trigger %", 30, 0.1, 20, 0.1, 1)]
        public double TrailTriggerPct = 2.0;

        [InputParameter("Trailing distance %", 40, 0.1, 20, 0.1, 1)]
        public double TrailDistPct = 1.0;

        // ── Состояние одной управляемой позиции ───────────────────────
        private class TrailedPos
        {
            public string PositionId;
            public string SlOrderId;     // текущий SL ордер
            public double SlPrice;       // последняя установленная цена SL
            public bool IsLong;
            public double TrailBestPrice; // лучшая цена с момента активации
            public bool TrailActive;   // трейлинг активен
            public DateTime LastSlUpdate;  // throttle
        }

        // Ключ — Position.Id (в Hedge Mode Long и Short имеют разные Id)
        private readonly Dictionary<string, TrailedPos> _managed = new();

        private StreamWriter _logFile;

        // ── Конструктор — задаём имя; экземпляр будет назван по Symbol ─
        public SafeTrailing() : base()
        {
            this.Name = $"SafeTrailing {VERSION}";
            this.Description = "Trailing SL for specific symbol. Hedge Mode compatible.";
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

                // Обновить имя экземпляра = символ (отображается в Strategy Manager)
                this.Name = $"SafeTrailing {symbol.Name} {VERSION}";

                WriteLog($"[{VERSION}] ✅ SafeTrailing запущен");
                WriteLog($"[{VERSION}] 📋 Symbol:{symbol.Name} | Account:{account.Name}");
                WriteLog($"[{VERSION}] ⚙ Trail@{TrailTriggerPct}%±{TrailDistPct}%");

                // Подписаться на тики символа
                symbol.NewLast += OnNewLast;

                // Подхватить уже открытые позиции по символу
                foreach (var pos in Core.Instance.Positions)
                {
                    if (pos.Account == account && pos.Symbol == symbol)
                        TryAttach(pos, "pre-existing");
                }

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

        // ── Подключить позицию к управлению ───────────────────────────
        private void TryAttach(Position pos, string reason)
        {
            if (_managed.ContainsKey(pos.Id)) return;

            // Найти существующий SL ордер для этой позиции
            var existingSl = FindExistingSlOrder(pos);

            var tp = new TrailedPos
            {
                PositionId = pos.Id,
                IsLong = pos.Side == Side.Buy,
                TrailBestPrice = pos.OpenPrice,
                TrailActive = false,
                LastSlUpdate = DateTime.MinValue,
                // Если SL уже выставлен — запоминаем его
                SlOrderId = existingSl?.Id,
                SlPrice = existingSl?.TriggerPrice ?? 0
            };

            _managed[pos.Id] = tp;

            WriteLog($"[{VERSION}] 🔗 [{pos.Symbol.Name}] {pos.Side} подключена ({reason}) @ {pos.OpenPrice:F6} ID:{pos.Id}");
            if (existingSl != null)
                WriteLog($"[{VERSION}] 📌 [{pos.Symbol.Name}] {pos.Side} существующий SL: {tp.SlPrice:F6} ID:{tp.SlOrderId}");
            else
                WriteLog($"[{VERSION}] ℹ [{pos.Symbol.Name}] {pos.Side} SL не найден — будет выставлен при активации трейлинга.");
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
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ OnPositionAdded: {ex}");
            }
        }

        // ── Позиция закрыта ────────────────────────────────────────────
        private void OnPositionRemoved(Position pos)
        {
            try
            {
                if (!_managed.ContainsKey(pos.Id)) return;
                _managed.Remove(pos.Id);
                WriteLog($"[{VERSION}] ■ [{pos.Symbol.Name}] {pos.Side} закрыта — очищена. ID:{pos.Id}");
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ OnPositionRemoved: {ex}");
            }
        }

        // ── Каждый тик символа ────────────────────────────────────────
        private void OnNewLast(Symbol sym, Last last)
        {
            try
            {
                double price = last.Price;

                // Проходим по всем управляемым позициям
                // В Hedge Mode их может быть 2 (Long + Short)
                foreach (var pos in Core.Instance.Positions)
                {
                    if (pos.Account != account) continue;
                    if (pos.Symbol != symbol) continue;
                    if (!_managed.ContainsKey(pos.Id)) continue;

                    var tp = _managed[pos.Id];

                    // Рассчитать PnL позиции
                    double pnlPct = tp.IsLong
                        ? (price - pos.OpenPrice) / pos.OpenPrice * 100.0
                        : (pos.OpenPrice - price) / pos.OpenPrice * 100.0;

                    // Позиция в убытке — не трогаем SL (пользователь управляет вручную)
                    if (pnlPct < 0)
                    {
                        if (tp.TrailActive)
                        {
                            // Трейлинг был активен но цена ушла в минус
                            // Оставляем последний SL на месте — не трогаем
                            WriteLog($"[{VERSION}] ⚠ [{symbol.Name}] {pos.Side} PnL={pnlPct:F3}% — убыток, SL не двигаем.");
                            tp.TrailActive = false;
                        }
                        continue;
                    }

                    // Активировать трейлинг при достижении триггера
                    if (!tp.TrailActive && pnlPct >= TrailTriggerPct)
                    {
                        tp.TrailActive = true;
                        tp.TrailBestPrice = price;
                        WriteLog($"[{VERSION}] 🚀 [{symbol.Name}] {pos.Side} Трейлинг активирован: PnL={pnlPct:F3}% @ {price:F6}");
                    }

                    if (!tp.TrailActive) continue;

                    // Обновить best price и двигать SL
                    bool newExtreme = tp.IsLong
                        ? price > tp.TrailBestPrice
                        : price < tp.TrailBestPrice;

                    if (newExtreme)
                    {
                        tp.TrailBestPrice = price;
                        double newSl = RoundToTick(
                            CalcPrice(price, tp.IsLong, -TrailDistPct),
                            symbol.TickSize);
                        WriteLog($"[{VERSION}] 📈 [{symbol.Name}] {pos.Side} Trail: {price:F6} → SL={newSl:F6}");
                        ReplaceSL(pos, tp, newSl);
                    }
                    else
                    {
                        // Цена не обновила экстремум — проверяем не сдвинул ли
                        // пользователь SL и восстанавливаем если нужно
                        CheckAndRestoreSL(pos, tp);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ OnNewLast: {ex}");
            }
        }

        // ── Проверить SL и восстановить если пользователь его изменил ─
        // В отличие от SafeBreakeven — здесь пользователь НЕ может
        // перехватить управление. Стратегия всегда восстанавливает SL.
        private void CheckAndRestoreSL(Position pos, TrailedPos tp)
        {
            if (string.IsNullOrEmpty(tp.SlOrderId) || tp.SlPrice <= 0) return;

            var slOrder = FindOrder(tp.SlOrderId);

            // SL исчез или изменился — восстановить
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
                    reason = $"SL изменён: {tp.SlPrice:F6}→{slOrder.TriggerPrice:F6}";
                }
            }

            if (needRestore)
            {
                WriteLog($"[{VERSION}] 🔧 [{symbol.Name}] {pos.Side} Восстанавливаем SL ({reason}) → {tp.SlPrice:F6}");
                ReplaceSL(pos, tp, tp.SlPrice);
            }
        }

        // ── Replace SL: Cancel + PlaceOrder ───────────────────────────
        private bool ReplaceSL(Position pos, TrailedPos tp, double newPrice)
        {
            try
            {
                // Throttle — не спамить биржу
                if ((DateTime.UtcNow - tp.LastSlUpdate).TotalMilliseconds < 300)
                    return false;
                tp.LastSlUpdate = DateTime.UtcNow;

                Side closeSide = tp.IsLong ? Side.Sell : Side.Buy;

                // Отменить старый SL если есть
                if (!string.IsNullOrEmpty(tp.SlOrderId))
                    CancelOrder(tp.SlOrderId, pos.Symbol.Name, $"SL(replace) {pos.Side}");

                // Выставить новый SL
                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                {
                    Symbol = pos.Symbol,
                    Account = pos.Account,
                    Side = closeSide,
                    OrderTypeId = OrderType.Stop,
                    TriggerPrice = newPrice,
                    Quantity = pos.Quantity,
                    TimeInForce = TimeInForce.GTC,
                    PositionId = pos.Id
                });

                WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] {pos.Side} ReplaceSL@{newPrice:F6}: {result.Status} {result.Message ?? "OK"} ID:{result.OrderId ?? "null"}");

                if (result.Status != TradingOperationResultStatus.Success)
                    return false;

                // Обновляем ID сразу — надёжно
                tp.SlOrderId = result.OrderId;
                tp.SlPrice = newPrice;
                return true;
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ ReplaceSL: {ex}");
                return false;
            }
        }

        // ── Найти существующий SL ордер для позиции ───────────────────
        private Order FindExistingSlOrder(Position pos)
        {
            Side expectedSide = pos.Side == Side.Buy ? Side.Sell : Side.Buy;
            foreach (var o in Core.Instance.Orders)
            {
                if (o.Account != pos.Account) continue;
                if (o.Symbol != pos.Symbol) continue;
                if (o.Side != expectedSide) continue;
                if (o.OrderTypeId != OrderType.Stop) continue;
                return o; // берём первый подходящий
            }
            return null;
        }

        // ── Отменить ордер ─────────────────────────────────────────────
        private void CancelOrder(string id, string sym, string label)
        {
            if (string.IsNullOrEmpty(id)) return;
            var order = FindOrder(id);
            if (order == null) { WriteLog($"[{VERSION}] ℹ [{sym}] {label} ID:{id} не найден."); return; }
            WriteLog($"[{VERSION}] 📤 [{sym}] Cancel {label} ID:{id}");
            var r = order.Cancel();
            WriteLog($"[{VERSION}] 📨 [{sym}] Cancel {label}: {r.Status} {r.Message ?? "OK"}");
        }

        private Order FindOrder(string id) { if (string.IsNullOrEmpty(id)) return null; foreach (var o in Core.Instance.Orders) if (o.Id == id) return o; return null; }
        private double RoundToTick(double p, double t) => t > 0 ? Math.Round(p / t) * t : p;
        private double CalcPrice(double b, bool l, double pct) => l ? b * (1 + pct / 100.0) : b * (1 - pct / 100.0);

        private void InitLogFile()
        {
            try
            {
                string name = symbol?.Name ?? "Unknown";
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"SafeTrailing_{name}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
                _logFile = new StreamWriter(path) { AutoFlush = true };
                WriteLog($"[{VERSION}] 📝 Log: {path}");
            }
            catch (Exception ex) { Log($"[{VERSION}] ⚠ Log error: {ex.Message}", StrategyLoggingLevel.Error); }
        }
        private void CloseLogFile() { try { _logFile?.Close(); _logFile = null; } catch { } }
        private void WriteLog(string msg, StrategyLoggingLevel lvl = StrategyLoggingLevel.Trading)
        {
            Log(msg, lvl);
            try { _logFile?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}"); } catch { }
        }
    }
}