// NQ Combined ORB + Anchored VWAP Strategy
// Uses ORB for trend following and AVWAP for mean reversion
// Version 1.0

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
    public class NQ_Combined_ORB_AVWAP_Strategy : Strategy
    {
        #region Variables - ORB
        // ORB tracking
        private double orbHigh = 0;
        private double orbLow = double.MaxValue;
        private double orbRange = 0;
        private bool orbFormed = false;
        private DateTime orbFormationDate = DateTime.MinValue;

        // ORB Three-Tier State
        private bool waitingLongConfirmation = false;
        private bool waitingShortConfirmation = false;
        private int barsSinceBreakout = 0;
        private double breakoutLevelLong = 0;
        private double breakoutLevelShort = 0;

        // ORB Statistics
        private int tier1Detected = 0;
        private int tier2Confirmed = 0;
        private int tier3Confirmed = 0;
        private int timeouts = 0;
        #endregion

        #region Variables - Anchored VWAP
        // VWAP tracking
        private double anchoredVWAP = 0;
        private double cumulativeTPV = 0;
        private double cumulativeVol = 0;
        private bool vwapActive = false;

        // Anchor point
        private double anchorPrice = 0;
        private DateTime anchorTime = DateTime.MinValue;
        private int anchorBar = 0;

        // Swing detection
        private double swingHigh = 0;
        private double swingLow = double.MaxValue;
        private int barsSinceSwing = 0;
        #endregion

        #region Variables - Position Management
        private double entryPrice = 0;
        private double stopPrice = 0;
        private double bestPrice = 0;
        private double initialRisk = 0;
        private bool trailActive = false;
        private int contracts = 1;
        private TradeType currentTradeType = TradeType.None;

        // Daily tracking
        private double dailyPnL = 0;
        private double peakEquity = 0;
        private DateTime lastResetDate = DateTime.MinValue;
        #endregion

        #region Variables - Time
        private TimeSpan sessionStart = new TimeSpan(6, 30, 0);   // 6:30 AM PT
        private TimeSpan sessionEnd = new TimeSpan(13, 0, 0);    // 1:00 PM PT
        private TimeSpan orbEnd = new TimeSpan(6, 44, 0);        // 6:44 AM PT
        private TimeSpan orbClose = new TimeSpan(14, 0, 0);     // 2:00 PM PT exit
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"NQ Combined ORB + Anchored VWAP Strategy";
                Name = "NQ_Combined_ORB_AVWAP_Strategy";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                Slippage = 2;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Day;
                BarsRequiredToTrade = 20;

                // Risk Management
                AccountSize = 50000;
                RiskPerTrade = 0.01;       // 1%
                DailyLossLimit = 0.02;     // 2%
                MaxDrawdownPercent = 0.10; // 10%

                // ORB Settings
                UseORB = true;
                UseThreeTier = true;
                MaxConfirmationBars = 3;
                VolumeMultiplier = 1.2;
                VolumeLookback = 20;
                ATRStopMultiplier = 1.5;

                // AVWAP Settings
                UseAVWAP = true;
                VWAPAnchorMode = VWAPAnchorModeType.SwingHighLow;
                VWAPPeriod = 20;
                VWAPDeviation = 0.5;       // Entry at 0.5% deviation
                VWAPStopPercent = 1.0;     // 1% stop
                VWAPProfitTargetR = 2.0;   // 2R target

                // General
                EnableTimeBasedExit = true;
            }
            else if (State == State.Configure)
            {
                peakEquity = AccountSize;
                AddPlot(Brushes.DodgerBlue, "AnchoredVWAP");
            }
            else if (State == State.DataLoaded)
            {
                Draw.TextFixed(this, "NinjaInfo",
                    "NQ Combined Strategy\n" +
                    "ORB: " + (UseORB ? "ON" : "OFF") + " | AVWAP: " + (UseAVWAP ? "ON" : "OFF") + "\n" +
                    "Session: 6:30 AM - 1:00 PM PT",
                    TextPosition.BottomLeft);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < VWAPPeriod)
                return;

            // Check for new day
            if (Time[0].Date != lastResetDate.Date)
            {
                ResetDay();
                lastResetDate = Time[0];
            }

            // Check halt conditions
            if (CheckHaltConditions())
                return;

            TimeSpan currentTime = Time[0].TimeOfDay;

            // Only trade during session hours
            if (currentTime < sessionStart || currentTime > sessionEnd)
                return;

            // Calculate average volume for ORB
            double avgVolume = CalculateAverageVolume(VolumeLookback);
            double volumeRatio = Volume[0] / avgVolume;

            // === ORB FORMATION (6:30-6:44 AM) ===
            if (UseORB && currentTime >= sessionStart && currentTime <= orbEnd && !orbFormed)
            {
                UpdateORBFormation();
            }

            // === ANCHORED VWAP CALCULATION ===
            if (UseAVWAP)
            {
                UpdateVWAPComponents(currentTime);
            }

            // === POSITION MANAGEMENT ===
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                ManagePosition(currentTime);
                return;
            }

            // === ENTRY LOGIC ===
            // Only trade after ORB formation
            if (currentTime < orbEnd)
                return;

            // Check for time-based exit approaching
            if (EnableTimeBasedExit && currentTime >= orbClose.Add(TimeSpan.FromMinutes(-15)))
                return;

            // Try ORB Entry (Trend Following)
            if (UseORB && orbFormed)
            {
                if (CheckORBEntry(avgVolume, volumeRatio))
                    return;
            }

            // Try AVWAP Entry (Mean Reversion)
            if (UseAVWAP && vwapActive)
            {
                CheckAVWAPEntry();
            }
        }

        #region ORB Methods
        private void UpdateORBFormation()
        {
            orbHigh = Math.Max(orbHigh, High[0]);
            orbLow = Math.Min(orbLow, Low[0]);

            // Check if ORB window closing
            TimeSpan currentTime = Time[0].TimeOfDay;
            if (currentTime >= orbEnd.Subtract(TimeSpan.FromMinutes(1)) && !orbFormed)
            {
                orbRange = orbHigh - orbLow;

                // Validate ORB range
                if (orbRange >= 1.0 && orbRange <= 120.0)
                {
                    orbFormed = true;
                    orbFormationDate = Time[0].Date;

                    // Set initial VWAP anchor to ORB midpoint
                    if (!vwapActive)
                    {
                        SetAnchor((orbHigh + orbLow) / 2, Time[0], "ORB_Midpoint");
                    }

                    Print(string.Format("ORB FORMED: High={0:F2} Low={1:F2} Range={2:F2}",
                        orbHigh, orbLow, orbRange));
                }
            }
        }

        private bool CheckORBEntry(double avgVolume, double volumeRatio)
        {
            if (!orbFormed)
                return false;

            double midPoint = (orbHigh + orbLow) / 2;
            double orbRangeStop = orbRange * ATRStopMultiplier;

            // === THREE-TIER CONFIRMATION ===
            if (UseThreeTier)
            {
                // TIER 1: Breakout Detection
                if (Close[0] > midPoint && Close[0] >= orbHigh + TickSize && !waitingLongConfirmation && !waitingShortConfirmation)
                {
                    waitingLongConfirmation = true;
                    breakoutLevelLong = orbHigh;
                    barsSinceBreakout = 0;
                    tier1Detected++;
                    Print(string.Format("TIER 1 LONG @ {0:F2}", Close[0]));
                }

                if (Close[0] <= midPoint && Close[0] <= orbLow - TickSize && !waitingShortConfirmation && !waitingLongConfirmation)
                {
                    waitingShortConfirmation = true;
                    breakoutLevelShort = orbLow;
                    barsSinceBreakout = 0;
                    tier1Detected++;
                    Print(string.Format("TIER 1 SHORT @ {0:F2}", Close[0]));
                }

                // Check for confirmation
                if (waitingLongConfirmation && barsSinceBreakout < MaxConfirmationBars)
                {
                    barsSinceBreakout++;

                    bool tier2Valid = CheckTier2Long();
                    bool tier3Valid = volumeRatio >= VolumeMultiplier;

                    if (tier2Valid) tier2Confirmed++;
                    if (tier3Valid) tier3Confirmed++;

                    if (tier2Valid && tier3Valid)
                    {
                        EnterORBLong(orbRangeStop);
                        currentTradeType = TradeType.ORB_Long;
                        waitingLongConfirmation = false;
                        return true;
                    }
                    else if (barsSinceBreakout >= MaxConfirmationBars)
                    {
                        Print("TIMEOUT LONG");
                        timeouts++;
                        waitingLongConfirmation = false;
                    }
                }

                if (waitingShortConfirmation && barsSinceBreakout < MaxConfirmationBars)
                {
                    barsSinceBreakout++;

                    bool tier2Valid = CheckTier2Short();
                    bool tier3Valid = volumeRatio >= VolumeMultiplier;

                    if (tier2Valid) tier2Confirmed++;
                    if (tier3Valid) tier3Confirmed++;

                    if (tier2Valid && tier3Valid)
                    {
                        EnterORBShort(orbRangeStop);
                        currentTradeType = TradeType.ORB_Short;
                        waitingShortConfirmation = false;
                        return true;
                    }
                    else if (barsSinceBreakout >= MaxConfirmationBars)
                    {
                        Print("TIMEOUT SHORT");
                        timeouts++;
                        waitingShortConfirmation = false;
                    }
                }
            }
            else
            {
                // Simple ORB - immediate entry
                if (Close[0] > midPoint && Close[0] >= orbHigh + TickSize)
                {
                    EnterORBLong(orbRangeStop);
                    currentTradeType = TradeType.ORB_Long;
                    return true;
                }

                if (Close[0] <= midPoint && Close[0] <= orbLow - TickSize)
                {
                    EnterORBShort(orbRangeStop);
                    currentTradeType = TradeType.ORB_Short;
                    return true;
                }
            }

            return false;
        }

        private void EnterORBLong(double stopDist)
        {
            entryPrice = Close[0];
            stopPrice = entryPrice - stopDist;
            bestPrice = entryPrice;
            initialRisk = stopDist;
            trailActive = false;

            int positionSize = CalculatePositionSize(stopDist);

            EnterLong(positionSize, "ORB_Long");
            SetStopLoss(CalculationMode.Price, stopPrice);

            Print(string.Format("ORB LONG: {0} @ {1:F2} | Stop: {2:F2} | Range: {3:F2}",
                positionSize, entryPrice, stopPrice, orbRange));
        }

        private void EnterORBShort(double stopDist)
        {
            entryPrice = Close[0];
            stopPrice = entryPrice + stopDist;
            bestPrice = entryPrice;
            initialRisk = stopDist;
            trailActive = false;

            int positionSize = CalculatePositionSize(stopDist);

            EnterShort(positionSize, "ORB_Short");
            SetStopLoss(CalculationMode.Price, stopPrice);

            Print(string.Format("ORB SHORT: {0} @ {1:F2} | Stop: {2:F2} | Range: {3:F2}",
                positionSize, entryPrice, stopPrice, orbRange));
        }

        private bool CheckTier2Long()
        {
            bool retestValid = Low[0] <= breakoutLevelLong + TickSize && Close[0] > breakoutLevelLong;
            bool bullishEngulf = IsBullishEngulfing();
            bool fvgValid = CheckBullishFVG();

            return retestValid || bullishEngulf || fvgValid;
        }

        private bool CheckTier2Short()
        {
            bool retestValid = High[0] >= breakoutLevelShort - TickSize && Close[0] < breakoutLevelShort;
            bool bearishEngulf = IsBearishEngulfing();
            bool fvgValid = CheckBearishFVG();

            return retestValid || bearishEngulf || fvgValid;
        }
        #endregion

        #region Anchored VWAP Methods
        private void UpdateVWAPComponents(TimeSpan currentTime)
        {
            // Detect anchor points
            if (High[0] > swingHigh)
            {
                swingHigh = High[0];
                barsSinceSwing = 0;
            }

            if (Low[0] < swingLow)
            {
                swingLow = Low[0];
                barsSinceSwing = 0;
            }

            barsSinceSwing++;

            // Set anchor based on mode
            if (!vwapActive)
            {
                switch (VWAPAnchorMode)
                {
                    case VWAPAnchorModeType.SwingHighLow:
                        if (barsSinceSwing >= VWAPPeriod && Close[0] < swingHigh * 0.995)
                        {
                            SetAnchor(swingHigh, Time[0], "SwingHigh");
                        }
                        else if (barsSinceSwing >= VWAPPeriod && Close[0] > swingLow * 1.005)
                        {
                            SetAnchor(swingLow, Time[0], "SwingLow");
                        }
                        break;

                    case VWAPAnchorModeType.ORBHighLow:
                        if (orbFormed && currentTime > orbEnd)
                        {
                            if (Close[0] > (orbHigh + orbLow) / 2)
                                SetAnchor(orbHigh, Time[0], "ORB_High");
                            else
                                SetAnchor(orbLow, Time[0], "ORB_Low");
                        }
                        break;
                }
            }

            // Update VWAP calculation
            if (vwapActive)
            {
                UpdateAnchoredVWAP();
                Values[0][0] = anchoredVWAP;
            }
        }

        private void SetAnchor(double price, DateTime time, string label)
        {
            anchorPrice = price;
            anchorTime = time;
            anchorBar = CurrentBar;
            vwapActive = true;

            cumulativeTPV = 0;
            cumulativeVol = 0;

            Print(string.Format("VWAP ANCHOR: {0} @ {1:F2}", label, price));
        }

        private void UpdateAnchoredVWAP()
        {
            double typicalPrice = (High[0] + Low[0] + Close[0]) / 3;
            cumulativeTPV += typicalPrice * Volume[0];
            cumulativeVol += Volume[0];

            if (cumulativeVol > 0)
                anchoredVWAP = cumulativeTPV / cumulativeVol;
        }

        private void CheckAVWAPEntry()
        {
            if (cumulativeVol < 100)
                return;

            double deviation = VWAPDeviation / 100;
            double deviationAmount = anchoredVWAP * deviation;

            double longEntry = anchoredVWAP - deviationAmount;
            double shortEntry = anchoredVWAP + deviationAmount;

            // LONG: Price pulls back TO AVWAP from above
            if (Close[0] <= longEntry && Close[0] > anchoredVWAP * 0.995)
            {
                double stopDist = entryPrice * (VWAPStopPercent / 100);
                entryPrice = Close[0];
                stopPrice = entryPrice - stopDist;
                bestPrice = entryPrice;
                initialRisk = stopDist;
                trailActive = false;

                int positionSize = CalculatePositionSize(stopDist);

                EnterLong(positionSize, "AVWAP_Long");
                SetStopLoss(CalculationMode.Price, stopPrice);

                double profitTarget = entryPrice + (stopDist * VWAPProfitTargetR);
                SetProfitTarget(CalculationMode.Price, profitTarget);

                currentTradeType = TradeType.AVWAP_Long;

                Print(string.Format("AVWAP LONG: {0} @ {1:F2} | VWAP: {2:F2} | Stop: {3:F2}",
                    positionSize, entryPrice, anchoredVWAP, stopPrice));
            }
            // SHORT: Price rallies TO AVWAP from below
            else if (Close[0] >= shortEntry && Close[0] < anchoredVWAP * 1.005)
            {
                double stopDist = entryPrice * (VWAPStopPercent / 100);
                entryPrice = Close[0];
                stopPrice = entryPrice + stopDist;
                bestPrice = entryPrice;
                initialRisk = stopDist;
                trailActive = false;

                int positionSize = CalculatePositionSize(stopDist);

                EnterShort(positionSize, "AVWAP_Short");
                SetStopLoss(CalculationMode.Price, stopPrice);

                double profitTarget = entryPrice - (stopDist * VWAPProfitTargetR);
                SetProfitTarget(CalculationMode.Price, profitTarget);

                currentTradeType = TradeType.AVWAP_Short;

                Print(string.Format("AVWAP SHORT: {0} @ {1:F2} | VWAP: {2:F2} | Stop: {3:F2}",
                    positionSize, entryPrice, anchoredVWAP, stopPrice));
            }
        }
        #endregion

        #region Position Management
        private void ManagePosition(TimeSpan currentTime)
        {
            // Time-based exit at 2:00 PM
            if (EnableTimeBasedExit && currentTime >= orbClose)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    ExitLong("TimeExit");
                    Print("TIME EXIT LONG @ " + Close[0].ToString("F2"));
                }
                else if (Position.MarketPosition == MarketPosition.Short)
                {
                    ExitShort("TimeExit");
                    Print("TIME EXIT SHORT @ " + Close[0].ToString("F2"));
                }
                return;
            }

            // ORB Trailing Stop
            if (currentTradeType == TradeType.ORB_Long || currentTradeType == TradeType.ORB_Short)
            {
                ManageORBTrail();
            }
        }

        private void ManageORBTrail()
        {
            if (Position.MarketPosition == MarketPosition.Long)
            {
                bestPrice = Math.Max(bestPrice, High[0]);

                if (!trailActive && bestPrice >= entryPrice + initialRisk)
                {
                    trailActive = true;
                    stopPrice = entryPrice;
                    SetStopLoss(CalculationMode.Price, stopPrice);
                    Print("ORB Trail Activated - Breakeven");
                }

                if (trailActive)
                {
                    stopPrice = Math.Max(stopPrice, bestPrice - initialRisk);
                    SetStopLoss(CalculationMode.Price, stopPrice);
                }
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                bestPrice = Math.Min(bestPrice, Low[0]);

                if (!trailActive && bestPrice <= entryPrice - initialRisk)
                {
                    trailActive = true;
                    stopPrice = entryPrice;
                    SetStopLoss(CalculationMode.Price, stopPrice);
                    Print("ORB Trail Activated - Breakeven");
                }

                if (trailActive)
                {
                    stopPrice = Math.Min(stopPrice, bestPrice + initialRisk);
                    SetStopLoss(CalculationMode.Price, stopPrice);
                }
            }
        }
        #endregion

        #region Helper Methods
        private double CalculateAverageVolume(int lookback)
        {
            if (CurrentBar < lookback)
                return Volume[0];

            double sum = 0;
            for (int i = 0; i < lookback; i++)
                sum += Volume[i];

            return sum / lookback;
        }

        private int CalculatePositionSize(double stopDistance)
        {
            double dollarRisk = AccountSize * RiskPerTrade;
            double riskPerContract = stopDistance * Instrument.MasterInstrument.PointValue;

            if (riskPerContract <= 0)
                return 1;

            int size = (int)(dollarRisk / riskPerContract);
            return Math.Max(1, Math.Min(size, 10)); // Max 10 contracts
        }

        private bool CheckHaltConditions()
        {
            double currentEquity = AccountSize + dailyPnL;
            double ddPercent = (peakEquity - currentEquity) / peakEquity;

            if (ddPercent >= MaxDrawdownPercent)
            {
                Print(string.Format("HALT: Max Drawdown {0:P}", ddPercent));
                return true;
            }

            if (dailyPnL <= -AccountSize * DailyLossLimit)
            {
                Print(string.Format("HALT: Daily Loss ${0:F2}", dailyPnL));
                return true;
            }

            return false;
        }

        private void ResetDay()
        {
            // Reset ORB
            orbHigh = 0;
            orbLow = double.MaxValue;
            orbRange = 0;
            orbFormed = false;
            waitingLongConfirmation = false;
            waitingShortConfirmation = false;

            // Reset VWAP
            vwapActive = false;
            anchorPrice = 0;
            cumulativeTPV = 0;
            cumulativeVol = 0;
            swingHigh = 0;
            swingLow = double.MaxValue;
            barsSinceSwing = 0;

            // Reset position
            trailActive = false;
            currentTradeType = TradeType.None;

            Print("=== NEW DAY ===");
        }

        private bool IsBullishEngulfing()
        {
            if (CurrentBar < 1) return false;

            // Current bullish
            if (Close[0] <= Open[0]) return false;
            // Previous bearish
            if (Close[1] >= Open[1]) return false;

            double currBody = Close[0] - Open[0];
            double prevBody = Open[1] - Close[1];

            return currBody > prevBody && Close[0] > Open[1] && Open[0] < Close[1];
        }

        private bool IsBearishEngulfing()
        {
            if (CurrentBar < 1) return false;

            // Current bearish
            if (Close[0] >= Open[0]) return false;
            // Previous bullish
            if (Close[1] <= Open[1]) return false;

            double currBody = Open[0] - Close[0];
            double prevBody = Close[1] - Open[1];

            return currBody > prevBody && Open[0] > Close[1] && Close[0] < Open[1];
        }

        private bool CheckBullishFVG()
        {
            if (CurrentBar < 2) return false;
            return Close[0] > High[1] && Low[0] > Close[1];
        }

        private bool CheckBearishFVG()
        {
            if (CurrentBar < 2) return false;
            return Close[0] < Low[1] && High[0] < Close[1];
        }
        #endregion

        #region Properties
        // Risk Management
        [NinjaScriptProperty]
        [Display(Name = "Account Size", Order = 1, GroupName = "1. Risk Management")]
        public double AccountSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Risk Per Trade %", Order = 2, GroupName = "1. Risk Management")]
        public double RiskPerTrade { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Loss Limit %", Order = 3, GroupName = "1. Risk Management")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Drawdown %", Order = 4, GroupName = "1. Risk Management")]
        public double MaxDrawdownPercent { get; set; }

        // ORB Settings
        [NinjaScriptProperty]
        [Display(Name = "Use ORB", Order = 1, GroupName = "2. ORB Settings")]
        public bool UseORB { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Three-Tier", Order = 2, GroupName = "2. ORB Settings")]
        public bool UseThreeTier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Confirmation Bars", Order = 3, GroupName = "2. ORB Settings")]
        public int MaxConfirmationBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Volume Multiplier", Order = 4, GroupName = "2. ORB Settings")]
        public double VolumeMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR Stop Multiplier", Order = 5, GroupName = "2. ORB Settings")]
        public double ATRStopMultiplier { get; set; }

        // AVWAP Settings
        [NinjaScriptProperty]
        [Display(Name = "Use AVWAP", Order = 1, GroupName = "3. AVWAP Settings")]
        public bool UseAVWAP { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchor Mode", Order = 2, GroupName = "3. AVWAP Settings")]
        public VWAPAnchorModeType VWAPAnchorMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Period", Order = 3, GroupName = "3. AVWAP Settings")]
        public int VWAPPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Deviation %", Order = 4, GroupName = "3. AVWAP Settings")]
        public double VWAPDeviation { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Stop %", Order = 5, GroupName = "3. AVWAP Settings")]
        public double VWAPStopPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VWAP Profit Target R", Order = 6, GroupName = "3. AVWAP Settings")]
        public double VWAPProfitTargetR { get; set; }

        // General
        [NinjaScriptProperty]
        [Display(Name = "Enable Time Exit", Order = 1, GroupName = "4. General")]
        public bool EnableTimeBasedExit { get; set; }
        #endregion
    }

    public enum TradeType
    {
        None,
        ORB_Long,
        ORB_Short,
        AVWAP_Long,
        AVWAP_Short
    }

    public enum VWAPAnchorModeType
    {
        SwingHighLow,
        ORBHighLow,
        PreviousDay
    }
}
