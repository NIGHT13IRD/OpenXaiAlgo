using Polly;
using Polly.Retry;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Microsoft.Extensions.Logging;

namespace TradingSystem.Console.Services;

/// <summary>
/// 🔥 Resilience Service - Provides Retry, Circuit Breaker, Timeout strategies for API calls
/// Implemented based on Polly library, ensuring 7x24 hour stable operation
/// </summary>
public static class ResilienceService
{
    #region 🔧 Strategy Configuration Constants

    // Retry Config
    private const int MaxRetryAttempts = 3;
    private const int InitialRetryDelayMs = 1000;  // 1 second
    private const int MaxRetryDelayMs = 30000;     // 30 seconds max

    // Circuit Breaker Config
    private const int CircuitBreakerFailureThreshold = 5;  // Break after 5 failures
    private const int CircuitBreakerDurationSeconds = 60;  // Break for 60 seconds

    // Timeout Config
    private const int DefaultTimeoutSeconds = 30;

    // Binance Specific Error Codes
    private static readonly int[] RetryableErrorCodes =
    {
        -1000, // UNKNOWN
        -1001, // DISCONNECTED
        -1003, // TOO_MANY_REQUESTS (Need to wait and retry)
        -1015, // TOO_MANY_ORDERS
        -1021, // TIMESTAMP (Timestamp issue, retry might fix)
    };

    // Non-retryable error codes (Fail immediately)
    private static readonly int[] NonRetryableErrorCodes =
    {
        -1013, // INVALID_MESSAGE
        -1014, // UNKNOWN_ORDER_COMPOSITION
        -1020, // PRICE_QTY_EXCEED
        -2010, // INSUFFICIENT_BALANCE
        -2011, // CANCEL_REJECTED
        -2013, // NO_SUCH_ORDER
        -2014, // BAD_API_KEY
        -2015, // REJECTED_MBX_KEY
    };

    #endregion

    #region 🚀 Retry Strategy

