using Microsoft.Extensions.Logging;

namespace TradingSystem.Console.Trading.Data;

/// <summary>
/// 🔥 File System State Storage Implementation
/// Extracts state persistence logic from AssetTradingEngine
/// </summary>
public class FileStateRepository : IStateRepository
{
    private readonly ILogger _logger;
    private readonly string _statesDirectory;
    private static readonly object _stateFileLock = new();
    
    public FileStateRepository(ILogger logger, string? statesDirectory = null)
    {
        _logger = logger;
        _statesDirectory = statesDirectory ?? Path.Combine(AppContext.BaseDirectory, "states");
        
        if (!Directory.Exists(_statesDirectory))
        {
            Directory.CreateDirectory(_statesDirectory);
        }
    }
    
    private string GetStateFilePath(string symbol) =>
        Path.Combine(_statesDirectory, $"{symbol}_state.json");
    
    private string GetKlineFilePath(string symbol) =>
        Path.Combine(_statesDirectory, $"{symbol}_klines.json");
    
    public Task<TradingState?> LoadStateAsync(string symbol)
    {
        try
        {
            var filePath = GetStateFilePath(symbol);
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var state = System.Text.Json.JsonSerializer.Deserialize<TradingState>(json);
                return Task.FromResult(state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Failed to load state", symbol);
        }
        return Task.FromResult<TradingState?>(null);
    }
    
    public Task SaveStateAsync(TradingState state)
    {
        try
        {
            lock (_stateFileLock)
            {
                var filePath = GetStateFilePath(state.Symbol);
                
                // Version Control
                state.StateVersion++;
                state.LastUpdated = DateTime.UtcNow;
                state.LastUpdatedBy = $"FileStateRepository[{state.Symbol}]";
                
                var json = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                
                // Backup Rotation
                if (File.Exists(filePath))
                {
                    RotateBackups(filePath, maxBackups: 3);
                }
                
                // Atomic Write
                var tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
                
                _logger.LogDebug("[{Symbol}] 💾 State saved v{Version}", state.Symbol, state.StateVersion);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Failed to save state", state.Symbol);
        }
        return Task.CompletedTask;
    }
    
    public Task<IList<Candle>?> LoadKlinesAsync(string symbol)
    {
        try
        {
            var filePath = GetKlineFilePath(symbol);
            if (!File.Exists(filePath))
            {
                return Task.FromResult<IList<Candle>?>(null);
            }
            
            var json = File.ReadAllText(filePath);
            var candles = System.Text.Json.JsonSerializer.Deserialize<List<Candle>>(json);
            
            if (candles == null || candles.Count == 0)
            {
                return Task.FromResult<IList<Candle>?>(null);
            }
            
            // Check if data is expired (last candle older than 2 hours)
            var lastCandleTime = candles.Max(c => c.OpenTime);
            if ((DateTime.UtcNow - lastCandleTime).TotalHours > 2)
            {
                _logger.LogInformation("[{Symbol}] 📊 Local Kline data expired, reloading from API", symbol);
                return Task.FromResult<IList<Candle>?>(null);
            }
            
            _logger.LogInformation("[{Symbol}] 📊 Loaded {Count} klines from local (Latest: {LastTime})", 
                symbol, candles.Count, lastCandleTime);
            
            return Task.FromResult<IList<Candle>?>(candles);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Symbol}] Failed to load klines from local", symbol);
            return Task.FromResult<IList<Candle>?>(null);
        }
    }
    
    public Task SaveKlinesAsync(string symbol, IList<Candle> candles)
    {
        try
        {
            if (candles.Count == 0) return Task.CompletedTask;
            
            var filePath = GetKlineFilePath(symbol);
            
            var json = System.Text.Json.JsonSerializer.Serialize(candles, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false // Compact format to save space
            });
            
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
            
            _logger.LogDebug("[{Symbol}] 💾 Saved {Count} klines to local", symbol, candles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Symbol}] Failed to save klines", symbol);
        }
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Backup Rotation: Keep max N backup files
    /// </summary>
    private void RotateBackups(string filePath, int maxBackups)
    {
        try
        {
            // Delete oldest backup
            var oldestBackup = $"{filePath}.bak{maxBackups}";
            if (File.Exists(oldestBackup))
            {
                File.Delete(oldestBackup);
            }
            
            // Rename backups sequentially
            for (int i = maxBackups - 1; i >= 1; i--)
            {
                var current = $"{filePath}.bak{i}";
                var next = $"{filePath}.bak{i + 1}";
                if (File.Exists(current))
                {
                    File.Move(current, next, overwrite: true);
                }
            }
            
            // Current file becomes .bak1
            var firstBackup = $"{filePath}.bak1";
            if (File.Exists(filePath))
            {
                File.Copy(filePath, firstBackup, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backup rotation failed: {FilePath}", filePath);
        }
    }
}
