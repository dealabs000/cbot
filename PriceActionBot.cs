using System;
using System.Collections.Generic;
using cAlgo.API;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class XauSpikeScalpBot : Robot
    {
        [Parameter("Spike Lookback (bars)", DefaultValue = 20)]
        public int SpikeLookback { get; set; }

        [Parameter("Spike Multiplier", DefaultValue = 1.8)]
        public double SpikeMultiplier { get; set; }

        [Parameter("Min Body Ratio", DefaultValue = 0.6)]
        public double MinBodyRatio { get; set; }

        [Parameter("Lots Per Entry", DefaultValue = 0.02)]
        public double LotsPerEntry { get; set; }

        [Parameter("Number Of Entries", DefaultValue = 10)]
        public int NumEntries { get; set; }

        [Parameter("Take Profit Per Trade ($)", DefaultValue = 1.0)]
        public double TakeProfitPerTrade { get; set; }

        [Parameter("Stop Loss Per Trade ($)", DefaultValue = 8.0)]
        public double StopLossPerTrade { get; set; }

        private const string Label = "XauSpikeScalpBot";

        protected override void OnStart()
        {
            if (SymbolName != "XAUUSD")
            {
                Print("This bot only trades XAUUSD. Attach it to an XAUUSD M1 chart.");
                Stop();
                return;
            }
        }

        protected override void OnTick()
        {
            // Manage exits every tick so $1 TP / $8 SL trigger instantly, per position
            CheckIndividualExits();

            // As soon as we're flat, immediately look for the next spike (don't wait for next bar close)
            if (!HasOpenPositions())
            {
                CheckForSpikeEntry();
            }
        }

        protected override void OnBar()
        {
            // Also check on each new bar close in case OnTick missed a fresh signal
            if (!HasOpenPositions())
            {
                CheckForSpikeEntry();
            }
        }

        private void CheckForSpikeEntry()
        {
            if (Bars.ClosePrices.Count < SpikeLookback + 2) return;

            int idx = Bars.ClosePrices.Count - 2; // last FULLY closed bar
            if (idx < SpikeLookback) return;

            double avgRange = 0;
            for (int i = idx - SpikeLookback; i < idx; i++)
            {
                avgRange += (Bars.HighPrices[i] - Bars.LowPrices[i]);
            }
            avgRange /= SpikeLookback;

            double open = Bars.OpenPrices[idx];
            double close = Bars.ClosePrices[idx];
            double high = Bars.HighPrices[idx];
            double low = Bars.LowPrices[idx];
            double range = high - low;

            if (range <= 0 || avgRange <= 0) return;

            bool isSpike = range >= avgRange * SpikeMultiplier;
            if (!isSpike) return;

            double body = Math.Abs(close - open);
            double bodyRatio = body / range;
            if (bodyRatio < MinBodyRatio) return; // too wicky, not a clean directional spike

            string direction = close > open ? "up" : "down";

            Print("Spike detected: range={0:F2} avgRange={1:F2} bodyRatio={2:F2} dir={3}",
                range, avgRange, bodyRatio, direction);

            OpenTradeBatch(direction);
        }

        private void OpenTradeBatch(string direction)
        {
            var tradeType = direction == "up" ? TradeType.Buy : TradeType.Sell;
            var volumeInUnits = Symbol.QuantityToVolumeInUnits(LotsPerEntry);

            for (int i = 0; i < NumEntries; i++)
            {
                ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, Label);
            }

            Print("Opened {0} spike-follow entries, direction={1}", NumEntries, direction);
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
            var toClose = new List<Position>();

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
    }
}