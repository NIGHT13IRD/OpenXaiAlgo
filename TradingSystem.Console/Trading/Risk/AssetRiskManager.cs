using Microsoft.Extensions.Logging;
using TradingSystem.Console.Configuration;
using TradingSystem.Console.Services;
using TradingSystem.Console.Utils;

namespace TradingSystem.Console.Trading;

/// <summary>
/// 🛡️ Single Asset Risk Manager (Complete Version)
/// 
/// Risk Checker
/// Responsibility: Verify if all trading operations comply with risk rules
/// - Max Drawdown Detection (MaxTotalDrawdown)
/// - Peak Capital Tracking (PeakCapital)
/// - Daily Loss Limit (MaxDailyLossPercent)
/// - Consecutive Loss Limit (MaxConsecutiveLosses)
/// - Daily Trade Count Limit (MaxDailyTrades)
/// - Risk Pause/Resume Mechanism
/// - Daily Report Sending
/// - Stop Loss Notification
/// </summary>
public class AssetRiskManager : IRiskManager
{
    private readonly ILogger? _logger;
    private readonly AssetRiskConfig _assetRisk;
    private readonly DingTalkNotificationService? _dingTalkService;
    private readonly TelegramNotificationService? _telegramService;
    
    // Daily Report Tracking
    private DateTime _lastDailyReportTime = DateTime.MinValue;
    private readonly TimeSpan _dailyReportInterval = TimeSpan.FromHours(24);
    
    public AssetRiskManager(
        AssetRiskConfig assetRisk, 
        ILogger? logger = null,
        DingTalkNotificationService? dingTalkService = null,
        TelegramNotificationService? telegramService = null)
    {
        _assetRisk = assetRisk;
        _logger = logger;
        _dingTalkService = dingTalkService;
        _telegramService = telegramService;
    }

    /// <summary>
    /// Check if trading is allowed
    /// </summary>
    public RiskCheckResult CanTrade(TradingState state)
    {
        // 0. Check if paused by risk control
        if (state.RiskPaused)
        {
            return new RiskCheckResult
            {
                IsAllowed = false,
                Reason = state.RiskPauseReason ?? "Trading paused by risk control"
            };
        }
        
        // 1. Check Daily Trades Count
        if (state.TodayTrades >= _assetRisk.MaxDailyTrades)
        {
            PauseTrading(state, $"Daily trades limit reached ({_assetRisk.MaxDailyTrades})");
            return new RiskCheckResult
            {
                IsAllowed = false,
                Reason = $"Max daily trades reached ({_assetRisk.MaxDailyTrades})"
            };
        }

        // 2. Check Consecutive Losses
        if (state.ConsecutiveLosses >= _assetRisk.MaxConsecutiveLosses)
        {
            PauseTrading(state, $"Consecutive losses limit reached ({_assetRisk.MaxConsecutiveLosses})");
            return new RiskCheckResult
            {
                IsAllowed = false,
                Reason = $"Max consecutive losses reached ({_assetRisk.MaxConsecutiveLosses})"
            };
        }

        // 3. Check Daily Loss Limit (Using DayStartCapital)
        if (state.DayStartCapital > 0)
        {
            var dailyLossPercent = (state.DayStartCapital - state.Capital) / state.DayStartCapital;
            if (dailyLossPercent >= _assetRisk.MaxDailyLossPercent)
            {
                PauseTrading(state, $"Daily loss limit reached ({dailyLossPercent:P2} >= {_assetRisk.MaxDailyLossPercent:P2})");
                return new RiskCheckResult
                {
                    IsAllowed = false,
                    Reason = $"Max daily loss reached ({dailyLossPercent:P2} >= {_assetRisk.MaxDailyLossPercent:P2})"
                };
            }
        }

        // 4. 🔥 Check Max Drawdown (Single Asset Config)
        if (state.PeakCapital > 0)
        {
            var currentDrawdown = (state.PeakCapital - state.Capital) / state.PeakCapital;
            if (currentDrawdown >= _assetRisk.MaxTotalDrawdownPercent)
            {
                PauseTrading(state, $"Max drawdown exceeded ({currentDrawdown:P2} >= {_assetRisk.MaxTotalDrawdownPercent:P2})");
                SendRiskAlertAsync("Max Drawdown Exceeded", 
                    $"[{state.Symbol}] Current Drawdown {currentDrawdown:P2} exceeded limit {_assetRisk.MaxTotalDrawdownPercent:P2}\n" +
                    $"Peak Capital: ${state.PeakCapital:N2}\n" +
                    $"Current Capital: ${state.Capital:N2}")
                    .SafeFireAndForget(onError: ex => _logger?.LogError(ex, "SendRiskAlertAsync failed"));
                return new RiskCheckResult
                {
                    IsAllowed = false,
                    Reason = $"Max drawdown exceeded ({currentDrawdown:P2} >= {_assetRisk.MaxTotalDrawdownPercent:P2})"
                };
            }
        }

        return new RiskCheckResult { IsAllowed = true };
    }

