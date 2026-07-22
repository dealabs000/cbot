using System;
using System.Collections.Generic;
using cAlgo.API;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class XauSpikeScalpBot : Robot
    {
        // ---- Spike detection ----
        [Parameter("Spike Lookback (bars)", DefaultValue = 20)]
        public int SpikeLookback { get; set; }

        [Parameter("Spike Multiplier", DefaultValue = 1.8)]
        public double SpikeMultiplier { get; set; }

        [Parameter("Min Body Ratio", DefaultValue = 0.6)]
        public double MinBodyRatio { get; set; }

        // ---- Confirmation ----
        [Parameter("Confirmation Distance (price)", DefaultValue = 0.15)]
        public double ConfirmationDistance { get; set; }

        [Parameter("Max Wait Bars For Confirmation", DefaultValue = 2)]
        public int MaxWaitBars { get; set; }

        // ---- Supply/Demand (simple zone, used by spike path) ----
        [Parameter("Zone Lookback (bars)", DefaultValue = 40)]
        public int ZoneLookback { get; set; }

        [Parameter("Min Room To Zone (price)", DefaultValue = 0.4)]
        public double MinRoomToZone { get; set; }

        // ---- Pivot Support/Resistance (used by breakout path) ----
        [Parameter("Pivot Strength (bars each side)", DefaultValue = 3)]
        public int PivotStrength { get; set; }

        [Parameter("Pivot Search Window (bars)", DefaultValue = 60)]
        public int PivotSearchWindow { get; set; }

        // ---- Trade management ----
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
        private string? pendingDirection = null;
        private double refPrice = 0;
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
                if (!waitingForConfirmation)
                {
                    CheckForPivotBreakoutEntry();
                }
            }
        }

        protected override void OnBar()
        {
            if (waitingForConfirmation)
            {
                barsWaited++;
                if (barsWaited > MaxWaitBars)
                {
                    Print("Confirmation timed out — cancelling signal.");
                    ResetPendingSignal();
                }
            }
            else if (!HasOpenPositions())
            {
                CheckForSpikeEntry();
                if (!waitingForConfirmation)
                {
                    CheckForPivotBreakoutEntry();
                }
            }
        }

        // ================= PATH A: SPIKE + ZONE =================

        private void CheckForSpikeEntry()
        {
            if (Bars.ClosePrices.Count < Math.Max(SpikeLookback, ZoneLookback) + 2) return;

            int idx = Bars.ClosePrices.Count - 2;
            if (idx < SpikeLookback) return;

            double avgRange = 0;
            for (int i = idx - SpikeLookback; i < idx; i++)
                avgRange += (Bars.HighPrices[i] - Bars.LowPrices[i]);
            avgRange /= SpikeLookback;

            double open = Bars.OpenPrices[idx];
            double close = Bars.ClosePrices[idx];
            double high = Bars.HighPrices[idx];
            double low = Bars.LowPrices[idx];
            double range = high - low;

            if (range <= 0 || avgRange <= 0) return;
            if (range < avgRange * SpikeMultiplier) return;

            double body = Math.Abs(close - open);
            if (body / range < MinBodyRatio) return;

            string direction = close > open ? "up" : "down";

            double zoneHigh, zoneLow;
            GetSimpleZone(idx, out zoneHigh, out zoneLow);

            if (direction == "up" && (zoneHigh - close) < MinRoomToZone)
            {
                Print("Spike up but too close to supply zone ({0:F2} room) — skipping.", zoneHigh - close);
                return;
            }
            if (direction == "down" && (close - zoneLow) < MinRoomToZone)
            {
                Print("Spike down but too close to demand zone ({0:F2} room) — skipping.", close - zoneLow);
                return;
            }

            Print("SPIKE signal: dir={0} — waiting for confirmation.", direction);
            ArmSignal(direction, close);
        }

        private void GetSimpleZone(int idx, out double zoneHigh, out double zoneLow)
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

        // ================= PATH B: PIVOT BREAKOUT + CANDLE PATTERN =================

        private void CheckForPivotBreakoutEntry()
        {
            if (Bars.ClosePrices.Count < PivotSearchWindow + PivotStrength + 2) return;

            int idx = Bars.ClosePrices.Count - 2; // last closed bar
            double resistance, support;
            if (!FindNearestPivots(idx, out resistance, out support)) return;

            double open = Bars.OpenPrices[idx];
            double close = Bars.ClosePrices[idx];
            double high = Bars.HighPrices[idx];
            double low = Bars.LowPrices[idx];
            double range = high - low;
            if (range <= 0) return;

            bool brokeResistance = close > resistance && Bars.ClosePrices[idx - 1] <= resistance;
            bool brokeSupport = close < support && Bars.ClosePrices[idx - 1] >= support;

            if (!brokeResistance && !brokeSupport) return;

            string direction = brokeResistance ? "up" : "down";

            bool patternConfirms =
                (direction == "up" && (IsBullishEngulfing(idx) || IsHammer(idx))) ||
                (direction == "down" && (IsBearishEngulfing(idx) || IsShootingStar(idx)));

            if (!patternConfirms)
            {
                Print("Pivot breakout ({0}) but no confirming candle pattern — skipping.", direction);
                return;
            }

            Print("BREAKOUT signal: dir={0} level={1:F2} — waiting for confirmation.",
                direction, direction == "up" ? resistance : support);
            ArmSignal(direction, close);
        }

        /// Finds nearest pivot high above and pivot low below the current price,
        /// where a pivot is a bar whose high/low is the extreme among PivotStrength bars on each side.
        private bool FindNearestPivots(int idx, out double resistance, out double support)
        {
            resistance = double.MaxValue;
            support = double.MinValue;
            bool foundRes = false, foundSup = false;

            double currentPrice = Bars.ClosePrices[idx];
            int start = Math.Max(PivotStrength, idx - PivotSearchWindow);
            int end = idx - PivotStrength;

            for (int i = end; i >= start; i--)
            {
                bool isPivotHigh = true;
                bool isPivotLow = true;

                for (int j = i - PivotStrength; j <= i + PivotStrength; j++)
                {
                    if (j == i) continue;
                    if (Bars.HighPrices[j] >= Bars.HighPrices[i]) isPivotHigh = false;
                    if (Bars.LowPrices[j] <= Bars.LowPrices[i]) isPivotLow = false;
                }

                if (isPivotHigh && Bars.HighPrices[i] > currentPrice && Bars.HighPrices[i] < resistance)
                {
                    resistance = Bars.HighPrices[i];
                    foundRes = true;
                }
                if (isPivotLow && Bars.LowPrices[i] < currentPrice && Bars.LowPrices[i] > support)
                {
                    support = Bars.LowPrices[i];
                    foundSup = true;
                }
            }

            return foundRes || foundSup;
        }

        // ================= CANDLESTICK PATTERNS =================

        private bool IsBullishEngulfing(int idx)
        {
            if (idx < 1) return false;
            double prevOpen = Bars.OpenPrices[idx - 1];
            double prevClose = Bars.ClosePrices[idx - 1];
            double open = Bars.OpenPrices[idx];
            double close = Bars.ClosePrices[idx];

            bool prevBear = prevClose < prevOpen;
            bool currBull = close > open;
            bool engulfs = open <= prevClose && close >= prevOpen;

            return prevBear && currBull && engulfs;
        }

        private bool IsBearishEngulfing(int idx)
        {
            if (idx < 1) return false;
            double prevOpen = Bars.OpenPrices[idx - 1];
            double prevClose = Bars.ClosePrices[idx - 1];
            double open = Bars.OpenPrices[idx];
            double close = Bars.ClosePrices[idx];

            bool prevBull = prevClose > prevOpen;
            bool currBear = close < open;
            bool engulfs = open >= prevClose && close <= prevOpen;

            return prevBull && currBear && engulfs;
        }

        private bool IsHammer(int idx)
        {
            double open = Bars.OpenPrices[idx];
            double close = Bars.ClosePrices[idx];
            double high = Bars.HighPrices[idx];
            double low = Bars.LowPrices[idx];
            double range = high - low;
            if (range <= 0) return false;

            double body = Math.Abs(close - open);
            double lowerWick = Math.Min(open, close) - low;
            double upperWick = high - Math.Max(open, close);

            // Small body near the top, long lower wick, minimal upper wick
            return lowerWick > body * 2 && upperWick < body && body / range < 0.35;
        }

        private bool IsShootingStar(int idx)
        {
            double open = Bars.OpenPrices[idx];
            double close = Bars.ClosePrices[idx];
            double high = Bars.HighPrices[idx];
            double low = Bars.LowPrices[idx];
            double range = high - low;
            if (range <= 0) return false;

            double body = Math.Abs(close - open);
            double upperWick = high - Math.Max(open, close);
            double lowerWick = Math.Min(open, close) - low;

            // Small body near the bottom, long upper wick, minimal lower wick
            return upperWick > body * 2 && lowerWick < body && body / range < 0.35;
        }

        // ================= SHARED CONFIRMATION / EXECUTION =================

        private void ArmSignal(string direction, double referencePrice)
        {
            waitingForConfirmation = true;
            pendingDirection = direction;
            refPrice = referencePrice;
            barsWaited = 0;
        }

        private void CheckConfirmation()
        {
            if (pendingDirection == null) return;
            double currentPrice = pendingDirection == "up" ? Symbol.Bid : Symbol.Ask;

            if (pendingDirection == "up")
            {
                if (currentPrice >= refPrice + ConfirmationDistance)
                {
                    Print("Confirmed continuation upward — entering now.");
                    OpenTradeBatch("up");
                    ResetPendingSignal();
                }
                else if (currentPrice <= refPrice - ConfirmationDistance)
                {
                    Print("Price reversed instead of continuing — cancelling signal.");
                    ResetPendingSignal();
                }
            }
            else
            {
                if (currentPrice <= refPrice - ConfirmationDistance)
                {
                    Print("Confirmed continuation downward — entering now.");
                    OpenTradeBatch("down");
                    ResetPendingSignal();
                }
                else if (currentPrice >= refPrice + ConfirmationDistance)
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
            refPrice = 0;
            barsWaited = 0;
        }

        private void OpenTradeBatch(string direction)
        {
            var tradeType = direction == "up" ? TradeType.Buy : TradeType.Sell;
            var volumeInUnits = Symbol.QuantityToVolumeInUnits(LotsPerEntry);

            for (int i = 0; i < NumEntries; i++)
                ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, Label);

            Print("Opened {0} entries, direction={1}", NumEntries, direction);
        }

        private bool HasOpenPositions()
        {
            foreach (var position in Positions)
                if (position.Label == Label) return true;
            return false;
        }

        private void CheckIndividualExits()
        {
            var toClose = new List<Position>();
            foreach (var position in Positions)
            {
                if (position.Label != Label) continue;
                if (position.NetProfit >= TakeProfitPerTrade || position.NetProfit <= -StopLossPerTrade)
                    toClose.Add(position);
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