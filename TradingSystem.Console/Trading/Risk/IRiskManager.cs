namespace TradingSystem.Console.Trading;

/// <summary>
/// 🔥 Risk Check Result
/// </summary>
public class RiskCheckResult
{
    public bool IsAllowed { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// 🔥 Risk Manager Interface - Abstract Risk Logic
/// </summary>
public interface IRiskManager
{
    /// <summary>
    /// Check if trading is allowed
    /// </summary>
    RiskCheckResult CanTrade(TradingState state);
    
    /// <summary>
    /// Handle Stop Loss Event
    /// </summary>
    void OnStopLoss(TradingState state, decimal lossPercent, decimal entryPrice, decimal stopLossPrice);
    
    /// <summary>
    /// Handle Profit Close
    /// </summary>
    void OnProfitClose(TradingState state);
    
    /// <summary>
    /// Pause Trading
    /// </summary>
    void PauseTrading(TradingState state, string reason);
    
    /// <summary>
    /// Resume Trading
    /// </summary>
    void ResumeTrading(TradingState state);
    
    /// <summary>
    /// Reset Risk State
    /// </summary>
    void ResetDrawdownPause(TradingState state, bool resetPeakCapital = true);
    
    /// <summary>
    /// Update Peak Capital
    /// </summary>
    void UpdatePeakCapital(TradingState state);
    
    /// <summary>
    /// Daily Reset Check
    /// </summary>
    void CheckNewDay(TradingState state);
    
    /// <summary>
    /// Check and Send Daily Report
    /// </summary>
    Task CheckAndSendDailyReportAsync(TradingState state);
    
    /// <summary>
    /// Check Capital Curve Anomaly
    /// </summary>
    Task CheckCapitalCurveAnomalyAsync(TradingState state);
}
