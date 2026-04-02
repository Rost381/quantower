// ═══════════════════════════════════════════════════════════════════
// SafeBreakeven v1.0
// Задача: при открытии нового ордера выставить TP/SL,
// при достижении BE trigger % перенести SL на BE lock %,
// после чего "заморозить" позицию (больше не трогать).
// Поддержка Hedge Mode: по одному символу могут быть Long и Short.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.IO;
using TradingPlatform.BusinessLayer;

namespace SafeBreakeven
{
    public class SafeBreakeven : Strategy
    {
        private const string VERSION = "v1.0";

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

        // ── Состояние позиции ──────────────────────────────────────────
        private class ManagedPos
        {
            public string PositionId;
            public string SlOrderId;
            public string TpOrderId;
            public double EntryPrice;
            public double SlPrice;
            public double TpPrice;
            public bool IsLong;
            public bool PricesConfirmed; // биржа подтвердила ордера
            public bool BeApplied;       // безубыток применён → позиция заморожена
            public DateTime LastSlUpdate;  // throttle для ReplaceSL
        }

        // Ключ — Position.Id (уникален для каждой позиции в Hedge Mode)
        private readonly Dictionary<string, ManagedPos> _managed = new();
        // Позиции открытые ДО запуска + замороженные после BE — не трогать
        private readonly HashSet<string> _frozen = new();
        // Фильтр дублей PositionAdded от Binance (шлёт повторно каждые ~11 сек)
        private readonly Dictionary<string, string> _loggedSymbols = new();

        private StreamWriter _logFile;

        // ── Конструктор — обязателен для регистрации в Quantower ───────
        public SafeBreakeven() : base()
        {
            this.Name = $"SafeBreakeven {VERSION}";
            this.Description = "Places SL/TP on new positions, moves SL to breakeven, then freezes.";
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
                _frozen.Clear();
                _loggedSymbols.Clear();

                // Заморозить ВСЕ позиции открытые до запуска
                foreach (var pos in Core.Instance.Positions)
                {
                    if (pos.Account == account)
                    {
                        _frozen.Add(pos.Id);
                        WriteLog($"[{VERSION}] ⏭ Pre-existing frozen: [{pos.Symbol.Name}] {pos.Side} @ {pos.OpenPrice:F6} ID:{pos.Id}");
                    }
                }

                Core.PositionAdded += OnPositionAdded;
                Core.PositionRemoved += OnPositionRemoved;

                WriteLog($"[{VERSION}] ✅ SafeBreakeven запущен | Аккаунт: {account.Name}");
                WriteLog($"[{VERSION}] ⚙ SL:{SlPct}% | TP:{TpPct}% | BE@{BeTriggerPct}%→+{BeLockPct}%");
                WriteLog($"[{VERSION}] 🔒 Заморожено: {_frozen.Count} поз.");
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

            // Отписываемся только от управляемых позиций
            foreach (var kvp in _managed)
            {
                var pos = FindPositionById(kvp.Key);
                if (pos != null)
                {
                    pos.Symbol.NewLast -= OnNewLast;
                    WriteLog($"[{VERSION}] 🔌 Отписка тиков: [{pos.Symbol.Name}] {pos.Side}");
                }
            }

            _managed.Clear();
            _frozen.Clear();
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
                if (_frozen.Contains(pos.Id)) return; // заморожена
                if (_managed.ContainsKey(pos.Id))
                {
                    // Binance шлёт повторные события — логируем один раз
                    string key = $"{pos.Symbol.Name}_{pos.Side}";
                    if (!_loggedSymbols.ContainsKey(key) || _loggedSymbols[key] != pos.Id)
                    {
                        WriteLog($"[{VERSION}] ⏭ [{pos.Symbol.Name}] {pos.Side} уже управляется ID:{pos.Id}");
                        _loggedSymbols[key] = pos.Id;
                    }
                    return;
                }

                WriteLog($"[{VERSION}] 📥 Новая позиция: [{pos.Symbol.Name}] {pos.Side} @ {pos.OpenPrice:F6} Qty:{pos.Quantity} ID:{pos.Id}");

                bool isLong = pos.Side == Side.Buy;
                double tick = pos.Symbol.TickSize;
                double slPrice = RoundToTick(CalcPrice(pos.OpenPrice, isLong, -SlPct), tick);
                double tpPrice = RoundToTick(CalcPrice(pos.OpenPrice, isLong, +TpPct), tick);

                WriteLog($"[{VERSION}] 📐 [{pos.Symbol.Name}] Tick:{tick} Entry:{pos.OpenPrice:F6} SL:{slPrice:F6} TP:{tpPrice:F6}");

                var mp = new ManagedPos
                {
                    PositionId = pos.Id,
                    EntryPrice = pos.OpenPrice,
                    IsLong = isLong,
                    SlPrice = slPrice,
                    TpPrice = tpPrice,
                    PricesConfirmed = false,
                    BeApplied = false,
                    LastSlUpdate = DateTime.MinValue
                };

                _managed[pos.Id] = mp;
                pos.Symbol.NewLast += OnNewLast;

                PlaceInitialOrders(pos, mp);
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ OnPositionAdded: {ex}");
            }
        }

