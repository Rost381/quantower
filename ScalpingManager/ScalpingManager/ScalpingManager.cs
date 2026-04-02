using System;
using System.Collections.Generic;
using System.IO;
using TradingPlatform.BusinessLayer;

namespace ScalpingManager
{
    /// <summary>
    /// ScalpingManager v5.1 — Production Release
    ///
    /// Архитектура SL:
    ///   - Cancel старого SL + PlaceOrder нового = надёжное обновление ID
    ///   - Throttle 300ms предотвращает спам на биржу
    ///   - Детектор: если внешний Stop ордер исчез или изменился — HandedOver
    ///
    /// Совместимость: Binance Futures Hedge+One-way, Bybit
    /// </summary>
    public class ScalpingManager : Strategy
    {
        private const string VERSION = "v5.1";

        // ── Параметры ──────────────────────────────────────────────────
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

        // ── Структура данных позиции ───────────────────────────────────
        private class ManagedPosition
        {
            public string PositionId;
            public string SlOrderId;        // актуальный ID SL
            public string TpOrderId;        // ID TP (не меняется)
            public double EntryPrice;
            public double SlPrice;          // последняя известная цена SL
            public double TpPrice;
            public bool IsLong;
            public bool PricesConfirmed;  // биржа подтвердила начальные ордера
            public bool BeApplied;
            public bool TrailActive;
            public bool HandedOver;       // передано в ручное управление
            public double TrailBestPrice;
            public DateTime LastSlUpdate;     // throttle
        }

        private readonly Dictionary<string, ManagedPosition> _managed = new();
        private readonly HashSet<string> _preExisting = new();
        private readonly Dictionary<string, string> _loggedSymbols = new();

        private StreamWriter _logFile;

        // ── КОНСТРУКТОР — обязателен для регистрации в Quantower ───────
        public ScalpingManager() : base()
        {
            this.Name = $"ScalpingManager {VERSION}";
            this.Description = "SL/TP/Breakeven/Trailing. Hedge+One-way. Binance/Bybit.";
        }

        // ── Запуск ─────────────────────────────────────────────────────
        protected override void OnRun()
        {
            try
            {
                if (account == null)
                {
                    Log($"[{VERSION}] ❌ Account не выбран!", StrategyLoggingLevel.Error);
                    Stop();
                    return;
                }

                InitLogFile();
                _managed.Clear();
                _preExisting.Clear();
                _loggedSymbols.Clear();

                // Заморозить позиции открытые ДО запуска
                foreach (var pos in Core.Instance.Positions)
                {
                    if (pos.Account == account)
                    {
                        _preExisting.Add(pos.Id);
                        WriteLog($"[{VERSION}] ⏭ Pre-existing: [{pos.Symbol.Name}] {pos.Side} @ {pos.OpenPrice:F6} ID:{pos.Id}");
                    }
                }

                Core.PositionAdded += OnPositionAdded;
                Core.PositionRemoved += OnPositionRemoved;

                WriteLog($"[{VERSION}] ✅ Запущен | Аккаунт: {account.Name}");
                WriteLog($"[{VERSION}] ⚙ SL:{SlPct}% TP:{TpPct}% BE@{BeTriggerPct}%→+{BeLockPct}% Trail@{TrailTriggerPct}%±{TrailDistPct}%");
                WriteLog($"[{VERSION}] 🔒 Заморожено позиций: {_preExisting.Count}");
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ OnRun exception: {ex}");
            }
        }

        // ── Остановка ──────────────────────────────────────────────────
        protected override void OnStop()
        {
            Core.PositionAdded -= OnPositionAdded;
            Core.PositionRemoved -= OnPositionRemoved;

            // Отписываемся только от управляемых символов
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
            _loggedSymbols.Clear();

            WriteLog($"[{VERSION}] ■ Остановлен.");
            CloseLogFile();
        }

