using System;
using System.Collections.Generic;
using System.IO;
using TradingPlatform.BusinessLayer;

namespace ScalpingManager
{
    public class ScalpingManager : Strategy
    {
        private const string VERSION = "v5.0";

        [InputParameter("Account", 10)]
        public Account account;

        [InputParameter("Stop Loss %", 20, 0.1, 20, 0.1, 1)]
        public double SlPct = 2.4;

        [InputParameter("Take Profit %", 30, 0.1, 50, 0.1, 1)]
        public double TpPct = 5.0;

        [InputParameter("Breakeven trigger %", 40)]
        public double BeTriggerPct = 1.0;

        [InputParameter("Breakeven lock %", 50)]
        public double BeLockPct = 0.3;

        [InputParameter("Trailing trigger %", 60)]
        public double TrailTriggerPct = 2.0;

        [InputParameter("Trailing distance %", 70)]
        public double TrailDistPct = 1.0;

        private class ManagedPosition
        {
            public string PositionId;
            public string SlOrderId;
            public string TpOrderId;

            public double EntryPrice;
            public double SlPrice;
            public double TpPrice;

            public bool IsLong;

            public bool BeApplied;
            public bool TrailActive;
            public bool HandedOver;

            public double TrailBestPrice;
            public DateTime LastSlUpdate;
        }

        private readonly Dictionary<string, ManagedPosition> _managed = new();
        private readonly HashSet<string> _preExisting = new();

        private StreamWriter _logFile;

        protected override void OnRun()
        {
            try
            {
                if (account == null)
                {
                    Log("❌ Account not selected", StrategyLoggingLevel.Error);
                    Stop();
                    return;
                }

                InitLogFile();

                foreach (var pos in Core.Instance.Positions)
                {
                    if (pos.Account == account)
                        _preExisting.Add(pos.Id);
                }

                Core.PositionAdded += OnPositionAdded;
                Core.PositionRemoved += OnPositionRemoved;

                WriteLog($"[{VERSION}] Started. Account={account.Name}");
            }
            catch (Exception ex)
            {
                var error_traceback = ex.ToString();
                Log(error_traceback, StrategyLoggingLevel.Error);
            }
        }

        protected override void OnStop()
        {
            Core.PositionAdded -= OnPositionAdded;
            Core.PositionRemoved -= OnPositionRemoved;

            foreach (var pos in Core.Instance.Positions)
                pos.Symbol.NewLast -= OnNewLast;

            WriteLog($"[{VERSION}] Stopped");
            _logFile?.Close();
        }

        private void OnPositionAdded(Position pos)
        {
            try
            {
                if (pos.Account != account) return;
                if (_preExisting.Contains(pos.Id)) return;
                if (_managed.ContainsKey(pos.Id)) return;

                bool isLong = pos.Side == Side.Buy;
                double tick = pos.Symbol.TickSize;

                double sl = RoundToTick(CalcPrice(pos.OpenPrice, isLong, -SlPct), tick);
                double tp = RoundToTick(CalcPrice(pos.OpenPrice, isLong, +TpPct), tick);

                var mp = new ManagedPosition
                {
                    PositionId = pos.Id,
                    EntryPrice = pos.OpenPrice,
                    IsLong = isLong,
                    SlPrice = sl,
                    TpPrice = tp,
                    TrailBestPrice = pos.OpenPrice
                };

                _managed[pos.Id] = mp;
                pos.Symbol.NewLast += OnNewLast;

                PlaceInitialOrders(pos, mp);
            }
            catch (Exception ex)
            {
                var error_traceback = ex.ToString();
                WriteLog(error_traceback);
            }
        }

        private void PlaceInitialOrders(Position pos, ManagedPosition mp)
        {
            Side closeSide = mp.IsLong ? Side.Sell : Side.Buy;

            var sl = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol = pos.Symbol,
                Account = pos.Account,
                Side = closeSide,
                OrderTypeId = OrderType.Stop,
                TriggerPrice = mp.SlPrice,
                Quantity = pos.Quantity,
                PositionId = pos.Id
            });

            if (sl.Status == TradingOperationResultStatus.Success)
                mp.SlOrderId = sl.OrderId;

