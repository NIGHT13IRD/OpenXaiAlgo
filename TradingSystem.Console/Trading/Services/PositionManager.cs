using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingSystem.Console.Configuration;

namespace TradingSystem.Console.Trading;

/// <summary>
/// 💰 Position Manager
/// Responsible for maintaining TradingState, calculating PnL, and persisting state
/// </summary>
public class PositionManager
{
    private readonly ILogger _logger;
    private readonly string _symbol;
    private readonly string _stateFilePath;
    private readonly object _stateLock = new();
    
    private TradingState _state;
    
    public TradingState State => _state;
    public bool IsInPosition => _state.IsInPosition;
    public decimal Quantity => _state.Quantity;
    public decimal EntryPrice => _state.EntryPrice;
    public decimal EntryCost => _state.EntryCost;
    public decimal StopLossPrice => _state.StopLossPrice;
    public decimal Capital => _state.Capital;
    public decimal HighestPriceSinceEntry => _state.HighestPriceSinceEntry;
    
    public PositionManager(
        ILogger logger,
        string symbol,
        decimal initialCapital)
    {
        _logger = logger;
        _symbol = symbol;
        _stateFilePath = Path.Combine(AppContext.BaseDirectory, "states", $"{symbol}_state.json");
        
        _state = LoadState() ?? new TradingState
        {
            Symbol = symbol,
            Capital = initialCapital,
            PeakCapital = initialCapital,
            DayStartCapital = initialCapital,
            LastTradeDate = DateTime.UtcNow.Date
        };
        
        _state.CheckNewDay();
    }
    
    /// <summary>
    /// Update Current Price (For display)
    /// </summary>
    public void UpdateCurrentPrice(decimal price)
    {
        _state.CurrentPrice = price;
    }
    
    /// <summary>
    /// Update Current Signal (For display)
    /// </summary>
    public void UpdateCurrentSignal(TradingSignal signal)
    {
        _state.CurrentSignal = signal.ToString();
    }
    
    /// <summary>
    /// Update Highest Price (For trailing stop)
    /// </summary>
    public void UpdateHighestPrice(decimal price)
    {
        if (price > _state.HighestPriceSinceEntry)
        {
            _state.HighestPriceSinceEntry = price;
        }
    }
    
    /// <summary>
    /// Update Stop Loss Price
    /// </summary>
    public void UpdateStopLossPrice(decimal price)
    {
        _state.StopLossPrice = price;
    }
    
    /// <summary>
    /// Record Buy Entry
    /// </summary>
    public void RecordEntry(decimal avgPrice, decimal quantity, decimal cost, decimal stopLossPrice, long orderId)
    {
        lock (_stateLock)
        {
            _state.IsInPosition = true;
            _state.EntryPrice = avgPrice;
            _state.Quantity = quantity;
            _state.EntryCost = cost;
            _state.HighestPriceSinceEntry = avgPrice;
            _state.StopLossPrice = stopLossPrice;
            _state.LastTradeTime = DateTime.UtcNow;
            _state.TodayTrades++;
            _state.LastOrderConfirmed = true;
            _state.LastConfirmedOrderId = orderId;
        }
        
        _logger.LogDebug("[{Symbol}] Record Entry: Qty={Qty}, AvgPrice={Price}, StopLoss={SL}", 
            _symbol, quantity, avgPrice, stopLossPrice);
    }
    
    /// <summary>
    /// Record Sell Exit
    /// </summary>
    /// <returns>PnL of this trade</returns>
    public decimal RecordExit(decimal sellValue, decimal commission)
    {
        decimal pnl;
        
        lock (_stateLock)
        {
            var entryCost = _state.EntryCost;
            pnl = (sellValue - commission) - entryCost;
            
            _state.Capital += pnl;
            _state.IsInPosition = false;
            _state.Quantity = 0;
            _state.StopLossPrice = 0;
            _state.EntryPrice = 0;
            _state.EntryCost = 0;
            _state.HighestPriceSinceEntry = 0;
            _state.TodayPnL += pnl;
            _state.TotalTradeCount++;
            
            if (pnl > 0)
            {
                _state.TodayWinCount++;
                _state.TotalWinCount++;
            }
            
            _state.UpdatePeakCapital();
        }
        
        _logger.LogDebug("[{Symbol}] Record Exit: PnL=${PnL:F2}", _symbol, pnl);
        return pnl;
    }
    