        // ── Выставить начальные SL и TP ───────────────────────────────
        private void PlaceInitialOrders(Position pos, ManagedPos mp)
        {
            Side closeSide = mp.IsLong ? Side.Sell : Side.Buy;

            // SL
            WriteLog($"[{VERSION}] 📤 [{pos.Symbol.Name}] SL Stop@{mp.SlPrice:F6} {closeSide} Qty:{pos.Quantity}");
            var slRes = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
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
            WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] SL: {slRes.Status} {slRes.Message ?? "OK"} ID:{slRes.OrderId ?? "null"}");
            if (slRes.Status == TradingOperationResultStatus.Success)
                mp.SlOrderId = slRes.OrderId;
            else
                WriteLog($"[{VERSION}] ❌ [{pos.Symbol.Name}] SL ОШИБКА: {slRes.Message}");

            // TP
            WriteLog($"[{VERSION}] 📤 [{pos.Symbol.Name}] TP Limit@{mp.TpPrice:F6} {closeSide} Qty:{pos.Quantity}");
            var tpRes = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
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
            WriteLog($"[{VERSION}] 📨 [{pos.Symbol.Name}] TP: {tpRes.Status} {tpRes.Message ?? "OK"} ID:{tpRes.OrderId ?? "null"}");
            if (tpRes.Status == TradingOperationResultStatus.Success)
                mp.TpOrderId = tpRes.OrderId;
            else
                WriteLog($"[{VERSION}] ❌ [{pos.Symbol.Name}] TP ОШИБКА: {tpRes.Message}");

