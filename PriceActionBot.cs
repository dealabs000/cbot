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

        [Parameter("Confirmation Distance (price)", DefaultValue = 0.15)]
        public double ConfirmationDistance { get; set; }

        [Parameter("Max Wait Bars For Confirmation", DefaultValue = 2)]
        public int MaxWaitBars { get; set; }

        [Parameter("Zone Lookback (bars)", DefaultValue = 40)]
        public int ZoneLookback { get; set; }

        [Parameter("Min Room To Zone (price)", DefaultValue = 0.8)]
        public double MinRoomToZone { get; set; }

        [Parameter("Lots Per Entry", DefaultValue = 0.02)]
        public double LotsPerEntry { get; set; }

        [Parameter("Number Of Entries", DefaultValue = 10)]
        public int NumEntries { get; set; }

        [Parameter("Take Profit Per Trade ($)", DefaultValue = 1.0)]
        public double TakeProfitPerTrade { get; set; }

        [Parameter("Stop Loss Per Trade ($)", DefaultValue = 8.0)]
        public double StopLossPerTrade { get; set; }

        private const string Label = "XauSpikeScalpBot";

        private bool waitingForConfirmation = false;
        private string pendingDirection = null;
        private double spikeClose = 0;
        private int barsWaited = 0;

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
            CheckIndividualExits();

            if (waitingForConfirmation)
            {
                CheckConfirmation();
            }
            else if (!HasOpenPositions())
            {
                CheckForSpikeEntry();
            }
        }

        protected override void OnBar()
        {
            if (waitingForConfirmation)
            {
                barsWaited++;
                if (barsWaited > MaxWaitBars)
                {
                    Print("Confirmation timed out — cancelling spike signal.");
                    ResetPendingSignal();
                }
            }
            else if (!HasOpenPositions())
            {
                CheckForSpikeEntry();
            }
        }

        private void CheckForSpikeEntry()
        {
            if (Bars.ClosePrices.Count < Math.Max(SpikeLookback, ZoneLookback) + 2) return;

            int idx = Bars.ClosePrices.Count - 2;
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
            if (bodyRatio < MinBodyRatio) return;

            string direction = close > open ? "up" : "down";

            // Supply/demand filter: make sure there's room to run before hitting the opposite zone
            double zoneHigh, zoneLow;
            GetZone(idx, out zoneHigh, out zoneLow);

            if (direction == "up")
            {
                double roomToSupply = zoneHigh - close;
                if (roomToSupply < MinRoomToZone)
                {
                    Print("Spike up but too close to supply zone ({0:F2} room) — skipping.", roomToSupply);
                    return;
                }
            }
            else
            {
                double roomToDemand = close - zoneLow;
                if (roomToDemand < MinRoomToZone)
                {
                    Print("Spike down but too close to demand zone ({0:F2} room) — skipping.", roomToDemand);
                    return;
                }
            }

            Print("Spike candidate: range={0:F2} avgRange={1:F2} bodyRatio={2:F2} dir={3} — waiting for confirmation",
                range, avgRange, bodyRatio, direction);

            waitingForConfirmation = true;
            pendingDirection = direction;
            spikeClose = close;
            barsWaited = 0;
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

        private void CheckConfirmation()
        {
            double currentPrice = pendingDirection == "up" ? Symbol.Bid : Symbol.Ask;

            if (pendingDirection == "up")
            {
                if (currentPrice >= spikeClose + ConfirmationDistance)
                {
                    Print("Confirmed continuation upward — entering now.");
                    OpenTradeBatch("up");
                    ResetPendingSignal();
                }
                else if (currentPrice <= spikeClose - ConfirmationDistance)
                {
                    Print("Price reversed instead of continuing — cancelling signal.");
                    ResetPendingSignal();
                }
            }
            else
            {
                if (currentPrice <= spikeClose - ConfirmationDistance)
                {
                    Print("Confirmed continuation downward — entering now.");
                    OpenTradeBatch("down");
                    ResetPendingSignal();
                }
                else if (currentPrice >= spikeClose + ConfirmationDistance)
                {
                    Print("Price reversed instead of continuing — cancelling signal.");
                    ResetPendingSignal();
                }
            }
        }

        private void ResetPendingSignal()
        {
            waitingForConfirmation = false;
            pendingDirection = null;
            spikeClose = 0;
            barsWaited = 0;
        }

        private void OpenTradeBatch(string direction)
        {
            var tradeType = direction == "up" ? TradeType.Buy : TradeType.Sell;
            var volumeInUnits = Symbol.QuantityToVolumeInUnits(LotsPerEntry);

            for (int i = 0; i < NumEntries; i++)
            {
                ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, Label);
            }

            Print("Opened {0} confirmed spike-follow entries, direction={1}", NumEntries, direction);
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