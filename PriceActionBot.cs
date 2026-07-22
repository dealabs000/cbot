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

        [Parameter("Group Take Profit ($)", DefaultValue = 2.0)]
        public double GroupTakeProfit { get; set; }

        [Parameter("Group Stop Loss ($)", DefaultValue = 5.0)]
        public double GroupStopLoss { get; set; }

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

            fastMa = Indicators.MovingAverage(MarketSeries.Close, FastPeriod, MovingAverageType.Simple);
            slowMa = Indicators.MovingAverage(MarketSeries.Close, SlowPeriod, MovingAverageType.Simple);
        }

        protected override void OnTick()
        {
            CheckGroupExit();
        }

        protected override void OnBar()
        {
            if (HasOpenGroup()) return;
            if (MarketSeries.Close.Count < SlowPeriod + 2) return;

            int idx = MarketSeries.Close.Count - 1;

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

            Print("Opened {0} entries, direction={1}", NumEntries, direction);
        }

        private bool HasOpenGroup()
        {
            foreach (var position in Positions)
            {
                if (position.Label == Label) return true;
            }
            return false;
        }

        private void CheckGroupExit()
        {
            double totalProfit = 0;
            bool hasPositions = false;

            foreach (var position in Positions)
            {
                if (position.Label != Label) continue;
                hasPositions = true;
                totalProfit += position.NetProfit;
            }

            if (!hasPositions) return;

            if (totalProfit >= GroupTakeProfit || totalProfit <= -GroupStopLoss)
            {
                CloseAllGroupPositions();
                Print("Group closed. Total P/L: {0:F2}", totalProfit);
            }
        }

        private void CloseAllGroupPositions()
        {
            foreach (var position in Positions)
            {
                if (position.Label == Label)
                {
                    ClosePosition(position);
                }
            }
        }

        private void GetZone(int idx, out double zoneHigh, out double zoneLow)
        {
            int start = Math.Max(0, idx - ZoneLookback);
            zoneHigh = double.MinValue;
            zoneLow = double.MaxValue;

            for (int i = start; i <= idx; i++)
            {
                if (MarketSeries.High[i] > zoneHigh) zoneHigh = MarketSeries.High[i];
                if (MarketSeries.Low[i] < zoneLow) zoneLow = MarketSeries.Low[i];
            }
        }

        private bool IsFakeout(int idx, double zoneHigh, double zoneLow, string direction)
        {
            if (idx < 0) return false;

            double open = MarketSeries.Open[idx];
            double close = MarketSeries.Close[idx];
            double high = MarketSeries.High[idx];
            double low = MarketSeries.Low[idx];
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