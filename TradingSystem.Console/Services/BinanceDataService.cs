using Binance.Net.Clients;
using Binance.Net.Enums;
using Microsoft.Extensions.Logging;

namespace TradingSystem.Console.Services;

/// <summary>
/// Binance Data Service - Fetch historical Klines and price data
/// <summary>
/// Binance Data Service
/// </summary>
/// </summary>
public class BinanceDataService
{
    private readonly BinanceRestClient _client;
    private readonly ILogger<BinanceDataService>? _logger;
    private const int MAX_RETRY_ATTEMPTS = 3;
    private const int BASE_RETRY_DELAY_MS = 1000;
    private const int BATCH_REQUEST_DELAY_MS = 200; // Batch request interval (avoid rate limit)
    private const int MAX_KLINES_PER_REQUEST = 1000; // Binance API single request limit

    public BinanceDataService(ILogger<BinanceDataService>? logger = null, bool useTestnet = false)
    {
        _logger = logger;

        if (useTestnet)
        {
            _client = new BinanceRestClient(options =>
            {
                options.Environment = Binance.Net.BinanceEnvironment.Testnet;
            });
            _logger?.LogInformation("BinanceDataService initialized (Testnet)");
        }
        else
        {
            _client = new BinanceRestClient();
            _logger?.LogInformation("BinanceDataService initialized");
        }
    }

    #region Market Data

    public async Task<List<Candle>> GetKlinesAsync(
        string symbol,
        KlineInterval interval,
        int limit = 500)
    {
        return await GetKlinesWithRetryAsync(symbol, interval, limit);
    }

    /// <summary>
    /// Batch fetch historical candle data (supports more than 1000 candles)
    /// </summary>
    public async Task<List<Candle>> GetKlinesBatchAsync(
        string symbol,
        KlineInterval interval,
        int totalLimit,
        IProgress<(int current, int total, string message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (totalLimit <= MAX_KLINES_PER_REQUEST)
        {
            // If not exceeding 1000, single request
            return await GetKlinesAsync(symbol, interval, totalLimit);
        }

        var allCandles = new List<Candle>();
        int batchCount = (int)Math.Ceiling((double)totalLimit / MAX_KLINES_PER_REQUEST);
        DateTime? endTime = null;

        _logger?.LogInformation("Start batch loading: {Symbol} {Interval} total {Total} candles (in {Batches} batches)",
            symbol, interval, totalLimit, batchCount);
        progress?.Report((0, batchCount, "Preparing batch load..."));

        for (int batch = 1; batch <= batchCount; batch++)
        {
            // Check cancellation request
            if (cancellationToken.IsCancellationRequested)
            {
                _logger?.LogInformation("Batch loading cancelled (loaded {Count}/{Total})", allCandles.Count, totalLimit);
                break;
            }

            int batchLimit = Math.Min(MAX_KLINES_PER_REQUEST, totalLimit - allCandles.Count);

            try
            {
                progress?.Report((batch, batchCount, $"Loading batch {batch}/{batchCount}..."));
                _logger?.LogDebug("Batch {Batch}/{Total}: requesting {Limit} candles{EndTime}",
                    batch, batchCount, batchLimit,
                    endTime.HasValue ? $" (until: {endTime.Value:yyyy-MM-dd HH:mm})" : "");

                var result = await _client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    interval,
                    endTime: endTime,
                    limit: batchLimit);

                if (!result.Success)
                {
                    throw new Exception($"Batch {batch} API error: {result.Error?.Message}");
                }

                if (result.Data == null || !result.Data.Any())
                {
                    _logger?.LogWarning("Batch {Batch} returned empty data, stopping load", batch);
                    break;
                }

                // Convert to Candle object
                var batchCandles = result.Data.Select(k => new Candle
                {
                    OpenTime = k.OpenTime,
                    Open = k.OpenPrice,
                    High = k.HighPrice,
                    Low = k.LowPrice,
                    Close = k.ClosePrice,
                    Volume = k.Volume,
                    CloseTime = k.CloseTime,
                    NumberOfTrades = k.TradeCount
                }).OrderBy(c => c.OpenTime).ToList();

                // Set next request end time (get earlier data)
                if (batchCandles.Count > 0)
                {
                    endTime = batchCandles[0].OpenTime.AddMilliseconds(-1);

                    // Add to result (insert at front since we're fetching from newest backwards)
                    allCandles.InsertRange(0, batchCandles);

                    _logger?.LogInformation("✓ Batch {Batch}/{Total} complete: {Count} candles (total: {AllCount}/{TotalLimit}, time range: {Start:yyyy-MM-dd} ~ {End:yyyy-MM-dd})",
                        batch, batchCount, batchCandles.Count, allCandles.Count, totalLimit,
                        batchCandles[0].OpenTime, batchCandles[^1].OpenTime);
                }

                // If enough data fetched, stop
                if (allCandles.Count >= totalLimit)
                {
                    break;
                }

                // Delay between batch requests (avoid rate limiting)
                if (batch < batchCount)
                {
                    await Task.Delay(BATCH_REQUEST_DELAY_MS, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("Batch loading cancelled (loaded {Count}/{Total})", allCandles.Count, totalLimit);
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Batch {Batch}/{Total} loading failed", batch, batchCount);

                // If partial data exists, continue returning
                if (allCandles.Count > 0)
                {
                    _logger?.LogWarning("Partial load successful: {Count}/{Total} candles", allCandles.Count, totalLimit);
                    break;
                }
                throw;
            }
        }

        // Data validation
        var validCandles = ValidateCandles(allCandles);

        _logger?.LogInformation("✅ Batch loading complete: {Count} candles (time range: {Start:yyyy-MM-dd} ~ {End:yyyy-MM-dd})",
            validCandles.Count, validCandles[0].OpenTime, validCandles[^1].OpenTime);

        progress?.Report((batchCount, batchCount, $"✓ Complete! Total {validCandles.Count} candles"));

        return validCandles;
    }

    private async Task<List<Candle>> GetKlinesWithRetryAsync(
        string symbol,
        KlineInterval interval,
        int limit)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MAX_RETRY_ATTEMPTS; attempt++)
        {
            try
            {
                _logger?.LogDebug("Fetching candle data: {Symbol} {Interval} limit={Limit} (attempt {Attempt}/{Max})",
                    symbol, interval, limit, attempt, MAX_RETRY_ATTEMPTS);

                var result = await _client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    interval,
                    limit: limit);

                if (!result.Success)
                {
                    throw new Exception($"API error: {result.Error?.Message}");
                }

                if (result.Data == null || !result.Data.Any())
                {
                    throw new Exception("API returned empty data");
                }

                // Convert to Candle object
                var candles = result.Data.Select(k => new Candle
                {
                    OpenTime = k.OpenTime,
                    Open = k.OpenPrice,
                    High = k.HighPrice,
                    Low = k.LowPrice,
                    Close = k.ClosePrice,
                    Volume = k.Volume,
                    CloseTime = k.CloseTime,
                    NumberOfTrades = k.TradeCount
                }).ToList();

                // Data validation
                var validCandles = ValidateCandles(candles);

                _logger?.LogInformation("✓ Successfully fetched candles: {Symbol} {Interval}, {Count} valid data",
                    symbol, interval, validCandles.Count);
                return validCandles;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger?.LogWarning("Failed to fetch candles (attempt {Attempt}/{Max}): {Error}",
                    attempt, MAX_RETRY_ATTEMPTS, ex.Message);

                if (attempt < MAX_RETRY_ATTEMPTS)
                {
                    // Exponential backoff retry
                    int delayMs = BASE_RETRY_DELAY_MS * (int)Math.Pow(2, attempt - 1);
                    _logger?.LogDebug("Waiting {Delay}ms before retry...", delayMs);
                    await Task.Delay(delayMs);
                }
            }
        }

