// NQ ORB Strategy - Long-term Backtest Version (2022-2024)
// Includes 11 AM time exit (as per original long-term backtest)
// This version matches the Python long-term backtest exactly

#region UsingDeclarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class NQ_ORB_LongTerm_Backtest : Strategy
    {
        #region Variables
        // ORB tracking
        private double orbHigh;
        private double orbLow;
        private double orbRange;
        private bool orbFormed;
        private bool canTradeToday;
        private DateTime lastResetDate;

        // Position tracking
        private double entryPrice;
        private double stopPrice;
        private double bestPrice;
        private double initialRisk;
        private bool trailActive;
        private int contracts;

        // Daily tracking
        private double dailyPnL;
        private double peakEquity;
        private int tradesToday;
        private double dayStartEquity;

        // Times
        private TimeSpan orbStart;
        private TimeSpan orbEnd;
        private TimeSpan tradeStart;
        private TimeSpan tradeEnd;
        private TimeSpan timeExit;

        // Statistics tracking
        private int totalTrades;
        private int winningTrades;
        private int losingTrades;
        private double totalProfit;
        private double totalLoss;
        private List<double> dailyReturns;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description					= @"NQ ORB Long-term Backtest (2022-2024) - Includes 11am time exit";
                Name						= "NQ_ORB_LongTerm_Backtest";
                Calculate					= Calculate.OnBarClose;
                EntriesPerDirection			= 1;
                EntryHandling				= EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy= true;
                ExitOnSessionCloseSeconds	= 30;
                IsFillLimitOnTouch			= false;
                MaximumBarsLookBack			= MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution			= OrderFillResolution.Standard;
                Slippage					= 2;
                StartBehavior				= StartBehavior.WaitUntilFlat;
                TimeInForce					= TimeInForce.Day;
                TraceOrders					= false;
                RealtimeErrorHandling			= RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling			= StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade			= 1;

                // Long-term backtest settings (matches Python version)
                AccountSize					= 50000;
                RiskPerTrade				= 0.005;  // 0.5%
                DailyLossLimit				= 0.02;   // 2%
                MaxDrawdownPercent			= 0.15;   // 15%
                BaseContracts				= 1;
                MaxContracts				= 2;
                ContractStep				= 25000;
                ATRStopMult					= 1.5;
                TrailTriggerR				= 1.0;    // 1R
                TrailATRMult				= 1.0;
                ORBRangeMin					= 1.0;    // Points
                ORBRangeMax					= 120.0;  // Points
                EntrySlippageTicks			= 4;
                ExitSlippageTicks			= 3;

                // Statistics
                totalTrades = 0;
                winningTrades = 0;
                losingTrades = 0;
                totalProfit = 0;
                totalLoss = 0;
                dailyReturns = new List<double>();

                // Initialize times (ET)
                orbStart	= new TimeSpan(9, 30, 0);
                orbEnd		= new TimeSpan(9, 44, 0);
                tradeStart	= new TimeSpan(9, 45, 0);
                tradeEnd	= new TimeSpan(15, 30, 0);
                timeExit	= new TimeSpan(11, 0, 0);  // 11 AM time exit ENABLED
            }
            else if (State == State.Configure)
            {
                // Initialize variables
                orbHigh			= 0;
                orbLow			= double.MaxValue;
                orbRange		= 0;
                orbFormed		= false;
                canTradeToday	= true;
                lastResetDate	= DateTime.MinValue;
                entryPrice		= 0;
                stopPrice		= 0;
                bestPrice		= 0;
                initialRisk		= 0;
                trailActive		= false;
                contracts		= 1;
                dailyPnL		= 0;
                peakEquity		= AccountSize;
                tradesToday		= 0;
                dayStartEquity	= AccountSize;
            }
            else if (State == State.DataLoaded)
            {
                Draw.TextFixed(this, "NinjaInfo",
                    "NQ ORB Long-term Backtest\n" +
                    "Period: 2022-2024\n" +
                    "ORB: 9:30-9:44 ET | 11am Time Exit",
                    TextPosition.BottomLeft);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < 1)
                return;

            // Check for new day
            if (Time[0].Date != lastResetDate.Date)
            {
                // Record previous day return for statistics
                if (lastResetDate != DateTime.MinValue)
                {
                    double dayReturn = (AccountSize + dailyPnL - dayStartEquity) / dayStartEquity;
                    dailyReturns.Add(dayReturn);
                }

                ResetDay();
                lastResetDate = Time[0];
                dayStartEquity = AccountSize + dailyPnL;
            }

            // Check halt conditions
            if (CheckHaltConditions())
                return;

            TimeSpan currentTime = Time[0].TimeOfDay;

            // ORB Formation Period (9:30-9:44 AM)
            if (currentTime >= orbStart && currentTime <= orbEnd)
            {
                UpdateORB(High[0], Low[0], currentTime);
                return;
            }

            // Trading Window (9:45 AM - 3:30 PM)
            if (currentTime >= tradeStart && currentTime <= tradeEnd)
            {
                if (Position.MarketPosition == MarketPosition.Flat && orbFormed && canTradeToday)
                {
                    if (tradesToday == 0)
                        CheckEntry(Close[0], currentTime);
                }
                else if (Position.MarketPosition != MarketPosition.Flat)
                {
                    CheckExit(High[0], Low[0], Close[0], currentTime);
                }
            }

            // End of day
            if (currentTime >= tradeEnd && Position.MarketPosition != MarketPosition.Flat)
            {
                ExitPosition("EOD");
            }
        }

        private void UpdateORB(double high, double low, TimeSpan currentTime)
        {
            if (high > orbHigh)
                orbHigh = high;
            if (low < orbLow)
                orbLow = low;

            if (currentTime >= orbEnd && !orbFormed)
            {
                orbRange = orbHigh - orbLow;

                if (orbRange >= ORBRangeMin && orbRange <= ORBRangeMax)
                {
                    orbFormed = true;

                    try
                    {
                        Draw.Rectangle(this, "ORB_" + CurrentBar, true,
                            new TimeSpan(9, 30, 0), orbLow,
                            new TimeSpan(9, 44, 0), orbHigh,
                            Brushes.Transparent, Brushes.CornflowerBlue, 2);
                    }
                    catch { }

                    Print(string.Format("ORB Formed: High={0:F2} Low={1:F2} Range={2:F2}",
                        orbHigh, orbLow, orbRange));
                }
                else
                {
                    Print(string.Format("ORB Range {0:F2} outside limits, skipping", orbRange));
                    canTradeToday = false;
                }
            }
        }

        private void CheckEntry(double close, TimeSpan currentTime)
        {
            if (orbHigh == 0 || orbLow == double.MaxValue)
                return;

            double atr = orbRange;
            double stopDist = atr * ATRStopMult;
            double midPoint = (orbHigh + orbLow) / 2;

            int positionSize = CalculatePositionSize(stopDist);

            // Long setup: above midpoint and breaks ORB high
            if (close > midPoint && close >= orbHigh + TickSize)
            {
                entryPrice = close + (EntrySlippageTicks * TickSize);
                stopPrice = entryPrice - stopDist;
                contracts = positionSize;

                EnterLong(contracts, "ORB_Long");
                SetStopLoss(CalculationMode.Price, stopPrice);

                Print(string.Format("ENTER LONG: {0} @ {1:F2} Stop={2:F2}",
                    contracts, entryPrice, stopPrice));

                initialRisk = stopDist;
                bestPrice = entryPrice;
                trailActive = false;
                tradesToday++;
            }
            // Short setup: below midpoint and breaks ORB low
            else if (close <= midPoint && close <= orbLow - TickSize)
            {
                entryPrice = close - (EntrySlippageTicks * TickSize);
                stopPrice = entryPrice + stopDist;
                contracts = positionSize;

                EnterShort(contracts, "ORB_Short");
                SetStopLoss(CalculationMode.Price, stopPrice);

                Print(string.Format("ENTER SHORT: {0} @ {1:F2} Stop={2:F2}",
                    contracts, entryPrice, stopPrice));

                initialRisk = stopDist;
                bestPrice = entryPrice;
                trailActive = false;
                tradesToday++;
            }
        }

        private void CheckExit(double high, double low, double close, TimeSpan currentTime)
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            // Update best price
            if (Position.MarketPosition == MarketPosition.Long)
                bestPrice = Math.Max(bestPrice, high);
            else
                bestPrice = Math.Min(bestPrice, low);

            // Activate trailing stop at 1R profit
            if (!trailActive)
            {
                if (Position.MarketPosition == MarketPosition.Long &&
                    bestPrice >= entryPrice + initialRisk)
                {
                    trailActive = true;
                    Print("Trail Activated (Long)");
                }
                else if (Position.MarketPosition == MarketPosition.Short &&
                         bestPrice <= entryPrice - initialRisk)
                {
                    trailActive = true;
                    Print("Trail Activated (Short)");
                }
            }

            // Update trailing stop
            if (trailActive)
            {
                double trailDist = initialRisk * TrailATRMult;

                if (Position.MarketPosition == MarketPosition.Long)
                {
                    double newStop = Math.Max(stopPrice, bestPrice - trailDist);
                    if (newStop > stopPrice)
                    {
                        stopPrice = newStop;
                        SetStopLoss(CalculationMode.Price, stopPrice);
                    }
                }
                else
                {
                    double newStop = Math.Min(stopPrice, bestPrice + trailDist);
                    if (newStop < stopPrice)
                    {
                        stopPrice = newStop;
                        SetStopLoss(CalculationMode.Price, stopPrice);
                    }
                }
            }

            // Check stop loss
            bool stopHit = false;
            if (Position.MarketPosition == MarketPosition.Long && low <= stopPrice)
            {
                ExitPosition(trailActive ? "TRAIL" : "STOP");
                stopHit = true;
            }
            else if (Position.MarketPosition == MarketPosition.Short && high >= stopPrice)
            {
                ExitPosition(trailActive ? "TRAIL" : "STOP");
                stopHit = true;
            }

            if (stopHit)
                return;

            // Time exit at 11 AM if not profitable (LONG-TERM VERSION)
            if (currentTime >= timeExit && !trailActive)
            {
                if (Position.MarketPosition == MarketPosition.Long && close <= entryPrice)
                {
                    ExitPosition("TIME");
                    Print("TIME EXIT: Long position not profitable at 11am");
                    return;
                }
                else if (Position.MarketPosition == MarketPosition.Short && close >= entryPrice)
                {
                    ExitPosition("TIME");
                    Print("TIME EXIT: Short position not profitable at 11am");
                    return;
                }
            }
        }

        private void ExitPosition(string reason)
        {
            if (Position.MarketPosition == MarketPosition.Long)
            {
                ExitLong(reason);
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                ExitShort(reason);
            }

            try
            {
                double pnl = Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency);
                dailyPnL += pnl;

                double currentEquity = AccountSize + dailyPnL;
                if (currentEquity > peakEquity)
                    peakEquity = currentEquity;

                // Track statistics
                totalTrades++;
                if (pnl > 0)
                {
                    winningTrades++;
                    totalProfit += pnl;
                }
                else
                {
                    losingTrades++;
                    totalLoss += Math.Abs(pnl);
                }

                Print(string.Format("EXIT {0}: P&L ${1:F2} | Total Trades: {2}",
                    reason, pnl, totalTrades));
            }
            catch { }
        }

        private int CalculatePositionSize(double stopDistance)
        {
            try
            {
                double profit = dailyPnL;
                int contractLimit = BaseContracts + (int)((profit / ContractStep) * 2);
                contractLimit = Math.Min(contractLimit, MaxContracts);

                double dollarRisk = AccountSize * RiskPerTrade;
                double riskPerContract = stopDistance * Instrument.MasterInstrument.PointValue;

                if (riskPerContract <= 0)
                    return 1;

                int size = (int)(dollarRisk / riskPerContract);
                size = Math.Max(1, size);

                return Math.Min(contractLimit, size);
            }
            catch
            {
                return 1;
            }
        }

        private bool CheckHaltConditions()
        {
            try
            {
                double currentEquity = AccountSize + dailyPnL;
                double ddPercent = (peakEquity - currentEquity) / peakEquity;

                if (ddPercent >= MaxDrawdownPercent)
                {
                    Print(string.Format("HALT: Max Drawdown {0:P} exceeded", ddPercent));
                    return true;
                }

                if (dailyPnL <= -AccountSize * DailyLossLimit)
                {
                    Print(string.Format("HALT: Daily Loss Limit ${0:F2} exceeded", dailyPnL));
                    return true;
                }
            }
            catch { }

            return false;
        }

        private void ResetDay()
        {
            orbHigh = 0;
            orbLow = double.MaxValue;
            orbRange = 0;
            orbFormed = false;
            canTradeToday = true;
            trailActive = false;
            tradesToday = 0;

            Print("=== NEW DAY ===");
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            base.OnExecutionUpdate(execution, executionId, price, quantity, marketPosition, orderId, time);

            try
            {
                if (execution.Order.OrderState == OrderState.Filled)
                {
                    if (execution.Order.Name.Contains("Long") || execution.Order.Name.Contains("Short"))
                    {
                        entryPrice = price;
                        Print(string.Format("ENTRY FILLED: {0} @ {1:F2}",
                            execution.Order.Name, price));
                    }
                    else if (execution.Order.Name.Contains("Exit"))
                    {
                        double pnl = execution.Quantity * (price - entryPrice) *
                            (marketPosition == MarketPosition.Long ? 1 : -1);
                        Print(string.Format("EXIT FILLED: {0} @ {1:F2} P&L ${2:F2}",
                            execution.Order.Name, price, pnl));
                    }
                }
            }
            catch { }
        }

        // Strategy Information
        public override string ToString()
        {
            double winRate = totalTrades > 0 ? (double)winningTrades / totalTrades * 100 : 0;
            double profitFactor = totalLoss > 0 ? totalProfit / totalLoss : 0;
            double currentEquity = AccountSize + dailyPnL;
            double totalReturn = (currentEquity - AccountSize) / AccountSize * 100;
            double maxDD = peakEquity > 0 ? (peakEquity - currentEquity) / peakEquity * 100 : 0;

            return string.Format(
                "Trades: {0} | Win Rate: {1:F1}% | PF: {2:F2} | Return: {3:F1}% | Max DD: {4:F1}%",
                totalTrades, winRate, profitFactor, totalReturn, maxDD);
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Account Size", Description = "Starting account size", Order = 1, GroupName = "1. Risk Management")]
        public double AccountSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Risk Per Trade", Description = "Risk per trade as % of account", Order = 2, GroupName = "1. Risk Management")]
        public double RiskPerTrade { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Loss Limit", Description = "Daily loss limit as % of account", Order = 3, GroupName = "1. Risk Management")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Drawdown %", Description = "Max drawdown before halt", Order = 4, GroupName = "1. Risk Management")]
        public double MaxDrawdownPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Base Contracts", Description = "Base number of contracts", Order = 1, GroupName = "2. Position Sizing")]
        public int BaseContracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Contracts", Description = "Maximum contracts", Order = 2, GroupName = "2. Position Sizing")]
        public int MaxContracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Contract Step", Description = "Profit step for scaling", Order = 3, GroupName = "2. Position Sizing")]
        public double ContractStep { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR Stop Multiplier", Description = "ATR multiplier for stop", Order = 1, GroupName = "3. Strategy Parameters")]
        public double ATRStopMult { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail Trigger R", Description = "R-multiple to activate trail", Order = 2, GroupName = "3. Strategy Parameters")]
        public double TrailTriggerR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail ATR Mult", Description = "ATR multiplier for trail", Order = 3, GroupName = "3. Strategy Parameters")]
        public double TrailATRMult { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ORB Range Min", Description = "Minimum ORB range", Order = 4, GroupName = "3. Strategy Parameters")]
        public double ORBRangeMin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ORB Range Max", Description = "Maximum ORB range", Order = 5, GroupName = "3. Strategy Parameters")]
        public double ORBRangeMax { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Entry Slippage Ticks", Description = "Slippage on entry", Order = 1, GroupName = "4. Execution")]
        public int EntrySlippageTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Exit Slippage Ticks", Description = "Slippage on exit", Order = 2, GroupName = "4. Execution")]
        public int ExitSlippageTicks { get; set; }
        #endregion
    }
}
