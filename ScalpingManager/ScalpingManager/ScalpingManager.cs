using System;
using System.Collections.Generic;
using System.IO;
using TradingPlatform.BusinessLayer;

namespace ScalpingManager
{
    /// <summary>
    /// ScalpingManager v4.3
    /// - Исправлен WaitingSlConfirm: после подтверждения продолжаем тик
    /// - Детектор ручного изменения: только SL, TP не контролируется
    /// </summary>
    public class ScalpingManager : Strategy
    {
        private const string VERSION = "v4.3";

        [InputParameter("Account", 10)]
        public Account account;

        [InputParameter("Stop Loss %", 20, 0.1, 20, 0.1, 1)]
        public double SlPct = 2.4;

        [InputParameter("Take Profit %", 30, 0.1, 50, 0.1, 1)]
        public double TpPct = 5.0;

        [InputParameter("Breakeven trigger %", 40, 0.1, 10, 0.1, 1)]
        public double BeTriggerPct = 1.0;

        [InputParameter("Breakeven lock %", 50, 0.0, 10, 0.1, 1)]
        public double BeLockPct = 0.3;

        [InputParameter("Trailing trigger %", 60, 0.1, 10, 0.1, 1)]
        public double TrailTriggerPct = 2.0;

        [InputParameter("Trailing distance %", 70, 0.1, 10, 0.1, 1)]
        public double TrailDistPct = 1.0;

        private class ManagedPosition
        {
            public string PositionId;
            public string SlOrderId;
            public string TpOrderId;
            public double SlPrice;
            public double TpPrice;
            public double EntryPrice;
            public bool IsLong;
            public bool PricesConfirmed;
            public bool BeApplied;
            public bool TrailActive;
            public bool HandedOver;
            public double TrailBestPrice;
            public bool WaitingSlConfirm;
            public double PendingSlPrice;
        }

        private readonly Dictionary<string, ManagedPosition> _managed
            = new Dictionary<string, ManagedPosition>();
        private readonly HashSet<string> _preExisting
            = new HashSet<string>();
        private readonly Dictionary<string, string> _loggedPositions
            = new Dictionary<string, string>();

        private StreamWriter _logFile;

        public ScalpingManager() : base()
        {
            this.Name = $"ScalpingManager {VERSION}";
            this.Description = "Universal SL/TP/BE/Trail. Hedge+One-way. Binance/Bybit.";
        }

        protected override void OnRun()
        {
            if (this.account == null)
            {
                Log($"[{VERSION}] ❌ ОШИБКА: Account не выбран!", StrategyLoggingLevel.Error);
                Stop();
                return;
            }

            InitLogFile();
            _preExisting.Clear();
            _managed.Clear();
            _loggedPositions.Clear();

            foreach (var pos in Core.Instance.Positions)
            {
                if (pos.Account == this.account)
                {
                    _preExisting.Add(pos.Id);
                    WriteLog($"[{VERSION}] ⏭ Pre-existing: [{pos.Symbol.Name}] {pos.Side} @ {pos.OpenPrice:F6} ID:{pos.Id}");
                }
            }

            Core.PositionAdded += OnPositionAdded;
            Core.PositionRemoved += OnPositionRemoved;

            WriteLog($"[{VERSION}] ✅ ScalpingManager запущен");
            WriteLog($"[{VERSION}] 📋 Аккаунт: {this.account.Name}");
            WriteLog($"[{VERSION}] ⚙ SL:{SlPct}% | TP:{TpPct}% | BE@{BeTriggerPct}%→+{BeLockPct}% | Trail@{TrailTriggerPct}%±{TrailDistPct}%");
            WriteLog($"[{VERSION}] 🔒 Позиций заморожено: {_preExisting.Count}");
        }