        // All retries failed
        var errorMsg = $"Failed to fetch candle data ({MAX_RETRY_ATTEMPTS} retries): {lastException?.Message}";
        _logger?.LogError(lastException, errorMsg);
        throw new Exception(errorMsg, lastException);
    }

    public async Task<decimal> GetPriceAsync(string symbol)
    {
        return await GetPriceWithRetryAsync(symbol);
    }

    private async Task<decimal> GetPriceWithRetryAsync(string symbol)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MAX_RETRY_ATTEMPTS; attempt++)
        {
            try
            {
                var result = await _client.SpotApi.ExchangeData.GetPriceAsync(symbol);

                if (!result.Success)
                {
                    throw new Exception($"API error: {result.Error?.Message}");
                }

                if (result.Data.Price <= 0)
                {
                    throw new Exception($"Invalid price: {result.Data.Price}");
                }

                return result.Data.Price;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger?.LogWarning("Failed to fetch price (attempt {Attempt}/{Max}): {Error}",
                    attempt, MAX_RETRY_ATTEMPTS, ex.Message);

                if (attempt < MAX_RETRY_ATTEMPTS)
                {
                    int delayMs = BASE_RETRY_DELAY_MS * attempt;
                    await Task.Delay(delayMs);
                }
            }
        }

        var errorMsg = $"Failed to fetch price ({MAX_RETRY_ATTEMPTS} retries): {lastException?.Message}";
        _logger?.LogError(lastException, errorMsg);
        throw new Exception(errorMsg, lastException);
    }

    /// <summary>
    /// Simple Data Validation
    /// </summary>
    private List<Candle> ValidateCandles(List<Candle> candles)
    {
        // Remove invalid data
        var validCandles = candles.Where(c =>
            c.Open > 0 && c.High > 0 && c.Low > 0 && c.Close > 0 &&
            c.High >= c.Low &&
            c.High >= c.Open && c.High >= c.Close &&
            c.Low <= c.Open && c.Low <= c.Close
        ).ToList();

        var invalidCount = candles.Count - validCandles.Count;
        if (invalidCount > 0)
        {
            _logger?.LogWarning("Data validation: {Invalid} invalid candles removed", invalidCount);
        }

        // Sort by time
        validCandles = validCandles.OrderBy(c => c.OpenTime).ToList();

        // Remove duplicates
        validCandles = validCandles
            .GroupBy(c => c.OpenTime)
            .Select(g => g.First())
            .OrderBy(c => c.OpenTime)
            .ToList();

        return validCandles;
    }

    #endregion
}

/// <summary>
/// Candle Data Model
/// </summary>
public class Candle
{
    public DateTime OpenTime { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public DateTime CloseTime { get; set; }
    public int NumberOfTrades { get; set; }
    public bool IsFinal { get; set; }  // Mark if candle is closed
}