    /// <summary>
    /// Create Exponential Backoff Retry Strategy
    /// Retry Interval: 1s -> 2s -> 4s (Exponential growth, capped)
    /// </summary>
    public static ResiliencePipeline CreateRetryPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(InitialRetryDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(MaxRetryDelayMs),
                UseJitter = true,  // Add random jitter to prevent thundering herd
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsRetryableException(ex)),
                OnRetry = args =>
                {
                    logger?.LogWarning("🔄 API Retry (Attempt {Attempt}): {Error}, waiting {Delay}ms...",
                        args.AttemptNumber,
                        args.Outcome.Exception?.Message ?? "Unknown Error",
                        args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Create Retry with Timeout Strategy
    /// </summary>
    public static ResiliencePipeline CreateRetryWithTimeoutPipeline(int timeoutSeconds = DefaultTimeoutSeconds, ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                OnTimeout = args =>
                {
                    logger?.LogWarning("⏰ API Call Timeout ({Timeout}s)", timeoutSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(InitialRetryDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(MaxRetryDelayMs),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsRetryableException(ex)),
                OnRetry = args =>
                {
                    logger?.LogWarning("🔄 API Retry (Attempt {Attempt}): {Error}",
                        args.AttemptNumber,
                        args.Outcome.Exception?.Message ?? "Unknown Error");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    #endregion

    #region 🔌 Circuit Breaker Strategy

    /// <summary>
    /// Create Circuit Breaker Strategy
    /// Break for 60 seconds after 5 consecutive failures, preventing avalanche
    /// </summary>
    public static ResiliencePipeline CreateCircuitBreakerPipeline(ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,  // Trigger at 50% failure rate
                MinimumThroughput = CircuitBreakerFailureThreshold,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(CircuitBreakerDurationSeconds),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsRetryableException(ex)),
                OnOpened = args =>
                {
                    logger?.LogError("🔴 Circuit Breaker Opened: API calls paused for {Duration} seconds", CircuitBreakerDurationSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    logger?.LogInformation("🟢 Circuit Breaker Closed: API calls resumed");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    logger?.LogInformation("🟡 Circuit Breaker Half-Open: Testing API connection...");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Create Combined Strategy: Retry + Circuit Breaker + Timeout
    /// This is the recommended complete strategy for production
    /// </summary>
    public static ResiliencePipeline CreateFullResiliencePipeline(int timeoutSeconds = DefaultTimeoutSeconds, ILogger? logger = null)
    {
        return new ResiliencePipelineBuilder()
            // Outer layer: Timeout control
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                OnTimeout = args =>
                {
                    logger?.LogWarning("⏰ API Call Timeout ({Timeout}s)", timeoutSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            // Middle layer: Circuit Breaker
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = CircuitBreakerFailureThreshold,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(CircuitBreakerDurationSeconds),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsRetryableException(ex)),
                OnOpened = args =>
                {
                    logger?.LogError("🔴 Circuit Breaker Opened: API calls paused for {Duration} seconds", CircuitBreakerDurationSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    logger?.LogInformation("🟢 Circuit Breaker Closed: API calls resumed");
                    return ValueTask.CompletedTask;
                }
            })
            // Inner layer: Retry
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(InitialRetryDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(MaxRetryDelayMs),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsRetryableException(ex)),
                OnRetry = args =>
                {
                    logger?.LogWarning("🔄 API Retry (Attempt {Attempt}): {Error}",
                        args.AttemptNumber,
                        args.Outcome.Exception?.Message ?? "Unknown Error");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    #endregion

    #region 🛠️ Helper Methods

    /// <summary>
    /// Check if exception is retryable
    /// </summary>
    private static bool IsRetryableException(Exception ex)
    {
        // Network related exceptions, retryable
        if (ex is HttpRequestException ||
            ex is TaskCanceledException ||
            ex is TimeoutException ||
            ex is System.Net.Sockets.SocketException)
        {
            return true;
        }

        // Check if exception message contains retryable error codes
        var message = ex.Message;
        foreach (var code in RetryableErrorCodes)
        {
            if (message.Contains(code.ToString()))
            {
                return true;
            }
        }

        // Check for non-retryable errors
        foreach (var code in NonRetryableErrorCodes)
        {
            if (message.Contains(code.ToString()))
            {
                return false;
            }
        }

        // Other exceptions retryable by default
        return true;
    }

    /// <summary>
    /// Check if Binance API response is successful
    /// </summary>
    public static bool IsBinanceSuccess<T>(CryptoExchange.Net.Objects.WebCallResult<T> result)
    {
        return result.Success;
    }

    /// <summary>
    /// Extract error code from Binance response
    /// </summary>
    public static int? GetBinanceErrorCode<T>(CryptoExchange.Net.Objects.WebCallResult<T> result)
    {
        if (result.Success) return null;

        // Attempt to parse error code from error message
        var errorMsg = result.Error?.Message ?? "";
        foreach (var code in RetryableErrorCodes.Concat(NonRetryableErrorCodes))
        {
            if (errorMsg.Contains(code.ToString()))
            {
                return code;
            }
        }

        return null;
    }

    #endregion

    #region 📊 Execution Methods

    /// <summary>
    /// Execute async operation with retry strategy
    /// </summary>
    public static async Task<T> ExecuteWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var pipeline = CreateRetryPipeline(logger);
        return await pipeline.ExecuteAsync(
            async token => await action(token),
            cancellationToken);
    }

    /// <summary>
    /// Execute async operation with full resilience strategy
    /// </summary>
    public static async Task<T> ExecuteWithFullResilienceAsync<T>(Func<CancellationToken, Task<T>> action, int timeoutSeconds = DefaultTimeoutSeconds, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var pipeline = CreateFullResiliencePipeline(timeoutSeconds, logger);
        return await pipeline.ExecuteAsync(
            async token => await action(token),
            cancellationToken);
    }

    /// <summary>
    /// Execute async operation with retry strategy (void)
    /// </summary>
    public static async Task ExecuteWithRetryAsync(Func<CancellationToken, Task> action, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var pipeline = CreateRetryPipeline(logger);
        await pipeline.ExecuteAsync(
            async token => { await action(token); return true; },
            cancellationToken);
    }

    #endregion
}

/// <summary>
/// Binance API Wrapper
/// Provides API call methods with resilience strategies
/// </summary>
public static class ResilientBinanceApi
{
    /// <summary>
    /// API Call with Retry
    /// </summary>
    public static async Task<CryptoExchange.Net.Objects.WebCallResult<T>> CallWithRetryAsync<T>(
        Func<Task<CryptoExchange.Net.Objects.WebCallResult<T>>> apiCall,
        ILogger? logger = null,
        int maxRetries = 3)
    {
        var pipeline = ResilienceService.CreateRetryPipeline(logger);

        return await pipeline.ExecuteAsync(async ct =>
        {
            var result = await apiCall();

            // If API call fails and is a retryable error, throw exception to trigger retry
            if (!result.Success)
            {
                var errorCode = ResilienceService.GetBinanceErrorCode(result);
                if (errorCode.HasValue && new[] { -1000, -1001, -1003, -1015, -1021 }.Contains(errorCode.Value))
                {
                    throw new BinanceApiException(result.Error?.Message ?? "Unknown error", errorCode.Value);
                }
            }

            return result;
        }, CancellationToken.None);
    }
}

/// <summary>
/// Binance API Exception
/// </summary>
public class BinanceApiException : Exception
{
    public int ErrorCode { get; }

    public BinanceApiException(string message, int errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }
}