        protected override void OnStop()
        {
            Core.PositionAdded -= OnPositionAdded;
            Core.PositionRemoved -= OnPositionRemoved;

            foreach (var kvp in _managed)
            {
                var pos = FindPositionById(kvp.Key);
                if (pos != null)
                {
                    pos.Symbol.NewLast -= OnNewLast;
                    WriteLog($"[{VERSION}] 🔌 Отписка: [{pos.Symbol.Name}]");
                }
            }

            _managed.Clear();
            _preExisting.Clear();
            _loggedPositions.Clear();

            WriteLog($"[{VERSION}] ■ Остановлен.");
            CloseLogFile();
        }

        private void OnPositionAdded(Position pos)
        {
            if (pos.Account != this.account) return;
            if (_preExisting.Contains(pos.Id)) return;

            if (_managed.ContainsKey(pos.Id))
            {
                if (!_loggedPositions.ContainsKey(pos.Symbol.Name) ||
                    _loggedPositions[pos.Symbol.Name] != pos.Id)
                {
                    WriteLog($"[{VERSION}] ⏭ [{pos.Symbol.Name}] Уже в управлении ID:{pos.Id} — пропуск.");
                    _loggedPositions[pos.Symbol.Name] = pos.Id;
                }
                return;
            }

            WriteLog($"[{VERSION}] 📥 PositionAdded: [{pos.Symbol.Name}] {pos.Side} @ {pos.OpenPrice:F6} Qty:{pos.Quantity} ID:{pos.Id}");
            _loggedPositions[pos.Symbol.Name] = pos.Id;

            bool isLong = pos.Side == Side.Buy;
            Side closeSide = isLong ? Side.Sell : Side.Buy;
            double tickSize = pos.Symbol.TickSize;
            double slPrice = RoundToTick(CalcPrice(pos.OpenPrice, isLong, -SlPct), tickSize);
            double tpPrice = RoundToTick(CalcPrice(pos.OpenPrice, isLong, +TpPct), tickSize);

            WriteLog($"[{VERSION}] 📐 [{pos.Symbol.Name}] TickSize:{tickSize} Entry:{pos.OpenPrice:F6} SL:{slPrice:F6} TP:{tpPrice:F6}");

            var mp = new ManagedPosition
            {
                PositionId = pos.Id,
                EntryPrice = pos.OpenPrice,
                IsLong = isLong,
                SlPrice = slPrice,
                TpPrice = tpPrice,
                PricesConfirmed = false,
                BeApplied = false,
                TrailActive = false,
                HandedOver = false,
                TrailBestPrice = pos.OpenPrice,
                SlOrderId = null,
                TpOrderId = null,
                WaitingSlConfirm = false,
                PendingSlPrice = 0
            };

            _managed[pos.Id] = mp;
            pos.Symbol.NewLast += OnNewLast;

            // SL
            WriteLog($"[{VERSION}] 📤 [{pos.Symbol.Name}] → SL Stop@{slPrice:F6} Side:{closeSide} Qty:{pos.Quantity} PosId:{pos.Id}");
            var slResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol = pos.Symbol,
                Account = pos.Account,
                Side = closeSide,
                OrderTypeId = OrderType.Stop,
                TriggerPrice = slPrice,
                Quantity = pos.Quantity,
                TimeInForce = TimeInForce.GTC,
                PositionId = pos.Id,
                Comment = $"SL_{pos.Id}"
            });
            WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] SL ответ: Status={slResult.Status} Msg={slResult.Message ?? "OK"} OrderId={slResult.OrderId ?? "null"}");
            if (slResult.Status == TradingOperationResultStatus.Success)
            {
                mp.SlOrderId = slResult.OrderId;
                WriteLog($"[{VERSION}] ✅ [{pos.Symbol.Name}] SL выставлен ID:{mp.SlOrderId}");
            }
            else
                WriteLog($"[{VERSION}] ❌ [{pos.Symbol.Name}] SL ОШИБКА: {slResult.Message}");

            // TP
            WriteLog($"[{VERSION}] 📤 [{pos.Symbol.Name}] → TP Limit@{tpPrice:F6} Side:{closeSide} Qty:{pos.Quantity} PosId:{pos.Id}");
            var tpResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol = pos.Symbol,
                Account = pos.Account,
                Side = closeSide,
                OrderTypeId = OrderType.Limit,
                Price = tpPrice,
                Quantity = pos.Quantity,
                TimeInForce = TimeInForce.GTC,
                PositionId = pos.Id,
                Comment = $"TP_{pos.Id}"
            });
            WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] TP ответ: Status={tpResult.Status} Msg={tpResult.Message ?? "OK"} OrderId={tpResult.OrderId ?? "null"}");
            if (tpResult.Status == TradingOperationResultStatus.Success)
            {
                mp.TpOrderId = tpResult.OrderId;
                WriteLog($"[{VERSION}] ✅ [{pos.Symbol.Name}] TP выставлен ID:{mp.TpOrderId}");
            }
            else
                WriteLog($"[{VERSION}] ❌ [{pos.Symbol.Name}] TP ОШИБКА: {tpResult.Message}");

            // Подтверждение цен от биржи через 2 сек
            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
            {
                if (!_managed.ContainsKey(pos.Id)) return;
                var slOrder = FindOrderById(mp.SlOrderId);
                var tpOrder = FindOrderById(mp.TpOrderId);
                if (slOrder != null)
                {
                    mp.SlPrice = slOrder.TriggerPrice;
                    WriteLog($"[{VERSION}] 📌 [{pos.Symbol.Name}] SL подтверждён: {mp.SlPrice:F6}");
                }
                else
                    WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] SL не найден (ID:{mp.SlOrderId})");
                if (tpOrder != null)
                {
                    mp.TpPrice = tpOrder.Price;
                    WriteLog($"[{VERSION}] 📌 [{pos.Symbol.Name}] TP подтверждён: {mp.TpPrice:F6}");
                }
                else
                    WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] TP не найден (ID:{mp.TpOrderId})");
                mp.PricesConfirmed = true;
                WriteLog($"[{VERSION}] ✅ [{pos.Symbol.Name}] Готов к управлению. SL={mp.SlPrice:F6} TP={mp.TpPrice:F6}");
            });
        }

        private void OnPositionRemoved(Position pos)
        {
            WriteLog($"[{VERSION}] 📥 PositionRemoved: [{pos.Symbol.Name}] ID:{pos.Id}");
            if (!_managed.ContainsKey(pos.Id))
            {
                WriteLog($"[{VERSION}] ℹ [{pos.Symbol.Name}] Не в управлении — пропуск.");
                return;
            }
            var mp = _managed[pos.Id];
            pos.Symbol.NewLast -= OnNewLast;
            CancelOrderById(mp.SlOrderId, pos.Symbol.Name, "SL");
            CancelOrderById(mp.TpOrderId, pos.Symbol.Name, "TP");
            _managed.Remove(pos.Id);
            if (_loggedPositions.ContainsKey(pos.Symbol.Name))
                _loggedPositions.Remove(pos.Symbol.Name);
            WriteLog($"[{VERSION}] ■ [{pos.Symbol.Name}] Закрыта — структура очищена.");
        }

        private void CancelOrderById(string orderId, string symbolName, string label)
        {
            if (string.IsNullOrEmpty(orderId))
            {
                WriteLog($"[{VERSION}] ⚠ [{symbolName}] {label} ID пустой — пропуск.");
                return;
            }
            var order = FindOrderById(orderId);
            if (order == null)
            {
                WriteLog($"[{VERSION}] ℹ [{symbolName}] {label} ID:{orderId} не найден (уже исполнен).");
                return;
            }
            WriteLog($"[{VERSION}] 📤 [{symbolName}] Отмена {label} ID:{orderId}");
            var result = order.Cancel();
            if (result.Status == TradingOperationResultStatus.Success)
                WriteLog($"[{VERSION}] ✅ [{symbolName}] {label} отменён.");
            else
                WriteLog($"[{VERSION}] ℹ [{symbolName}] {label}: {result.Message ?? "уже исполнен/отменён"}");
        }

        private void OnNewLast(Symbol symbol, Last last)
        {
            foreach (var pos in Core.Instance.Positions)
            {
                if (pos.Account != this.account) continue;
                if (pos.Symbol != symbol) continue;
                if (!_managed.ContainsKey(pos.Id)) continue;

                var mp = _managed[pos.Id];
                if (!mp.PricesConfirmed) continue;
                if (mp.HandedOver) continue;

                // ── Подтверждение ModifySL от биржи ───────────────────
                // ИСПРАВЛЕНИЕ v4.3: после подтверждения НЕ делаем continue
                // а продолжаем выполнение тика
                if (mp.WaitingSlConfirm)
                    goto SkipDetector;

                // Детектор ручного изменения — только SL
                if (DetectManualSlChange(pos, mp))
                {
                    mp.HandedOver = true;
                    WriteLog($"[{VERSION}] ✋ [{symbol.Name}] SL изменён вручную — автоматика отключена.");
                    continue;
                }

SkipDetector:

                double current = last.Price;
                double pnlPct = mp.IsLong
                    ? (current - mp.EntryPrice) / mp.EntryPrice * 100.0
                    : (mp.EntryPrice - current) / mp.EntryPrice * 100.0;

                // Фаза 2: Безубыток
                if (!mp.BeApplied && pnlPct >= BeTriggerPct)
                {
                    double newSl = RoundToTick(
                        CalcPrice(mp.EntryPrice, mp.IsLong, +BeLockPct),
                        pos.Symbol.TickSize);
                    WriteLog($"[{VERSION}] 🎯 [{symbol.Name}] Безубыток: PnL={pnlPct:F3}% → SL={newSl:F6}");
                    if (ModifySL(pos, mp, newSl))
                    {
                        mp.BeApplied = true;
                        mp.TrailBestPrice = current;
                    }
                }

                // Фаза 3: Активация трейлинга
                if (mp.BeApplied && !mp.TrailActive && pnlPct >= TrailTriggerPct)
                {
                    mp.TrailActive = true;
                    mp.TrailBestPrice = current;
                    WriteLog($"[{VERSION}] 🚀 [{symbol.Name}] Трейлинг активирован @ {current:F6}");
                }

                // Трейлинг
                if (mp.TrailActive)
                {
                    bool newExtreme = mp.IsLong
                        ? current > mp.TrailBestPrice
                        : current < mp.TrailBestPrice;
                    if (newExtreme)
                    {
                        mp.TrailBestPrice = current;
                        double newSl = RoundToTick(
                            CalcPrice(current, mp.IsLong, -TrailDistPct),
                            pos.Symbol.TickSize);
                        WriteLog($"[{VERSION}] 📈 [{symbol.Name}] Трейлинг: {current:F6} → SL={newSl:F6}");
                        ModifySL(pos, mp, newSl);
                    }
                }
            }
        }

        private bool ModifySL(Position pos, ManagedPosition mp, double newSlPrice)
        {
            if (string.IsNullOrEmpty(mp.SlOrderId))
            {
                WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] SlOrderId пустой.");
                return false;
            }
            var order = FindOrderById(mp.SlOrderId);
            if (order == null)
            {
                WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] SL ID:{mp.SlOrderId} не найден.");
                return false;
            }
            WriteLog($"[{VERSION}] 📤 [{pos.Symbol.Name}] ModifySL: {mp.SlPrice:F6} → {newSlPrice:F6}");
            var result = Core.Instance.ModifyOrder(order, triggerPrice: newSlPrice);
            WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] ModifySL: Status={result.Status} Msg={result.Message ?? "OK"}");
            if (result.Status == TradingOperationResultStatus.Success)
            {
                mp.PendingSlPrice = newSlPrice;
                mp.WaitingSlConfirm = true;
                // Обновить ID — биржа создаёт новый ордер после ModifyOrder
                System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ =>
                {
                    if (!_managed.ContainsKey(pos.Id)) return;
                    // Найти новый SL ордер по символу, аккаунту и комментарию
                    var newSlOrder = FindOrderByComment(pos.Symbol, pos.Account, $"SL_{pos.Id}");
                    if (newSlOrder != null && newSlOrder.Id != mp.SlOrderId)
                    {
                        WriteLog($"[{VERSION}] 🔄 [{pos.Symbol.Name}] SL ID обновлён: {mp.SlOrderId} → {newSlOrder.Id}");
                        mp.SlOrderId = newSlOrder.Id;
                    }
                    // Обновить подтверждённую цену
                    var slOrder = FindOrderById(mp.SlOrderId);
                    if (slOrder != null)
                    {
                        mp.SlPrice = slOrder.TriggerPrice;
                        mp.WaitingSlConfirm = false;
                        WriteLog($"[{VERSION}] 📌 [{pos.Symbol.Name}] SL подтверждён биржей: {mp.SlPrice:F6} ID:{mp.SlOrderId}");
                    }
                });
                return true;
            }
            WriteLog($"[{VERSION}] ❌ [{pos.Symbol.Name}] ModifySL ошибка: {result.Message}");
            return false;
        }

        // ИСПРАВЛЕНИЕ v4.3: детектор проверяет ТОЛЬКО SL, TP игнорируется
        private bool DetectManualSlChange(Position pos, ManagedPosition mp)
        {
            double tolerance = pos.Symbol.TickSize * 2;

            var slOrder = FindOrderById(mp.SlOrderId);

            // SL удалён вручную
            if (slOrder == null)
            {
                WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] SL ID:{mp.SlOrderId} исчез — ручное удаление.");
                return true;
            }

            bool slChanged = Math.Abs(slOrder.TriggerPrice - mp.SlPrice) > tolerance;
            if (slChanged)
                WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] SL изменён вручную: {mp.SlPrice:F6}→{slOrder.TriggerPrice:F6}");

            return slChanged;
        }

        private void InitLogFile()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string fileName = $"ScalpingManager_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log";
                string path = Path.Combine(dir, fileName);
                _logFile = new StreamWriter(path, append: true) { AutoFlush = true };
                Log($"[{VERSION}] 📝 Лог-файл: {path}", StrategyLoggingLevel.Trading);
            }
            catch (Exception ex)
            {
                Log($"[{VERSION}] ⚠ Лог-файл не создан: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        private void CloseLogFile()
        {
            try { _logFile?.Close(); _logFile = null; }
            catch { }
        }

        private void WriteLog(string message, StrategyLoggingLevel level = StrategyLoggingLevel.Trading)
        {
            Log(message, level);
            try { _logFile?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}"); }
            catch { }
        }

        private double RoundToTick(double price, double tickSize)
        {
            if (tickSize <= 0) return price;
            return Math.Round(price / tickSize) * tickSize;
        }

        private double CalcPrice(double basePrice, bool isLong, double pct) =>
            isLong ? basePrice * (1.0 + pct / 100.0)
                   : basePrice * (1.0 - pct / 100.0);

        private Order FindOrderById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var o in Core.Instance.Orders)
                if (o.Id == id) return o;
            return null;
        }

        private Position FindPositionById(string id)
        {
            foreach (var p in Core.Instance.Positions)
                if (p.Id == id) return p;
            return null;
        }
        private Order FindOrderByComment(Symbol symbol, Account account, string comment)
        {
            foreach (var o in Core.Instance.Orders)
                if (o.Symbol == symbol &&
                    o.Account == account &&
                    o.Comment == comment)
                    return o;
            return null;
        }
    }
}