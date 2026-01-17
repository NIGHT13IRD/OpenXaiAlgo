using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingSystem.Console.Configuration;

namespace TradingSystem.Console.Trading;

/// <summary>
/// 📊 Market Data Service
/// Responsible for maintaining Kline data, calculating indicators, and managing current price
/// </summary>
public class MarketDataService
{
    private readonly ILogger _logger;
    private readonly BinanceRestClient _restClient;
    private readonly string _symbol;
    private readonly string _interval;
    
    private readonly List<Candle> _candles = new();
    private readonly object _candlesLock = new();
    private const int MaxCandles = 500;
    
    private readonly string _klinesFilePath;
    
    public decimal CurrentPrice { get; private set; }
    public int CandleCount => _candles.Count;
    
    /// <summary>
    /// Get current Kline snapshot (Thread-safe)
    /// </summary>
    public IReadOnlyList<Candle> GetCandles()
    {
        lock (_candlesLock)
        {
            return _candles.ToList();
        }
    }
    
    public MarketDataService(
        ILogger logger,
        BinanceRestClient restClient,
        string symbol,
        string interval)
    {
        _logger = logger;
        _restClient = restClient;
        _symbol = symbol;
        _interval = interval;
        
        _klinesFilePath = Path.Combine(AppContext.BaseDirectory, "states", $"{symbol}_klines.json");
    }
    
    /// <summary>
    /// Initialize Kline Data
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var loadedFromLocal = LoadKlines();
        
        var interval = ParseInterval(_interval);
        var limit = 500;
        DateTime? startTime = null;

        if (loadedFromLocal)
        {
            lock (_candlesLock)
            {
                if (_candles.Count > 0)
                {
                    startTime = _candles.Max(c => c.OpenTime);
                    limit = 1000;
                }
            }
        }

        var klineResult = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(
            _symbol,
            interval,
            startTime: startTime, 
            limit: limit,
            ct: ct);

        if (klineResult.Success)
        {
            lock (_candlesLock)
            {
                if (!loadedFromLocal)
                {
                    _candles.Clear();
                }
                
                foreach (var k in klineResult.Data)
                {
                    var existingIndex = _candles.FindIndex(c => c.OpenTime == k.OpenTime);
                    var candle = new Candle
                    {
                        OpenTime = k.OpenTime,
                        Open = k.OpenPrice,
                        High = k.HighPrice,
                        Low = k.LowPrice,
                        Close = k.ClosePrice,
                        Volume = k.Volume
                    };
                    
                    if (existingIndex >= 0)
                    {
                        _candles[existingIndex] = candle;
                    }
                    else
                    {
                        _candles.Add(candle);
                    }
                }
                
                _candles.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
                while (_candles.Count > MaxCandles)
                {
                    _candles.RemoveAt(0);
                }
            }
            
            _logger.LogInformation("[{Symbol}] 📊 Kline initialization completed: {Count} candles (Local: {Local}, API Filled: {Api})", 
                _symbol, _candles.Count, loadedFromLocal ? "Yes" : "No", klineResult.Data.Count());
            
            SaveKlines();
        }
        