            // Подтвердить реальные цены от биржи через 2 сек
            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
            {
                if (!_managed.ContainsKey(pos.Id)) return;
                var sl = FindOrder(mp.SlOrderId);
                var tp = FindOrder(mp.TpOrderId);
                if (sl != null) { mp.SlPrice = sl.TriggerPrice; WriteLog($"[{VERSION}] 📌 [{pos.Symbol.Name}] SL подтверждён: {mp.SlPrice:F6}"); }
                else WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] SL не найден после 2с ID:{mp.SlOrderId}");
                if (tp != null) { mp.TpPrice = tp.Price; WriteLog($"[{VERSION}] 📌 [{pos.Symbol.Name}] TP подтверждён: {mp.TpPrice:F6}"); }
                else WriteLog($"[{VERSION}] ⚠ [{pos.Symbol.Name}] TP не найден после 2с ID:{mp.TpOrderId}");
                mp.PricesConfirmed = true;
                WriteLog($"[{VERSION}] ✅ [{pos.Symbol.Name}] {pos.Side} готов. SL={mp.SlPrice:F6} TP={mp.TpPrice:F6}");
            });
        }

        // ── Позиция закрыта ────────────────────────────────────────────
        private void OnPositionRemoved(Position pos)
        {
            try
            {
                WriteLog($"[{VERSION}] 📥 Closed: [{pos.Symbol.Name}] {pos.Side} ID:{pos.Id}");

                // Убрать из frozen если там была
                _frozen.Remove(pos.Id);

                if (!_managed.ContainsKey(pos.Id))
                {
                    WriteLog($"[{VERSION}] ℹ [{pos.Symbol.Name}] {pos.Side} не управлялась.");
                    return;
                }

                var mp = _managed[pos.Id];
                pos.Symbol.NewLast -= OnNewLast;

                // OCO: отменить оставшийся ордер
                CancelOrder(mp.SlOrderId, pos.Symbol.Name, "SL");
                CancelOrder(mp.TpOrderId, pos.Symbol.Name, "TP");

                _managed.Remove(pos.Id);
                _loggedSymbols.Remove($"{pos.Symbol.Name}_{pos.Side}");

                WriteLog($"[{VERSION}] ■ [{pos.Symbol.Name}] {pos.Side} очищена.");
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ OnPositionRemoved: {ex}");
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

                    // Ждём подтверждения начальных ордеров
                    if (!mp.PricesConfirmed) continue;

                    // Безубыток уже применён → позиция заморожена
                    if (mp.BeApplied) continue;

                    double price = last.Price;
                    double pnlPct = mp.IsLong
                        ? (price - mp.EntryPrice) / mp.EntryPrice * 100.0
                        : (mp.EntryPrice - price) / mp.EntryPrice * 100.0;

                    // Применить безубыток при достижении триггера
                    if (pnlPct >= BeTriggerPct)
                    {
                        double newSl = RoundToTick(
                            CalcPrice(mp.EntryPrice, mp.IsLong, +BeLockPct),
                            symbol.TickSize);

                        WriteLog($"[{VERSION}] 🎯 [{symbol.Name}] {pos.Side} BE: PnL={pnlPct:F3}% → SL={newSl:F6}");

                        if (ReplaceSL(pos, mp, newSl))
                        {
                            mp.BeApplied = true;
                            // Заморозить — больше не управляем этой позицией
                            _frozen.Add(pos.Id);
                            WriteLog($"[{VERSION}] 🔒 [{symbol.Name}] {pos.Side} заморожена после BE. ID:{pos.Id}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ OnNewLast: {ex}");
            }
        }

        // ── Replace SL: Cancel + PlaceOrder ───────────────────────────
        // Надёжнее ModifyOrder: сразу получаем новый ID
        private bool ReplaceSL(Position pos, ManagedPos mp, double newPrice)
        {
            try
            {
                // Throttle — не спамить биржу
                if ((DateTime.UtcNow - mp.LastSlUpdate).TotalMilliseconds < 300)
                    return false;
                mp.LastSlUpdate = DateTime.UtcNow;

                Side closeSide = mp.IsLong ? Side.Sell : Side.Buy;

                CancelOrder(mp.SlOrderId, pos.Symbol.Name, "SL(replace)");

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

                // ID обновляется сразу — нет проблем с асинхронностью
                mp.SlOrderId = result.OrderId;
                mp.SlPrice = newPrice;
                return true;
            }
            catch (Exception ex)
            {
                WriteLog($"[{VERSION}] ❌ ReplaceSL: {ex}");
                return false;
            }
        }

        // ── Отменить ордер ─────────────────────────────────────────────
        private void CancelOrder(string id, string symbol, string label)
        {
            if (string.IsNullOrEmpty(id)) return;
            var order = FindOrder(id);
            if (order == null) { WriteLog($"[{VERSION}] ℹ [{symbol}] {label} ID:{id} не найден (исполнен?)."); return; }
            WriteLog($"[{VERSION}] 📤 [{symbol}] Cancel {label} ID:{id}");
            var r = order.Cancel();
            WriteLog($"[{VERSION}] 📨 [{symbol}] Cancel {label}: {r.Status} {r.Message ?? "OK"}");
        }

        private Order FindOrder(string id) { if (string.IsNullOrEmpty(id)) return null; foreach (var o in Core.Instance.Orders) if (o.Id == id) return o; return null; }
        private Position FindPositionById(string id) { foreach (var p in Core.Instance.Positions) if (p.Id == id) return p; return null; }
        private double RoundToTick(double p, double t) => t > 0 ? Math.Round(p / t) * t : p;
        private double CalcPrice(double b, bool l, double pct) => l ? b * (1 + pct / 100.0) : b * (1 - pct / 100.0);

        private void InitLogFile()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"SafeBreakeven_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
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