    /// <summary>
    /// Handle Stop Loss Event
    /// </summary>
    public void OnStopLoss(TradingState state, decimal lossPercent, decimal entryPrice, decimal stopLossPrice)
    {
        state.ConsecutiveLosses++;
        _logger?.LogWarning("[{Symbol}] 🛑 Stop Loss Triggered: Consecutive Losses {Count}, Loss {Loss:P2}", 
            state.Symbol, state.ConsecutiveLosses, lossPercent);
        
        // Send Stop Loss Notification
        SendStopLossNotificationAsync(state.Symbol, entryPrice, stopLossPrice, lossPercent)
            .SafeFireAndForget(onError: ex => _logger?.LogError(ex, "SendStopLossNotificationAsync failed"));
        
        // Check Risk Control
        if (state.ConsecutiveLosses >= _assetRisk.MaxConsecutiveLosses)
        {
            PauseTrading(state, $"Consecutive losses {state.ConsecutiveLosses}");
        }
    }

    /// <summary>
    /// Handle Profit Close
    /// </summary>
    public void OnProfitClose(TradingState state)
    {
        state.ConsecutiveLosses = 0;
        _logger?.LogInformation("[{Symbol}] 💚 Profit Close, consecutive loss count reset", state.Symbol);
    }

    /// <summary>
    /// Pause Trading
    /// </summary>
    public void PauseTrading(TradingState state, string reason)
    {
        if (state.RiskPaused) return;
        
        state.RiskPaused = true;
        state.RiskPauseReason = reason;
        _logger?.LogWarning("[{Symbol}] ⛔ Trading Paused: {Reason}", state.Symbol, reason);
        
        SendRiskAlertAsync("Trading Paused", $"[{state.Symbol}] {reason}")
            .SafeFireAndForget(onError: ex => _logger?.LogError(ex, "SendRiskAlertAsync failed"));
    }

    /// <summary>
    /// Resume Trading
    /// </summary>
    public void ResumeTrading(TradingState state)
    {
        if (!state.RiskPaused) return;
        
        state.RiskPaused = false;
        state.RiskPauseReason = null;
        _logger?.LogInformation("[{Symbol}] ✅ Trading Resumed", state.Symbol);
    }

    /// <summary>
    /// Reset Risk State (Including Peak Capital)
    /// </summary>
    public void ResetDrawdownPause(TradingState state, bool resetPeakCapital = true)
    {
        state.RiskPaused = false;
        state.RiskPauseReason = null;
        state.ConsecutiveLosses = 0;
        
        if (resetPeakCapital)
        {
            state.PeakCapital = state.Capital;
            state.MaxDrawdown = 0m;
            _logger?.LogWarning("[{Symbol}] 🔄 Peak Capital Reset to Current Capital: ${Capital:N2}", state.Symbol, state.Capital);
        }
        
        _logger?.LogWarning("[{Symbol}] 🔄 Risk State Completely Reset", state.Symbol);
    }

    /// <summary>
    /// Update Peak Capital (Should be called after each trade)
    /// </summary>
    public void UpdatePeakCapital(TradingState state)
    {
        if (state.Capital > state.PeakCapital)
        {
            state.PeakCapital = state.Capital;
            _logger?.LogDebug("[{Symbol}] 📈 New Peak Capital: ${PeakCapital:N2}", state.Symbol, state.PeakCapital);
        }
        
        // Calculate Current Drawdown
        if (state.PeakCapital > 0)
        {
            var currentDrawdown = (state.PeakCapital - state.Capital) / state.PeakCapital;
            if (currentDrawdown > state.MaxDrawdown)
            {
                state.MaxDrawdown = currentDrawdown;
            }
        }
    }

    /// <summary>
    /// Daily Reset Check
    /// </summary>
    public void CheckNewDay(TradingState state)
    {
        var today = DateTime.UtcNow.Date;
        if (state.LastTradeDate.Date < today)
        {
            _logger?.LogInformation("[{Symbol}] 📅 New Day, Resetting Daily Counters", state.Symbol);
            
            state.DayStartCapital = state.Capital;
            state.TodayTrades = 0;
            state.TodayPnL = 0m;
            state.LastTradeDate = today;
            
            // If paused due to daily loss, auto-resume on new day
            if (state.RiskPaused && state.RiskPauseReason?.Contains("Daily") == true)
            {
                ResumeTrading(state);
                _logger?.LogInformation("[{Symbol}] ✅ New Day, Automatically Resuming Daily Loss Pause", state.Symbol);
            }
        }
    }

    /// <summary>
    /// Check and Send Daily Report
    /// </summary>
    public async Task CheckAndSendDailyReportAsync(TradingState state)
    {
        var now = DateTime.UtcNow;
        if (now - _lastDailyReportTime >= _dailyReportInterval)
        {
            _lastDailyReportTime = now;
            await SendDailyReportAsync(state);
        }
    }