        // ── Новая позиция ──────────────────────────────────────────────
        private void OnPositionAdded(Position pos)
        {
            try
            {
                if (pos.Account != account) return;
                if (_preExisting.Contains(pos.Id)) return;
                if (_managed.ContainsKey(pos.Id))
                {
                    // Фильтр дублей — логируем только первый раз
                    if (!_loggedSymbols.ContainsKey(pos.Symbol.Name) ||
                        _loggedSymbols[pos.Symbol.Name] != pos.Id)
                    {
                        WriteLog($"[{VERSION}] ⏭ [{pos.Symbol.Name}] Уже в управлении ID:{pos.Id}");
                        _loggedSymbols[pos.Symbol.Name] = pos.Id;
                    }
                    return;
                }

                WriteLog($"[{VERSION}] 📥 New: [{pos.Symbol.Name}] {pos.Side} @ {pos.OpenPrice:F6} Qty:{pos.Quantity} ID:{pos.Id}");
                _loggedSymbols[pos.Symbol.Name] = pos.Id;

                bool isLong = pos.Side == Side.Buy;
                double tick = pos.Symbol.TickSize;
                double slPrice = RoundToTick(CalcPrice(pos.OpenPrice, isLong, -SlPct), tick);
                double tpPrice = RoundToTick(CalcPrice(pos.OpenPrice, isLong, +TpPct), tick);

                WriteLog($"[{VERSION}] 📐 [{pos.Symbol.Name}] Tick:{tick} SL:{slPrice:F6} TP:{tpPrice:F6}");

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
                    LastSlUpdate = DateTime.MinValue
                };

                _managed[pos.Id] = mp;
                pos.Symbol.NewLast += OnNewLast;

                PlaceInitialOrders(pos, mp);
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ OnPositionAdded exception: {ex}");
            }
        }