            var tp = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol = pos.Symbol,
                Account = pos.Account,
                Side = closeSide,
                OrderTypeId = OrderType.Limit,
                Price = mp.TpPrice,
                Quantity = pos.Quantity,
                PositionId = pos.Id
            });

            if (tp.Status == TradingOperationResultStatus.Success)
                mp.TpOrderId = tp.OrderId;
        }

        private void OnPositionRemoved(Position pos)
        {
            if (!_managed.ContainsKey(pos.Id)) return;

            var mp = _managed[pos.Id];

            Cancel(mp.SlOrderId);
            Cancel(mp.TpOrderId);

            _managed.Remove(pos.Id);
        }

        private void OnNewLast(Symbol symbol, Last last)
        {
            foreach (var pos in Core.Instance.Positions)
            {
                if (pos.Account != account) continue;
                if (pos.Symbol != symbol) continue;
                if (!_managed.ContainsKey(pos.Id)) continue;

                var mp = _managed[pos.Id];
                if (mp.HandedOver) continue;

                double price = last.Price;

                double pnl = mp.IsLong
                    ? (price - mp.EntryPrice) / mp.EntryPrice * 100
                    : (mp.EntryPrice - price) / mp.EntryPrice * 100;

                // BE
                if (!mp.BeApplied && pnl >= BeTriggerPct)
                {
                    double newSl = RoundToTick(
                        CalcPrice(mp.EntryPrice, mp.IsLong, +BeLockPct),
                        symbol.TickSize);

                    if (ReplaceSL(pos, mp, newSl))
                    {
                        mp.BeApplied = true;
                        mp.TrailBestPrice = price;
                    }
                }

                // Activate trailing
                if (mp.BeApplied && !mp.TrailActive && pnl >= TrailTriggerPct)
                {
                    mp.TrailActive = true;
                    mp.TrailBestPrice = price;
                }

                // Trailing
                if (mp.TrailActive)
                {
                    bool better = mp.IsLong
                        ? price > mp.TrailBestPrice
                        : price < mp.TrailBestPrice;

                    if (!better) continue;

                    mp.TrailBestPrice = price;

                    double newSl = RoundToTick(
                        CalcPrice(price, mp.IsLong, -TrailDistPct),
                        symbol.TickSize);

                    ReplaceSL(pos, mp, newSl);
                }
            }
        }

        private bool ReplaceSL(Position pos, ManagedPosition mp, double newPrice)
        {
            try
            {
                // throttle
                if ((DateTime.UtcNow - mp.LastSlUpdate).TotalMilliseconds < 300)
                    return false;

                mp.LastSlUpdate = DateTime.UtcNow;

                Cancel(mp.SlOrderId);

                Side closeSide = mp.IsLong ? Side.Sell : Side.Buy;

                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                {
                    Symbol = pos.Symbol,
                    Account = pos.Account,
                    Side = closeSide,
                    OrderTypeId = OrderType.Stop,
                    TriggerPrice = newPrice,
                    Quantity = pos.Quantity,
                    PositionId = pos.Id
                });

                if (result.Status != TradingOperationResultStatus.Success)
                    return false;

                mp.SlOrderId = result.OrderId;
                mp.SlPrice = newPrice;

                return true;
            }
            catch (Exception ex)
            {
                var error_traceback = ex.ToString();
                WriteLog(error_traceback);
                return false;
            }
        }

        private void Cancel(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            var order = FindOrder(id);
            order?.Cancel();
        }

        private Order FindOrder(string id)
        {
            foreach (var o in Core.Instance.Orders)
                if (o.Id == id) return o;
            return null;
        }

        private double RoundToTick(double price, double tick)
        {
            if (tick <= 0) return price;
            return Math.Round(price / tick) * tick;
        }

        private double CalcPrice(double basePrice, bool isLong, double pct)
        {
            return isLong
                ? basePrice * (1 + pct / 100.0)
                : basePrice * (1 - pct / 100.0);
        }

        private void InitLogFile()
        {
            try
            {
                string path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    $"ScalpingManager_{DateTime.Now:yyyyMMdd_HHmmss}.log");

                _logFile = new StreamWriter(path) { AutoFlush = true };
            }
            catch { }
        }

        private void WriteLog(string msg)
        {
            Log(msg);
            _logFile?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
        }
    }
}