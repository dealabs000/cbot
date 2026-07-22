using System;
using System.Collections.Generic;
using cAlgo.API;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class XauMomentumScalpBot : Robot
    {
        // ---- Momentum candle detection ----
        [Parameter("Momentum Lookback (bars)", DefaultValue = 20)]
        public int MomentumLookback { get; set; }

        [Parameter("Momentum Multiplier", DefaultValue = 1.4)]
        public double MomentumMultiplier { get; set; }

        [Parameter("Min Body Ratio", DefaultValue = 0.55)]
        public double MinBodyRatio { get; set; }

        [Parameter("Require Candlestick Pattern", DefaultValue = false)]
        public bool RequirePattern { get; set; }

        // ---- Pivot Support/Resistance ----
        [Parameter("Pivot Strength (bars each side)", DefaultValue = 3)]
        public int PivotStrength { get; set; }

        [Parameter("Pivot Search Window (bars)", DefaultValue = 60)]
        public int PivotSearchWindow { get; set; }

        [Parameter("Min Room To Level (price)", DefaultValue = 0.3)]
        public double MinRoomToLevel { get; set; }

        // ---- Trade management (tuned for small account) ----
        [Parameter("Lots Per Entry", DefaultValue = 0.01)]
        public double LotsPerEntry { get; set; }

        [Parameter("Number Of Entries", DefaultValue = 2)]
        public int NumEntries { get; set; }

        [Parameter("Take Profit Per Trade ($)", DefaultValue = 1.0)]
        public double TakeProfitPerTrade { get; set; }

        [Parameter("Stop Loss Per Trade ($)", DefaultValue = 2.0)]
        public double StopLossPerTrade { get; set; }

        private const string Label = "XauMomentumScalpBot";

        protected override void OnStart()
        {
            if (SymbolName != "XAUUSD")
            {
                Print("This bot only trades XAUUSD. Attach it to an XAUUSD M1 chart.");
                Stop();
                return;
            }

            Print("Bot started. Account balance: {0:F2} — verify lot size/entry count fits your balance before scaling up.", Account.Balance);
        }

        protected override void OnTick()
        {
            CheckIndividualExits();
        }

        protected override void OnBar()
        {
            // No waiting, no confirmation delay — evaluate and act the instant a bar closes
            if (HasOpenPositions()) return;
            TryEnterOnMomentum();
        }

        private void TryEnterOnMomentum()
        {
            if (Bars.ClosePrices.Count < Math.Max(MomentumLookback, PivotSearchWindow) + PivotStrength + 2) return;

            int idx = Bars.ClosePrices.Count - 2; // last fully closed bar

            double avgRange = 0;
            for (int i = idx - MomentumLookback; i < idx; i++)
                avgRange += (Bars.HighPrices[i] - Bars.LowPrices[i]);
            avgRange /= MomentumLookback;

            double open = Bars.OpenPrices[idx];
            double close = Bars.ClosePrices[idx];
            double high = Bars.HighPrices[idx];
            double low = Bars.LowPrices[idx];
            double range = high - low;

            if (range <= 0 || avgRange <= 0) return;

            bool isBigCandle = range >= avgRange * MomentumMultiplier;
            double body = Math.Abs(close - open);
            bool strongBody = (body / range) >= MinBodyRatio;

            if (!isBigCandle || !strongBody) return;

            string direction = close > open ? "up" : "down";

            // Support/Resistance filter — skip only if we're jammed right against the opposing level
            double resistance, support;
            if (FindNearestPivots(idx, out resistance, out support))
            {
                if (direction == "up" && resistance != double.MaxValue && (resistance - close) < MinRoomToLevel)
                {
                    Print("Momentum up but pinned under resistance ({0:F2} room) — skipping.", resistance - close);
                    return;
                }
                if (direction == "down" && support != double.MinValue && (close - support) < MinRoomToLevel)
                {
                    Print("Momentum down but pinned above support ({0:F2} room) — skipping.", close - support);
                    return;
                }
            }

            if (RequirePattern)
            {
                bool patternConfirms =
                    (direction == "up" && (IsBullishEngulfing(idx) || IsHammer(idx))) ||
                    (direction == "down" && (IsBearishEngulfing(idx) || IsShootingStar(idx)));

                if (!patternConfirms)
                {
                    Print("Big candle ({0}) but no confirming candlestick pattern — skipping.", direction);
                    return;
                }
            }

            Print("MOMENTUM entry: dir={0} range={1:F2} avgRange={2:F2} — entering immediately.", direction, range, avgRange);
            OpenTradeBatch(direction);
        }

        private bool FindNearestPivots(int idx, out double resistance, out double support)
        {
            resistance = double.MaxValue;
            support = double.MinValue;
            bool found = false;

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
                    found = true;
                }
                if (isPivotLow && Bars.LowPrices[i] < currentPrice && Bars.LowPrices[i] > support)
                {
                    support = Bars.LowPrices[i];
                    found = true;
                }
            }

            return found;
        }

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
            return upperWick > body * 2 && lowerWick < body && body / range < 0.35;
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