        // ── Выставить начальные SL и TP ───────────────────────────────
        private void PlaceInitialOrders(Position pos, ManagedPosition mp)
        {
            Side closeSide = mp.IsLong ? Side.Sell : Side.Buy;

            // SL
            WriteLog($"[{VERSION}] 📤 [{pos.Symbol.Name}] SL Stop@{mp.SlPrice:F6} Side:{closeSide} Qty:{pos.Quantity}");
            var slResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol = pos.Symbol,
                Account = pos.Account,
                Side = closeSide,
                OrderTypeId = OrderType.Stop,
                TriggerPrice = mp.SlPrice,
                Quantity = pos.Quantity,
                TimeInForce = TimeInForce.GTC,
                PositionId = pos.Id
            });
            WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] SL: {slResult.Status} {slResult.Message ?? "OK"} ID:{slResult.OrderId ?? "null"}");
            if (slResult.Status == TradingOperationResultStatus.Success)
                mp.SlOrderId = slResult.OrderId;
            else
                WriteLog($"[{VERSION}] ❌ [{pos.Symbol.Name}] SL ОШИБКА: {slResult.Message}");

            // TP
            WriteLog($"[{VERSION}] 📤 [{pos.Symbol.Name}] TP Limit@{mp.TpPrice:F6} Side:{closeSide} Qty:{pos.Quantity}");
            var tpResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol = pos.Symbol,
                Account = pos.Account,
                Side = closeSide,
                OrderTypeId = OrderType.Limit,
                Price = mp.TpPrice,
                Quantity = pos.Quantity,
                TimeInForce = TimeInForce.GTC,
                PositionId = pos.Id
            });
            WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] TP: {tpResult.Status} {tpResult.Message ?? "OK"} ID:{tpResult.OrderId ?? "null"}");
            if (tpResult.Status == TradingOperationResultStatus.Success)
                mp.TpOrderId = tpResult.OrderId;
            else
                WriteLog($"[{VERSION}] ❌ [{pos.Symbol.Name}] TP ОШИБКА: {tpResult.Message}");

            // Подтвердить реальные цены через 2 сек
            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
            {
                if (!_managed.ContainsKey(pos.Id)) return;
                var slOrder = FindOrder(mp.SlOrderId);
                var tpOrder = FindOrder(mp.TpOrderId);
                if (slOrder != null) { mp.SlPrice = slOrder.TriggerPrice; WriteLog($"[{VERSION}] 📌 [{pos.Symbol.Name}] SL confirmed:{mp.SlPrice:F6} ID:{mp.SlOrderId}"); }
                else WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] SL not found after 2s (ID:{mp.SlOrderId})");
                if (tpOrder != null) { mp.TpPrice = tpOrder.Price; WriteLog($"[{VERSION}] 📌 [{pos.Symbol.Name}] TP confirmed:{mp.TpPrice:F6} ID:{mp.TpOrderId}"); }
                else WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] TP not found after 2s (ID:{mp.TpOrderId})");
                mp.PricesConfirmed = true;
                WriteLog($"[{VERSION}] ✅ [{pos.Symbol.Name}] Ready. SL={mp.SlPrice:F6} TP={mp.TpPrice:F6}");
            });
        }

        // ── Позиция закрыта ────────────────────────────────────────────
        private void OnPositionRemoved(Position pos)
        {
            try
            {
                WriteLog($"[{VERSION}] 📥 Removed: [{pos.Symbol.Name}] ID:{pos.Id}");
                if (!_managed.ContainsKey(pos.Id))
                {
                    WriteLog($"[{VERSION}] ℹ [{pos.Symbol.Name}] Not managed.");
                    return;
                }
                var mp = _managed[pos.Id];
                pos.Symbol.NewLast -= OnNewLast;
                CancelOrder(mp.SlOrderId, pos.Symbol.Name, "SL");
                CancelOrder(mp.TpOrderId, pos.Symbol.Name, "TP");
                _managed.Remove(pos.Id);
                _loggedSymbols.Remove(pos.Symbol.Name);
                WriteLog($"[{VERSION}] ■ [{pos.Symbol.Name}] Closed & cleaned.");
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ OnPositionRemoved exception: {ex}");
            }
        }

        // ── Каждый тик ────────────────────────────────────────────────
        private void OnNewLast(Symbol symbol, Last last)
        {
            try
            {
                foreach (var pos in Core.Instance.Positions)
                {
                    if (pos.Account != account) continue;
                    if (pos.Symbol != symbol) continue;
                    if (!_managed.ContainsKey(pos.Id)) continue;

                    var mp = _managed[pos.Id];
                    if (!mp.PricesConfirmed) continue;
                    if (mp.HandedOver) continue;

                    // Детектор ручного изменения SL
                    if (DetectManualSlChange(pos, mp))
                    {
                        mp.HandedOver = true;
                        WriteLog($"[{VERSION}] ✋ [{symbol.Name}] Manual SL change — auto disabled.");
                        continue;
                    }

                    double price = last.Price;
                    double pnlPct = mp.IsLong
                        ? (price - mp.EntryPrice) / mp.EntryPrice * 100.0
                        : (mp.EntryPrice - price) / mp.EntryPrice * 100.0;

                    // Фаза 2: Безубыток
                    if (!mp.BeApplied && pnlPct >= BeTriggerPct)
                    {
                        double newSl = RoundToTick(
                            CalcPrice(mp.EntryPrice, mp.IsLong, +BeLockPct),
                            symbol.TickSize);
                        WriteLog($"[{VERSION}] 🎯 [{symbol.Name}] BE: PnL={pnlPct:F3}% → SL={newSl:F6}");
                        if (ReplaceSL(pos, mp, newSl))
                        {
                            mp.BeApplied = true;
                            mp.TrailBestPrice = price;
                        }
                    }

                    // Фаза 3: Активация трейлинга
                    if (mp.BeApplied && !mp.TrailActive && pnlPct >= TrailTriggerPct)
                    {
                        mp.TrailActive = true;
                        mp.TrailBestPrice = price;
                        WriteLog($"[{VERSION}] 🚀 [{symbol.Name}] Trail activated @ {price:F6}");
                    }

                    // Трейлинг
                    if (mp.TrailActive)
                    {
                        bool newExtreme = mp.IsLong
                            ? price > mp.TrailBestPrice
                            : price < mp.TrailBestPrice;

                        if (newExtreme)
                        {
                            mp.TrailBestPrice = price;
                            double newSl = RoundToTick(
                                CalcPrice(price, mp.IsLong, -TrailDistPct),
                                symbol.TickSize);
                            WriteLog($"[{VERSION}] 📈 [{symbol.Name}] Trail: {price:F6} → SL={newSl:F6}");
                            ReplaceSL(pos, mp, newSl);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ OnNewLast exception: {ex}");
            }
        }

        // ── Заменить SL: Cancel + PlaceOrder ──────────────────────────
        // Единственный надёжный способ — не ModifyOrder.
        // Binance после Modify создаёт новый ордер с новым ID,
        // поэтому проще явно Cancel + Place и сразу получить новый ID.
        private bool ReplaceSL(Position pos, ManagedPosition mp, double newPrice)
        {
            try
            {
                // Throttle — не спамить биржу чаще 300ms
                if ((DateTime.UtcNow - mp.LastSlUpdate).TotalMilliseconds < 300)
                    return false;

                mp.LastSlUpdate = DateTime.UtcNow;

                Side closeSide = mp.IsLong ? Side.Sell : Side.Buy;

                // Отменить старый SL
                CancelOrder(mp.SlOrderId, pos.Symbol.Name, "SL(replace)");

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

                WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] ReplaceSL@{newPrice:F6}: {result.Status} {result.Message ?? "OK"} ID:{result.OrderId ?? "null"}");

                if (result.Status != TradingOperationResultStatus.Success)
                    return false;

                // ID обновляется сразу — нет проблем с асинхронностью
                mp.SlOrderId = result.OrderId;
                mp.SlPrice = newPrice;
                return true;
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ ReplaceSL exception: {ex}");
                return false;
            }
        }

        // ── Детектор ручного изменения SL ─────────────────────────────
        private bool DetectManualSlChange(Position pos, ManagedPosition mp)
        {
            if (string.IsNullOrEmpty(mp.SlOrderId)) return false;

            var slOrder = FindOrder(mp.SlOrderId);

            // SL ордер исчез — не из-за нашего кода
            if (slOrder == null)
            {
                WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] SL ID:{mp.SlOrderId} gone — manual remove?");
                return true;
            }

            // Цена изменилась не нами
            double tolerance = pos.Symbol.TickSize * 2;
            bool changed = Math.Abs(slOrder.TriggerPrice - mp.SlPrice) > tolerance;

            if (changed)
                WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] SL price changed manually: {mp.SlPrice:F6}→{slOrder.TriggerPrice:F6}");

            return changed;
        }

        // ── Отменить ордер ─────────────────────────────────────────────
        private void CancelOrder(string id, string symbol, string label)
        {
            if (string.IsNullOrEmpty(id)) return;
            var order = FindOrder(id);
            if (order == null)
            {
                WriteLog($"[{VERSION}] ℹ [{symbol}] {label} ID:{id} not found (already filled?).");
                return;
            }
            WriteLog($"[{VERSION}] 📤 [{symbol}] Cancel {label} ID:{id}");
            var r = order.Cancel();
            WriteLog($"[{VERSION}] 📨 [{symbol}] Cancel {label}: {r.Status} {r.Message ?? "OK"}");
        }

        // ── Вспомогательные ───────────────────────────────────────────
        private Order FindOrder(string id)
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

        private double RoundToTick(double price, double tick) =>
            tick > 0 ? Math.Round(price / tick) * tick : price;

        private double CalcPrice(double basePrice, bool isLong, double pct) =>
            isLong
                ? basePrice * (1.0 + pct / 100.0)
                : basePrice * (1.0 - pct / 100.0);

        // ── Лог-файл ──────────────────────────────────────────────────
        private void InitLogFile()
        {
            try
            {
                string path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    $"ScalpingManager_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
                _logFile = new StreamWriter(path) { AutoFlush = true };
                WriteLog($"[{VERSION}] 📝 Log: {path}");
            }
            catch (Exception ex)
            {
                Log($"[{VERSION}] ⚠ Log file error: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        private void CloseLogFile()
        {
            try { _logFile?.Close(); _logFile = null; }
            catch { }
        }

        private void WriteLog(string msg, StrategyLoggingLevel level = StrategyLoggingLevel.Trading)
        {
            Log(msg, level);
            try { _logFile?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}"); }
            catch { }
        }
    }
}