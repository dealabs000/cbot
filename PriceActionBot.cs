using System;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class XauScalpBot : Robot
    {
        [Parameter("Fast MA Period", DefaultValue = 3)]
        public int FastPeriod { get; set; }

        [Parameter("Slow MA Period", DefaultValue = 8)]
        public int SlowPeriod { get; set; }

        [Parameter("Zone Lookback", DefaultValue = 15)]
        public int ZoneLookback { get; set; }

        [Parameter("Wick Rejection Ratio", DefaultValue = 0.5)]
        public double WickRejectionRatio { get; set; }

        [Parameter("Lots Per Entry", DefaultValue = 0.02)]
        public double LotsPerEntry { get; set; }

        [Parameter("Number Of Entries", DefaultValue = 3)]
        public int NumEntries { get; set; }

        [Parameter("Take Profit Per Trade ($)", DefaultValue = 2.0)]
        public double TakeProfitPerTrade { get; set; }

        [Parameter("Stop Loss Per Trade ($)", DefaultValue = 5.0)]
        public double StopLossPerTrade { get; set; }

        private MovingAverage fastMa;
        private MovingAverage slowMa;
        private const string Label = "XauScalpBot";

        protected override void OnStart()
        {
            if (SymbolName != "XAUUSD")
            {
                Print("This bot only trades XAUUSD. Attach it to an XAUUSD M1 chart.");
                Stop();
                return;
            }

            fastMa = Indicators.MovingAverage(Bars.ClosePrices, FastPeriod, MovingAverageType.Simple);
            slowMa = Indicators.MovingAverage(Bars.ClosePrices, SlowPeriod, MovingAverageType.Simple);
        }

        protected override void OnTick()
        {
            CheckIndividualExits();
        }

        protected override void OnBar()
        {
            if (HasOpenPositions()) return;
            if (Bars.ClosePrices.Count < SlowPeriod + 2) return;

            int idx = Bars.ClosePrices.Count - 1;

            double fastNow = fastMa.Result[idx];
            double slowNow = slowMa.Result[idx];
            double fastPrev = fastMa.Result[idx - 1];
            double slowPrev = slowMa.Result[idx - 1];

            string direction = null;
            if (fastPrev <= slowPrev && fastNow > slowNow) direction = "up";
            else if (fastPrev >= slowPrev && fastNow < slowNow) direction = "down";

            if (direction == null) return;

            double zoneHigh, zoneLow;
            GetZone(idx, out zoneHigh, out zoneLow);

            if (IsFakeout(idx - 1, zoneHigh, zoneLow, direction))
            {
                Print("Fakeout detected on M1 — skipping entry.");
                return;
            }

            OpenTradeGroup(direction);
        }

        private void OpenTradeGroup(string direction)
        {
            var tradeType = direction == "up" ? TradeType.Buy : TradeType.Sell;
            var volumeInUnits = Symbol.QuantityToVolumeInUnits(LotsPerEntry);

            for (int i = 0; i < NumEntries; i++)
            {
                ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, Label);
            }

            Print("Opened {0} independent entries, direction={1}", NumEntries, direction);
        }

        private bool HasOpenPositions()
        {
            foreach (var position in Positions)
            {
                if (position.Label == Label) return true;
            }
            return false;
        }

        private void CheckIndividualExits()
        {
            // Snapshot first since ClosePosition modifies the collection while iterating
            var toClose = new System.Collections.Generic.List<Position>();

            foreach (var position in Positions)
            {
                if (position.Label != Label) continue;

                if (position.NetProfit >= TakeProfitPerTrade || position.NetProfit <= -StopLossPerTrade)
                {
                    toClose.Add(position);
                }
            }

            foreach (var position in toClose)
            {
                double pnl = position.NetProfit;
                ClosePosition(position);
                Print("Closed position {0}. P/L: {1:F2}", position.Id, pnl);
            }
        }

        private void GetZone(int idx, out double zoneHigh, out double zoneLow)
        {
            int start = Math.Max(0, idx - ZoneLookback);
            zoneHigh = double.MinValue;
            zoneLow = double.MaxValue;

            for (int i = start; i <= idx; i++)
            {
                if (Bars.HighPrices[i] > zoneHigh) zoneHigh = Bars.HighPrices[i];
                if (Bars.LowPrices[i] < zoneLow) zoneLow = Bars.LowPrices[i];
            }
        }

        private bool IsFakeout(int idx, double zoneHigh, double zoneLow, string direction)
        {
            if (idx < 0) return false;

            double open = Bars.OpenPrices[idx];
            double close = Bars.ClosePrices[idx];
            double high = Bars.HighPrices[idx];
            double low = Bars.LowPrices[idx];
            double range = high - low;

            if (range <= 0) return false;

            if (direction == "up")
            {
                double upperWick = high - Math.Max(open, close);
                bool brokeAbove = high > zoneHigh;
                bool closedBackInside = close < zoneHigh;
                return brokeAbove && closedBackInside && (upperWick / range) > WickRejectionRatio;
            }
            else
            {
                double lowerWick = Math.Min(open, close) - low;
                bool brokeBelow = low < zoneLow;
                bool closedBackInside = close > zoneLow;
                return brokeBelow && closedBackInside && (lowerWick / range) > WickRejectionRatio;
            }
        }
    }
}