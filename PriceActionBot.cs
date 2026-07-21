using System;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class PriceActionBot : Robot
    {
        [Parameter("Fast MA Period", DefaultValue = 5)]
        public int FastPeriod { get; set; }

        [Parameter("Slow MA Period", DefaultValue = 20)]
        public int SlowPeriod { get; set; }

        [Parameter("Zone Lookback", DefaultValue = 30)]
        public int ZoneLookback { get; set; }

        [Parameter("Wick Rejection Ratio", DefaultValue = 0.6)]
        public double WickRejectionRatio { get; set; }

        [Parameter("Volume (Lots)", DefaultValue = 0.01)]
        public double Volume { get; set; }

        [Parameter("Take Profit ($)", DefaultValue = 2.0)]
        public double TakeProfitMoney { get; set; }

        [Parameter("Stop Loss ($)", DefaultValue = 5.0)]
        public double StopLossMoney { get; set; }

        private MovingAverage fastMa;
        private MovingAverage slowMa;
        private const string Label = "PriceActionBot";

        protected override void OnStart()
        {
            fastMa = Indicators.MovingAverage(MarketSeries.Close, FastPeriod, MovingAverageType.Simple);
            slowMa = Indicators.MovingAverage(MarketSeries.Close, SlowPeriod, MovingAverageType.Simple);
        }

        protected override void OnTick()
        {
            foreach (var position in Positions)
            {
                if (position.Label != Label) continue;

                if (position.NetProfit >= TakeProfitMoney || position.NetProfit <= -StopLossMoney)
                {
                    ClosePosition(position);
                }
            }
        }

        protected override void OnBar()
        {
            if (Positions.Find(Label) != null) return; // already in a trade
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
                Print("Fakeout detected, skipping trade.");
                return;
            }

            var volumeInUnits = Symbol.QuantityToVolumeInUnits(Volume);

            if (direction == "up")
                ExecuteMarketOrder(TradeType.Buy, SymbolName, volumeInUnits, Label);
            else
                ExecuteMarketOrder(TradeType.Sell, SymbolName, volumeInUnits, Label);
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