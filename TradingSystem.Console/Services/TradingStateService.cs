using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TradingSystem.Console.Services;

/// <summary>
/// Trading State Persistence Service
/// Save and restore trading state to prevent loss after restart
/// Linux Version: Atomic write + Backup recovery mechanism
/// </summary>
public class TradingStateService
{
    private readonly string _statePath;
    private readonly object _lock = new();
    private readonly ILogger<TradingStateService>? _logger;

    public TradingStateService(string? fileName = null, string? basePath = null, ILogger<TradingStateService>? logger = null)
    {
        _logger = logger;
        var folder = basePath ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(folder);
        _statePath = Path.Combine(folder, fileName ?? "trading_state.json");

        _logger?.LogInformation("State persistence path: {Path}", _statePath);
    }

    /// <summary>
    /// Save trading state (Atomic write to prevent file corruption)
    /// </summary>
    public void SaveState(TradingState state)
    {
        lock (_lock)
        {
            try
            {
                state.LastUpdated = DateTime.UtcNow;  // Use UTC time (Consistent with Binance)
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                var json = JsonSerializer.Serialize(state, options);

                // Atomic write: Write to temp file first, then rename
                var tempPath = _statePath + ".tmp";
                var backupPath = _statePath + ".bak";

                // Write to temp file
                File.WriteAllText(tempPath, json);

                // If original file exists, backup first
                if (File.Exists(_statePath))
                {
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                    File.Move(_statePath, backupPath);
                }

                // Rename temp file to official file
                File.Move(tempPath, _statePath);

                _logger?.LogDebug("Trading state saved");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save state");

                // Attempt to restore from backup
                var backupPath = _statePath + ".bak";
                if (File.Exists(backupPath) && !File.Exists(_statePath))
                {
                    try
                    {
                        File.Copy(backupPath, _statePath);
                        _logger?.LogInformation("Restored state file from backup");
                    }
                    catch { }
                }
            }
        }
    }

    /// <summary>
    /// Load trading state (Supports recovery from backup)
    /// </summary>
    public TradingState LoadState()
    {
        lock (_lock)
        {
            try
            {
                // First attempt to load main file
                if (File.Exists(_statePath))
                {
                    var json = File.ReadAllText(_statePath);
                    var options = new JsonSerializerOptions
                    {
                        Converters = { new JsonStringEnumConverter() }
                    };
                    var state = JsonSerializer.Deserialize<TradingState>(json, options);
                    if (state != null)
                    {
                        _logger?.LogInformation("Trading state loaded - InPosition: {IsInPosition}, Capital: ${Capital:N2}",
                            state.IsInPosition, state.Capital);
                        return state;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load state");

                // Attempt to restore from backup
                var backupPath = _statePath + ".bak";
                if (File.Exists(backupPath))
                {
                    try
                    {
                        _logger?.LogInformation("Attempting to restore from backup file...");
                        var json = File.ReadAllText(backupPath);
                        var options = new JsonSerializerOptions
                        {
                            Converters = { new JsonStringEnumConverter() }
                        };
                        var state = JsonSerializer.Deserialize<TradingState>(json, options);
                        if (state != null)
                        {
                            _logger?.LogInformation("✓ Restored from backup successfully - InPosition: {IsInPosition}, Capital: ${Capital:N2}",
                                state.IsInPosition, state.Capital);
                            // Restore main file
                            File.Copy(backupPath, _statePath, true);
                            return state;
                        }
                    }
                    catch (Exception backupEx)
                    {
                        _logger?.LogError(backupEx, "Failed to restore from backup");
                    }
                }
            }

            _logger?.LogInformation("Creating new trading state");
            return new TradingState();
        }
    }

    /// <summary>
    /// Clear state
    /// </summary>
    public void ClearState()
    {
        lock (_lock)
        {
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
                _logger?.LogInformation("Trading state cleared");
            }
        }
    }

    /// <summary>
    /// Add trade record
    /// </summary>
    public void AddTradeRecord(TradingState state, TradeRecord record)
    {
        state.TradeHistory.Add(record);

        // Keep only the last 100 records
        if (state.TradeHistory.Count > 100)
        {
            state.TradeHistory.RemoveAt(0);
        }

        SaveState(state);
    }
}

/// <summary>
/// Trading State
/// </summary>
public class TradingState
{
    /// <summary>
    /// Current Symbol
    /// </summary>
    public string Symbol { get; set; } = "BTCUSDT";

    /// <summary>
    /// Time Interval
    /// </summary>
    public string Interval { get; set; } = "1d";

    /// <summary>
    /// Is In Position
    /// </summary>
    public bool IsInPosition { get; set; }

    /// <summary>
    /// Current Position Quantity
    /// </summary>
    public decimal PositionQuantity { get; set; }

    /// <summary>
    /// Entry Price
    /// </summary>
    public decimal EntryPrice { get; set; }

    /// <summary>
    /// Total Entry Cost (including fees, for precise PnL calculation)
    /// </summary>
    public decimal EntryCost { get; set; }

    /// <summary>
    /// Entry Time
    /// </summary>
    public DateTime EntryTime { get; set; }

    /// <summary>
    /// Stop Loss Price at Entry (Fixed Value)
    /// </summary>
    public decimal StopLossPrice { get; set; }

    /// <summary>
    /// Current Capital (USDT)
    /// </summary>
    public decimal Capital { get; set; } = 10000m;

    /// <summary>
    /// Initial Capital
    /// </summary>
    public decimal InitialCapital { get; set; } = 10000m;