    /// <summary>
    /// Sync Position Quantity (For fee correction)
    /// </summary>
    public void SyncQuantity(decimal actualQuantity)
    {
        lock (_stateLock)
        {
            if (Math.Abs(actualQuantity - _state.Quantity) > _state.Quantity * 0.0001m)
            {
                _logger.LogWarning("[{Symbol}] Sync Position Quantity: {Old} -> {New}", 
                    _symbol, _state.Quantity, actualQuantity);
                _state.Quantity = actualQuantity;
            }
        }
    }
    
    /// <summary>
    /// Sync Position State (From Exchange)
    /// </summary>
    public void SyncFromExchange(bool hasPosition, decimal quantity, decimal currentPrice, decimal stopLossPercent)
    {
        lock (_stateLock)
        {
            if (hasPosition && !_state.IsInPosition)
            {
                _state.IsInPosition = true;
                _state.Quantity = quantity;
                if (_state.EntryPrice <= 0)
                {
                    _state.EntryPrice = currentPrice;
                    _state.StopLossPrice = currentPrice * (1 - stopLossPercent);
                }
                _logger.LogWarning("[{Symbol}] Sync Position: Holding {Qty} @ ${Price}", 
                    _symbol, quantity, _state.EntryPrice);
            }
            else if (!hasPosition && _state.IsInPosition)
            {
                _state.IsInPosition = false;
                _state.Quantity = 0;
                _state.EntryPrice = 0;
                _state.StopLossPrice = 0;
                _logger.LogWarning("[{Symbol}] Sync Position: Cleared", _symbol);
            }
            else if (hasPosition && _state.IsInPosition)
            {
                // Only update quantity
                if (Math.Abs(quantity - _state.Quantity) > _state.Quantity * 0.0001m)
                {
                    _state.Quantity = quantity;
                }
            }
        }
    }
    
    /// <summary>
    /// Set Pending Order Info
    /// </summary>
    public void SetPendingBuyOrder(long orderId, decimal price, decimal quantity)
    {
        _state.PendingBuyOrderId = orderId;
        _state.PendingBuyPrice = price;
        _state.PendingBuyQuantity = quantity;
    }
    
    public void SetPendingSellOrder(long orderId, decimal price, decimal quantity)
    {
        _state.PendingSellOrderId = orderId;
        _state.PendingSellPrice = price;
        _state.PendingSellQuantity = quantity;
    }
    
    public void ClearPendingBuyOrder()
    {
        _state.PendingBuyOrderId = 0;
        _state.PendingBuyPrice = 0;
        _state.PendingBuyQuantity = 0;
    }
    
    public void ClearPendingSellOrder()
    {
        _state.PendingSellOrderId = 0;
        _state.PendingSellPrice = 0;
        _state.PendingSellQuantity = 0;
    }
    
    /// <summary>
    /// Check New Day
    /// </summary>
    public void CheckNewDay()
    {
        _state.CheckNewDay();
    }
    
    /// <summary>
    /// Set Risk Paused
    /// </summary>
    public void SetRiskPaused(bool paused, string? reason = null)
    {
        _state.RiskPaused = paused;
        _state.RiskPauseReason = reason;
    }
    
    /// <summary>
    /// Save State
    /// </summary>
    public void SaveState()
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            TradingState stateToSave;
            lock (_stateLock)
            {
                stateToSave = _state;
            }

            var json = JsonSerializer.Serialize(stateToSave, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var tempFile = _stateFilePath + ".tmp";
            File.WriteAllText(tempFile, json);
            
            RotateBackups(_stateFilePath, 3);
            
            if (File.Exists(_stateFilePath))
            {
                var backupFile = _stateFilePath + $".bak.{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(_stateFilePath, backupFile);
            }

            File.Move(tempFile, _stateFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Failed to save state", _symbol);
        }
    }
    
    /// <summary>
    /// Load State
    /// </summary>
    private TradingState? LoadState()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                var json = File.ReadAllText(_stateFilePath);
                return JsonSerializer.Deserialize<TradingState>(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Symbol}] Failed to load state, using default state", _symbol);
        }
        return null;
    }
    
    /// <summary>
    /// Rotate Backups
    /// </summary>
    private void RotateBackups(string filePath, int maxBackups)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath) ?? ".";
            var fileName = Path.GetFileName(filePath);
            var backupPattern = fileName + ".bak.*";
            
            var backups = Directory.GetFiles(dir, backupPattern)
                .OrderByDescending(f => f)
                .ToList();
            
            while (backups.Count >= maxBackups)
            {
                var oldestBackup = backups.Last();
                File.Delete(oldestBackup);
                backups.RemoveAt(backups.Count - 1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rotate backups");
        }
    }
}