        // Get Current Price
        var priceResult = await _restClient.SpotApi.ExchangeData.GetPriceAsync(_symbol, ct);
        if (priceResult.Success)
        {
            CurrentPrice = priceResult.Data.Price;
        }
    }
    
    /// <summary>
    /// Handle WebSocket Kline Update
    /// </summary>
    /// <returns>true if Kline is closed</returns>
    public bool OnKlineUpdate(IBinanceStreamKline kline)
    {
        var candle = new Candle
        {
            OpenTime = kline.OpenTime,
            Open = kline.OpenPrice,
            High = kline.HighPrice,
            Low = kline.LowPrice,
            Close = kline.ClosePrice,
            Volume = kline.Volume,
            IsClosed = kline.Final
        };

        CurrentPrice = kline.ClosePrice;

        if (kline.Final)
        {
            lock (_candlesLock)
            {
                var existingIndex = _candles.FindIndex(c => c.OpenTime == kline.OpenTime);
                if (existingIndex >= 0)
                {
                    _candles[existingIndex] = candle;
                }
                else
                {
                    _candles.Add(candle);
                    if (_candles.Count > MaxCandles)
                    {
                        _candles.RemoveAt(0);
                    }
                }
            }

            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Backfill Kline after reconnection
    /// </summary>
    public async Task BackfillKlinesAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("[{Symbol}] 🔄 Starting Kline backfill...", _symbol);
            
            DateTime? startTime = null;
            lock (_candlesLock)
            {
                if (_candles.Count > 0)
                {
                    startTime = _candles.Max(c => c.OpenTime);
                }
            }
            
            var interval = ParseInterval(_interval);
            var klineResult = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                _symbol,
                interval,
                startTime: startTime,
                limit: 100,
                ct: ct);

            if (klineResult.Success && klineResult.Data.Any())
            {
                int newCount = 0;
                int updatedCount = 0;
                
                lock (_candlesLock)
                {
                    foreach (var k in klineResult.Data)
                    {
                        var existingIndex = _candles.FindIndex(c => c.OpenTime == k.OpenTime);
                        var candle = new Candle
                        {
                            OpenTime = k.OpenTime,
                            Open = k.OpenPrice,
                            High = k.HighPrice,
                            Low = k.LowPrice,
                            Close = k.ClosePrice,
                            Volume = k.Volume
                        };
                        
                        if (existingIndex >= 0)
                        {
                            _candles[existingIndex] = candle;
                            updatedCount++;
                        }
                        else
                        {
                            _candles.Add(candle);
                            newCount++;
                        }
                    }
                    
                    _candles.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
                    while (_candles.Count > MaxCandles)
                    {
                        _candles.RemoveAt(0);
                    }
                }

                _logger.LogInformation("[{Symbol}] ✅ Kline backfill completed: Added {New}, Updated {Updated}", 
                    _symbol, newCount, updatedCount);
                
                SaveKlines();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Kline backfill failed", _symbol);
        }
    }
    
    /// <summary>
    /// Save Kline to local
    /// </summary>
    public void SaveKlines()
    {
        try
        {
            var dir = Path.GetDirectoryName(_klinesFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            List<Candle> candlesToSave;
            lock (_candlesLock)
            {
                candlesToSave = _candles.TakeLast(200).ToList();
            }
            
            var json = JsonSerializer.Serialize(candlesToSave, new JsonSerializerOptions
            {
                WriteIndented = false
            });
            
            var tempFile = _klinesFilePath + ".tmp";
            File.WriteAllText(tempFile, json);
            
            if (File.Exists(_klinesFilePath))
            {
                File.Delete(_klinesFilePath);
            }
            File.Move(tempFile, _klinesFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Symbol}] Failed to save Kline", _symbol);
        }
    }
    
    /// <summary>
    /// Load Kline from local
    /// </summary>
    private bool LoadKlines()
    {
        try
        {
            if (!File.Exists(_klinesFilePath))
            {
                return false;
            }
            
            var fileInfo = new FileInfo(_klinesFilePath);
            if (fileInfo.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-24))
            {
                _logger.LogWarning("[{Symbol}] Local Kline data expired (over 24h), refetching", _symbol);
                return false;
            }
            
            var json = File.ReadAllText(_klinesFilePath);
            var loadedCandles = JsonSerializer.Deserialize<List<Candle>>(json);
            
            if (loadedCandles != null && loadedCandles.Count > 0)
            {
                lock (_candlesLock)
                {
                    _candles.Clear();
                    _candles.AddRange(loadedCandles);
                }
                
                _logger.LogInformation("[{Symbol}] 📂 Loaded {Count} Klines from local", _symbol, loadedCandles.Count);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Symbol}] Failed to load local Klines", _symbol);
        }
        
        return false;
    }
    
    private static KlineInterval ParseInterval(string interval)
    {
        return interval.ToLower() switch
        {
            "1m" => KlineInterval.OneMinute,
            "3m" => KlineInterval.ThreeMinutes,
            "5m" => KlineInterval.FiveMinutes,
            "15m" => KlineInterval.FifteenMinutes,
            "30m" => KlineInterval.ThirtyMinutes,
            "1h" => KlineInterval.OneHour,
            "4h" => KlineInterval.FourHour,
            "1d" => KlineInterval.OneDay,
            _ => KlineInterval.OneMinute
        };
    }
}