    /// <summary>
    /// Allocated Capital (Baseline capital for risk control in multi-asset mode)
    /// Single Asset Mode = InitialCapital
    /// Multi-Asset Mode = Account Balance * Allocation Ratio (Locked at startup)
    /// </summary>
    public decimal AllocatedCapital { get; set; } = 10000m;

    /// <summary>
    /// Is Multi-Asset Pair (Independent capital in multi-asset mode, not synced from exchange balance)
    /// </summary>
    public bool IsMultiAssetMode { get; set; } = false;

    /// <summary>
    /// Peak Capital (For drawdown calculation)
    /// </summary>
    public decimal PeakCapital { get; set; } = 10000m;

    /// <summary>
    /// Highest Price During Position (For trailing stop persistence)
    /// </summary>
    public decimal HighestPrice { get; set; }

    /// <summary>
    /// Max Drawdown
    /// </summary>
    public decimal MaxDrawdown { get; set; }

    /// <summary>
    /// Day Start Capital
    /// </summary>
    public decimal DayStartCapital { get; set; } = 10000m;

    /// <summary>
    /// Current Trade Date
    /// </summary>
    public DateTime CurrentTradeDate { get; set; } = DateTime.UtcNow.Date;

    /// <summary>
    /// Consecutive Stop Loss Count
    /// </summary>
    public int ConsecutiveStopLossCount { get; set; }

    /// <summary>
    /// Waiting For New Signal
    /// </summary>
    public bool WaitingForNewSignal { get; set; }

    /// <summary>
    /// Daily Trade Count
    /// </summary>
    public int DailyTradeCount { get; set; }

    /// <summary>
    /// Daily Win Count
    /// </summary>
    public int DailyWinCount { get; set; }

    /// <summary>
    /// Total Trade Count
    /// </summary>
    public int TotalTradeCount { get; set; }

    /// <summary>
    /// Total Win Count
    /// </summary>
    public int TotalWinCount { get; set; }

    /// <summary>
    /// Daily PnL
    /// </summary>
    public decimal DailyPnL { get; set; }

    /// <summary>
    /// Stop Loss Percent
    /// </summary>
    public decimal StopLossPercent { get; set; } = 0.06m;

    /// <summary>
    /// Position Size Percent (0.1-1.0)
    /// </summary>
    public decimal PositionSizePercent { get; set; } = 1.0m;

    /// <summary>
    /// Is System Enabled
    /// </summary>
    public bool IsSystemEnabled { get; set; } = true;

    /// <summary>
    /// Is Paused By Risk Control
    /// </summary>
    public bool IsRiskPaused { get; set; }

    /// <summary>
    /// Risk Pause Reason
    /// </summary>
    public string? RiskPauseReason { get; set; }

    /// <summary>
    /// Last Updated Time
    /// </summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Last Signal
    /// </summary>
    public string LastSignal { get; set; } = "None";

    /// <summary>
    /// Last Candle Time
    /// </summary>
    public DateTime LastCandleTime { get; set; }

    /// <summary>
    /// Last Trade Time
    /// </summary>
    public DateTime LastTradeTime { get; set; }

    /// <summary>
    /// Trade History
    /// </summary>
    public List<TradeRecord> TradeHistory { get; set; } = new();

    /// <summary>
    /// Check if it is a new day (UTC), reset daily stats
    /// Detect long downtime and reset intraday risk control limits
    /// </summary>
    public void CheckNewDay(DateTime? currentTime = null)
    {
        var todayUtc = (currentTime ?? DateTime.UtcNow).Date;
        if (todayUtc > CurrentTradeDate.Date)
        {
            var daysDiff = (todayUtc - CurrentTradeDate.Date).Days;

            // Detect long downtime
            if (daysDiff > 1)
            {
                // Logger would be injected if needed
                System.Diagnostics.Debug.WriteLine($"⚠️ Long downtime detected: {daysDiff} days ({CurrentTradeDate:yyyy-MM-dd} → {todayUtc:yyyy-MM-dd})");
            }

            // Reset daily stats
            DayStartCapital = Capital;
            CurrentTradeDate = todayUtc;
            DailyTradeCount = 0;
            DailyWinCount = 0;
            DailyPnL = 0;

            // New day requires resetting intraday risk pause
            // Only reset pause caused by "daily loss limit" or "insufficient funds", keep pause caused by "max drawdown limit"
            if (IsRiskPaused && RiskPauseReason != null &&
                (RiskPauseReason.Contains("Daily loss limit") || RiskPauseReason.Contains("Insufficient funds")))
            {
                IsRiskPaused = false;
                RiskPauseReason = null;
            }
        }
    }

    /// <summary>
    /// Update Peak and Drawdown
    /// </summary>
    public void UpdatePeakAndDrawdown(decimal currentValue)
    {
        if (currentValue > PeakCapital)
        {
            PeakCapital = currentValue;
        }

        if (PeakCapital > 0)
        {
            var drawdown = (PeakCapital - currentValue) / PeakCapital;
            if (drawdown > MaxDrawdown)
            {
                MaxDrawdown = drawdown;
            }
        }
    }
}

/// <summary>
/// Trade Record
/// </summary>
public class TradeRecord
{
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";  // BUY / SELL
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Amount { get; set; }
    public DateTime Time { get; set; }
    public string? OrderId { get; set; }
    public bool IsStopLoss { get; set; }
    public decimal? PnL { get; set; }
    public decimal? PnLPercent { get; set; }
}
