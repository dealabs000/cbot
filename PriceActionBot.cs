using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Requests;
using System.Collections.Generic;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class XAUUSD_Scalper : Robot
    {
        // Parameters
        [Parameter("Trade Size (Units)", Group = "Risk", DefaultValue = 1000)]
        public double TradeSize { get; set; }

        [Parameter("Take Profit ($)", Group = "Risk", DefaultValue = 1.0)]
        public double TakeProfitDollars { get; set; }

        [Parameter("EMA Fast Period", Group = "Indicators", DefaultValue = 5)]
        public int EmaFastPeriod { get; set; }

        [Parameter("EMA Slow Period", Group = "Indicators", DefaultValue = 20)]
        public int EmaSlowPeriod { get; set; }

        [Parameter("RSI Period", Group = "Indicators", DefaultValue = 7)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Overbought", Group = "Indicators", DefaultValue = 70)]
        public int RsiOverbought { get; set; }

        [Parameter("RSI Oversold", Group = "Indicators", DefaultValue = 30)]
        public int RsiOversold { get; set; }

        [Parameter("Max Trades Per Bar", Group = "Settings", DefaultValue = 2)]
        public int MaxTradesPerBar { get; set; }

        // Indicators
        private ExponentialMovingAverage emaFast;
        private ExponentialMovingAverage emaSlow;
        private RelativeStrengthIndex rsi;

        // Tracking
        private int tradesThisBar = 0;
        private DateTime currentBarTime;
        private Dictionary<long, double> entryPrices = new Dictionary<long, double>();

        protected override void OnStart()
        {
            // Initialize indicators
            emaFast = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaFastPeriod);
            emaSlow = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaSlowPeriod);
            rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);

            currentBarTime = Bars.Last(0).OpenTime;
            Print("XAUUSD Scalper Started - Target: $1 profit per trade");
        }

        protected override void OnTick()
        {
            // Check for new bar
            if (Bars.Last(0).OpenTime != currentBarTime)
            {
                currentBarTime = Bars.Last(0).OpenTime;
                tradesThisBar = 0;
                
                // Clean closed positions
                CleanEntryPrices();
            }

            // Manage existing positions
            ManagePositions();

            // Open new trades
            if (tradesThisBar < MaxTradesPerBar)
            {
                OpenTrades();
            }
        }

        private void ManagePositions()
        {
            foreach (var position in Positions)
            {
                if (position.SymbolName != SymbolName) continue;

                double profitInDollars = position.NetProfit;
                
                // Close if profit >= $1
                if (profitInDollars >= TakeProfitDollars)
                {
                    ClosePosition(position);
                    Print($"Closed {position.TradeType} at ${profitInDollars:F2} profit");
                }
                
                // Emergency stop loss - prevent big losses
                if (profitInDollars <= -2.0)
                {
                    ClosePosition(position);
                    Print($"Emergency close: ${profitInDollars:F2} loss");
                }
            }
        }

        private void OpenTrades()
        {
            var lastBar = Bars.Last(1);
            var currentClose = lastBar.Close;
            var currentHigh = lastBar.High;
            var currentLow = lastBar.Low;

            // Check trend with EMAs
            bool uptrend = emaFast.Result.Last(1) > emaSlow.Result.Last(1) && 
                          emaFast.Result.Last(2) <= emaSlow.Result.Last(2);
            
            bool downtrend = emaFast.Result.Last(1) < emaSlow.Result.Last(1) && 
                            emaFast.Result.Last(2) >= emaSlow.Result.Last(2);

            double rsiValue = rsi.Result.Last(1);
            double prevRsi = rsi.Result.Last(2);

            // Calculate TP in pips for $1
            double tickValue = Symbol.PipValue * (TradeSize / 100000);
            double targetPips = Math.Max(TakeProfitDollars / tickValue, 10);
            
            // Buy signal: EMA crossover up + RSI not overbought + price near low
            if (uptrend && rsiValue < RsiOverbought && currentClose <= currentLow + 0.5 * (currentHigh - currentLow))
            {
                ExecuteMarketOrder(TradeType.Buy, SymbolName, TradeSize, "ScalpBuy", 
                    null, targetPips * Symbol.PipSize);
                tradesThisBar++;
                Print($"BUY Signal - RSI: {rsiValue:F1}, Price: {currentClose}");
            }
            
            // Sell signal: EMA crossover down + RSI not oversold + price near high
            else if (downtrend && rsiValue > RsiOversold && currentClose >= currentHigh - 0.5 * (currentHigh - currentLow))
            {
                ExecuteMarketOrder(TradeType.Sell, SymbolName, TradeSize, "ScalpSell", 
                    null, targetPips * Symbol.PipSize);
                tradesThisBar++;
                Print($"SELL Signal - RSI: {rsiValue:F1}, Price: {currentClose}");
            }
        }

        private void CleanEntryPrices()
        {
            var toRemove = new List<long>();
            foreach (var pos in Positions)
            {
                if (!entryPrices.ContainsKey(pos.Id))
                    toRemove.Add(pos.Id);
            }
            foreach (var id in toRemove)
                entryPrices.Remove(id);
        }

        protected override void OnStop()
        {
            Print("XAUUSD Scalper Stopped");
        }
    }
}