    private async Task SendStopLossNotificationAsync(string symbol, decimal entryPrice, decimal stopLossPrice, decimal lossPercent)
    {
        try
        {
            if (_dingTalkService != null)
            {
                await _dingTalkService.SendStopLossAsync(symbol, entryPrice, stopLossPrice, lossPercent);
            }
            if (_telegramService != null)
            {
                await _telegramService.SendStopLossAsync(symbol, entryPrice, stopLossPrice, lossPercent);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send stop loss notification");
        }
    }

    private async Task SendRiskAlertAsync(string alertType, string message)
    {
        try
        {
            if (_dingTalkService != null)
            {
                await _dingTalkService.SendRiskAlertAsync(alertType, message);
            }
            if (_telegramService != null)
            {
                await _telegramService.SendRiskAlertAsync(alertType, message);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send risk alert");
        }
    }

    private async Task SendDailyReportAsync(TradingState state)
    {
        try
        {
            var pnlPercent = state.DayStartCapital > 0 
                ? state.TodayPnL / state.DayStartCapital 
                : 0;
            var drawdown = state.PeakCapital > 0 
                ? (state.PeakCapital - state.Capital) / state.PeakCapital 
                : 0;
            
            // Calculate Win Count (Positive Daily PnL counts as one win conceptually)
            var winCount = state.TodayPnL > 0 ? 1 : 0;
            
            if (_dingTalkService != null)
            {
                await _dingTalkService.SendDailyReportAsync(
                    state.Symbol, state.DayStartCapital, state.Capital,
                    state.TodayTrades, winCount, drawdown);
            }
            if (_telegramService != null)
            {
                await _telegramService.SendDailyReportAsync(
                    state.Symbol, state.DayStartCapital, state.Capital,
                    state.TodayTrades, winCount, drawdown);
            }
            
            _logger?.LogInformation("[{Symbol}] 📊 Daily Report Sent", state.Symbol);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send daily report");
        }
    }
    
    // Capital Curve History
    private readonly List<(DateTime Time, decimal Capital)> _capitalHistory = new();
    private DateTime _lastCapitalCheckTime = DateTime.MinValue;
    private const int MaxCapitalHistorySize = 100;
    
    /// <summary>
    /// Check Capital Curve Anomaly (Sharp Drop in Short Time)
    /// </summary>
    public async Task CheckCapitalCurveAnomalyAsync(TradingState state)
    {
        var now = DateTime.UtcNow;
        
        // Record once per minute
        if ((now - _lastCapitalCheckTime).TotalMinutes < 1) return;
        _lastCapitalCheckTime = now;
        
        // Add current capital to history
        lock (_capitalHistory)
        {
            _capitalHistory.Add((now, state.Capital));
            
            // Limit history size
            while (_capitalHistory.Count > MaxCapitalHistorySize)
            {
                _capitalHistory.RemoveAt(0);
            }
        }
        
        // Check capital change in the last 1 hour
        var oneHourAgo = now.AddHours(-1);
        var recentHistory = _capitalHistory.Where(h => h.Time >= oneHourAgo).ToList();
        
        if (recentHistory.Count < 5) return;  // Insufficient data
        
        var maxCapitalInHour = recentHistory.Max(h => h.Capital);
        var currentCapital = state.Capital;
        
        // Alert if drop exceeds 5% in 1 hour
        var dropPercent = (maxCapitalInHour - currentCapital) / maxCapitalInHour;
        
        if (dropPercent >= 0.05m)  // 5% threshold
        {
            _logger?.LogWarning("[{Symbol}] ⚠️ Capital Curve Anomaly! Dropped {DropPercent:P2} in 1 hour", 
                state.Symbol, dropPercent);
            
            var message = $"⚠️ Capital Curve Anomaly Alert\n\n" +
                $"[{state.Symbol}] Rapid Capital Drop Detected\n\n" +
                $"• Max in 1h: ${maxCapitalInHour:N2}\n" +
                $"• Current Capital: ${currentCapital:N2}\n" +
                $"• Drop: {dropPercent:P2}\n\n" +
                $"Please check trading strategy and market conditions!";
            
            await SendRiskAlertAsync("Capital Curve Anomaly", message);
        }
        
        // Check for consecutive drops (Last 5 points all decreasing)
        if (recentHistory.Count >= 5)
        {
            var last5 = recentHistory.TakeLast(5).ToList();
            var allDecreasing = true;
            for (int i = 1; i < last5.Count; i++)
            {
                if (last5[i].Capital >= last5[i-1].Capital)
                {
                    allDecreasing = false;
                    break;
                }
            }
            
            if (allDecreasing)
            {
                var dropFromFirst = (last5[0].Capital - last5[^1].Capital) / last5[0].Capital;
                if (dropFromFirst >= 0.02m)  // Consecutive drop over 2%
                {
                    _logger?.LogWarning("[{Symbol}] ⚠️ Capital Continuously Dropping! {Count} consecutive drops, accumulative {Drop:P2}", 
                        state.Symbol, last5.Count, dropFromFirst);
                    
                    var message = $"⚠️ Capital Continuous Drop Alert\n\n" +
                        $"[{state.Symbol}] Capital Continuously Dropping\n\n" +
                        $"• Consecutive Drops: {last5.Count}\n" +
                        $"• Accumulative Drop: {dropFromFirst:P2}\n\n" +
                        $"Recommend pausing trading and checking strategy!";
                    
                    await SendRiskAlertAsync("Capital Continuous Drop", message);
                }
            }
        }
    }
}
