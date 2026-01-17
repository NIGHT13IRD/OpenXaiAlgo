using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Microsoft.Extensions.Logging;
using System.Timers;

namespace TradingSystem.Console.Services;

/// <summary>
/// Binance WebSocket Service - Real-time Kline Data Stream
/// Supports auto-reconnect, heartbeat detection, and disconnection recovery
/// <summary>
/// Binance WebSocket Service
/// Responsible for real-time data subscription and connection management
/// </summary>
/// </summary>
public class BinanceWebSocketService : IDisposable
{
    private readonly BinanceSocketClient _socketClient;
    private readonly BinanceRestClient _restClient;
    private readonly bool _useTestnet;
    private readonly string _instanceName;
    private readonly ILogger<BinanceWebSocketService>? _logger;
    private int? _subscriptionId;
    private System.Timers.Timer? _heartbeatTimer;
    private DateTime _lastMessageTime;
    private DateTime _lastKlineTime;
    private int _reconnectAttempts = 0;
    private const int MAX_RECONNECT_ATTEMPTS = 10;
    private const int HEARTBEAT_INTERVAL_MS = 30000; // 30 seconds
    private const int MESSAGE_TIMEOUT_SECONDS = 120; // 2 minutes
    private const int RECOVERY_RETRY_INTERVAL_MS = 600000; // 10 minutes
    private bool _isDisposed = false;
    private bool _isManualDisconnect = false;
    private System.Timers.Timer? _recoveryTimer;

    // Current subscription info (for reconnection)
    private string? _currentSymbol;
    private KlineInterval? _currentInterval;

    public event Action<Candle>? OnKlineUpdate;
    public event Action<List<Candle>>? OnMissingKlinesRecovered;
    public event Action<bool>? OnConnectionStateChanged;
    public event Action<string>? OnError;

    public bool IsConnected => _subscriptionId.HasValue && !_isManualDisconnect;
    public string InstanceName => _instanceName;

    public BinanceWebSocketService(ILogger<BinanceWebSocketService>? logger = null, bool useTestnet = false, string instanceName = "Chart")
    {
        _logger = logger;
        _useTestnet = useTestnet;
        _instanceName = instanceName;

        if (useTestnet)
        {
            _socketClient = new BinanceSocketClient(options =>
            {
                options.Environment = BinanceEnvironment.Testnet;
            });
            _restClient = new BinanceRestClient(options =>
            {
                options.Environment = BinanceEnvironment.Testnet;
            });
            _logger?.LogInformation("[{Instance}] BinanceWebSocketService initialized (Testnet)", _instanceName);
        }
        else
        {
            _socketClient = new BinanceSocketClient();
            _restClient = new BinanceRestClient();
            _logger?.LogInformation("[{Instance}] BinanceWebSocketService initialized", _instanceName);
        }
    }

    /// <summary>
    /// Subscribe to candle data stream
    /// </summary>
    public async Task<bool> SubscribeToKlineAsync(string symbol, KlineInterval interval)
    {
        _currentSymbol = symbol;
        _currentInterval = interval;
        _isManualDisconnect = false;
        _reconnectAttempts = 0;

        _logger?.LogInformation("[{Instance}] Starting WebSocket subscription: {Symbol} {Interval}", _instanceName, symbol, interval);

        var success = await ConnectAsync();

        if (success)
        {
            StartHeartbeat();
        }

        return success;
    }

    /// <summary>
    /// Establish WebSocket connection
    /// </summary>
    private async Task<bool> ConnectAsync()
    {
        if (string.IsNullOrEmpty(_currentSymbol) || !_currentInterval.HasValue)
        {
            _logger?.LogError("Invalid subscription parameters");
            return false;
        }

        try
        {
            // If already subscribed, cancel first
            if (_subscriptionId.HasValue)
            {
                try
                {
                    await _socketClient.UnsubscribeAsync(_subscriptionId.Value);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("Error cancelling old subscription: {Error}", ex.Message);
                }
                _subscriptionId = null;
            }

            _logger?.LogDebug("Connecting WebSocket: {Symbol} {Interval}", _currentSymbol, _currentInterval);

            var result = await _socketClient.SpotApi.ExchangeData.SubscribeToKlineUpdatesAsync(
                _currentSymbol,
                _currentInterval.Value,
                data =>
                {
                    try
                    {
                        _lastMessageTime = DateTime.UtcNow;

                        var klineData = data.Data.Data;

                        // Data backlog detection
                        var localNow = DateTime.UtcNow;
                        var klineOpenTimeUtc = klineData.OpenTime;
                        var processingDelay = (localNow - klineOpenTimeUtc).TotalSeconds;
                        var klineIntervalSeconds = (klineData.CloseTime - klineData.OpenTime).TotalSeconds;

                        if (processingDelay > klineIntervalSeconds * 2)
                        {
                            _logger?.LogWarning("⚠️ Data backlog warning: K-line time={KlineTime} UTC, " +
                                              "processing time={ProcessTime} UTC, delay={Delay:F0}s (exceeds {Threshold}s)",
                                              klineOpenTimeUtc.ToString("HH:mm:ss"), localNow.ToString("HH:mm:ss"),
                                              processingDelay, klineIntervalSeconds * 2);
                        }

                        var candle = new Candle
                        {
                            OpenTime = klineData.OpenTime,
                            Open = klineData.OpenPrice,
                            High = klineData.HighPrice,
                            Low = klineData.LowPrice,
                            Close = klineData.ClosePrice,
                            Volume = klineData.Volume,
                            CloseTime = klineData.CloseTime,
                            NumberOfTrades = klineData.TradeCount,
                            IsFinal = klineData.Final
                        };

                        // Log final candles
                        if (candle.IsFinal)
                        {
                            var receiveTime = DateTime.UtcNow;
                            var delay = (receiveTime - candle.CloseTime).TotalSeconds;
                            _logger?.LogInformation("📥 Received final K-line: OpenTime={OpenTime:HH:mm}, CloseTime={CloseTime:HH:mm:ss}, " +
                                                   "receive time={ReceiveTime:HH:mm:ss.fff}, close delay={Delay:F1}s",
                                                   candle.OpenTime, candle.CloseTime, receiveTime, delay);
                        }

                        // Data validation
                        if (ValidateCandle(candle, out string? errorMessage))
                        {
                            _lastKlineTime = candle.OpenTime;
                            OnKlineUpdate?.Invoke(candle);
                        }
                        else
                        {
                            _logger?.LogWarning("WebSocket received invalid candle data: {Error}", errorMessage);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing WebSocket message");
                    }
                });

            if (result.Success)
            {
                _subscriptionId = result.Data.Id;
                var previousKlineTime = _lastKlineTime;
                var wasReconnecting = _reconnectAttempts > 0;
                _lastMessageTime = DateTime.UtcNow;
                _reconnectAttempts = 0;
                StopRecoveryTimer();

                _logger?.LogInformation("[{Instance}] ✓ WebSocket connected: {Symbol} {Interval}", _instanceName, _currentSymbol, _currentInterval);
                OnConnectionStateChanged?.Invoke(true);

                // Recover missing K-lines after reconnection
                if (previousKlineTime != DateTime.MinValue && wasReconnecting)
                {
                    try
                    {
                        _logger?.LogInformation("[{Instance}] ⏳ Waiting for K-line recovery...", _instanceName);
                        await RecoverMissingKlinesAsync(previousKlineTime);
                        _logger?.LogInformation("[{Instance}] ✓ K-line recovery complete, safe to calculate signals", _instanceName);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to recover missing K-lines");
                    }
                }

                return true;
            }
            else
            {
                _logger?.LogError("[{Instance}] WebSocket connection failed: {Error}", _instanceName, result.Error?.Message);
                OnConnectionStateChanged?.Invoke(false);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WebSocket connection exception");
            OnConnectionStateChanged?.Invoke(false);
            return false;
        }
    }

    /// <summary>
    /// Start heartbeat monitoring
    /// </summary>
    private void StartHeartbeat()
    {
        StopHeartbeat();

        _heartbeatTimer = new System.Timers.Timer(HEARTBEAT_INTERVAL_MS);
        _heartbeatTimer.Elapsed += OnHeartbeatCheck;
        _heartbeatTimer.AutoReset = true;
        _heartbeatTimer.Start();

        _logger?.LogDebug("[{Instance}] Heartbeat monitoring started ({Symbol})", _instanceName, _currentSymbol);
    }

    /// <summary>
    /// Stop heartbeat monitoring
    /// </summary>
    private void StopHeartbeat()
    {
        if (_heartbeatTimer != null)
        {
            _heartbeatTimer.Stop();
            _heartbeatTimer.Dispose();
            _heartbeatTimer = null;
            _logger?.LogDebug("Heartbeat monitoring stopped");
        }
    }

    /// <summary>
    /// Start recovery retry timer (retry after 10 minutes)
    /// </summary>
    private void StartRecoveryTimer()
    {
        StopRecoveryTimer();

        _recoveryTimer = new System.Timers.Timer(RECOVERY_RETRY_INTERVAL_MS);
        _recoveryTimer.Elapsed += OnRecoveryTimerElapsed;
        _recoveryTimer.AutoReset = false;
        _recoveryTimer.Start();

        _logger?.LogInformation("🔄 Recovery timer started, will retry connection in {Minutes} minutes", RECOVERY_RETRY_INTERVAL_MS / 60000);
    }

    /// <summary>
    /// Stop recovery retry timer
    /// </summary>
    private void StopRecoveryTimer()
    {
        if (_recoveryTimer != null)
        {
            _recoveryTimer.Stop();
            _recoveryTimer.Dispose();
            _recoveryTimer = null;
        }
    }

    /// <summary>
    /// Recovery timer elapsed callback
    /// </summary>
    private async void OnRecoveryTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_isDisposed || _isManualDisconnect)
            return;

        _logger?.LogInformation("🔄 Recovery timer triggered, retrying WebSocket connection...");
        OnError?.Invoke("🔄 Attempting to restore WebSocket connection...");

        _reconnectAttempts = 0;

        var success = await ConnectAsync();

        if (success)
        {
            _logger?.LogInformation("✅ WebSocket connection restored!");
            OnError?.Invoke("✅ WebSocket connection restored, data stream normal");
            StartHeartbeat();
        }
        else
        {
            await ReconnectAsync();
        }
    }

    /// <summary>
    /// Heartbeat check callback
    /// </summary>
    private async void OnHeartbeatCheck(object? sender, ElapsedEventArgs e)
    {
        if (_isDisposed || _isManualDisconnect)
            return;

        var timeSinceLastMessage = (DateTime.UtcNow - _lastMessageTime).TotalSeconds;

        if (timeSinceLastMessage > MESSAGE_TIMEOUT_SECONDS)
        {
            _logger?.LogWarning("[{Instance}] ⚠️ WebSocket timeout: {Seconds:F0}s without data ({Symbol})",
                               _instanceName, timeSinceLastMessage, _currentSymbol);

            await ReconnectAsync();
        }
        else
        {
            _logger?.LogDebug("[{Instance}] Heartbeat: {Seconds:F0}s ({Symbol})", _instanceName, timeSinceLastMessage, _currentSymbol);
        }
    }

    /// <summary>
    /// Reconnect
    /// </summary>
    private async Task ReconnectAsync()
    {
        if (_isDisposed || _isManualDisconnect)
            return;

        if (_reconnectAttempts >= MAX_RECONNECT_ATTEMPTS)
        {
            var errorMsg = $"WebSocket reconnection failed: max retry attempts reached ({MAX_RECONNECT_ATTEMPTS})";
            _logger?.LogError(errorMsg);
            OnError?.Invoke(errorMsg);
            StopHeartbeat();

            StartRecoveryTimer();
            OnError?.Invoke("⚠️ WebSocket temporarily unavailable, will auto-retry in 10 minutes");
            return;
        }

        _reconnectAttempts++;

        // Exponential backoff
        int delayMs = Math.Min(1000 * (int)Math.Pow(2, _reconnectAttempts - 1), 30000);
        _logger?.LogInformation("[{Instance}] 🔄 Preparing to reconnect WebSocket (attempt {Attempt}/{Max}), symbol={Symbol}, interval={Interval}, waiting {Delay}ms...",
                               _instanceName, _reconnectAttempts, MAX_RECONNECT_ATTEMPTS, _currentSymbol, _currentInterval, delayMs);

        if (_reconnectAttempts == 1)
        {
            OnError?.Invoke($"⚠️ [{_instanceName}] WebSocket disconnected ({_currentSymbol} {_currentInterval}), attempting reconnection...");
        }

        await Task.Delay(delayMs);

        if (_isDisposed || _isManualDisconnect)
            return;

        var success = await ConnectAsync();

        if (success)
        {
            _logger?.LogInformation("[{Instance}] ✅ WebSocket reconnected: {Symbol} {Interval} (after {Attempts} attempts)",
                                   _instanceName, _currentSymbol, _currentInterval, _reconnectAttempts);
            OnError?.Invoke($"✅ [{_instanceName}] WebSocket reconnected: {_currentSymbol} {_currentInterval}, data stream restored");
        }
        else if (_reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
        {
            await ReconnectAsync();
        }
    }

    /// <summary>
    /// Unsubscribe
    /// </summary>
    public async Task UnsubscribeAsync()
    {
        _isManualDisconnect = true;
        StopHeartbeat();

        if (_subscriptionId.HasValue)
        {
            try
            {
                await _socketClient.UnsubscribeAsync(_subscriptionId.Value);
                _logger?.LogInformation("WebSocket subscription cancelled");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Error during unsubscribe: {Error}", ex.Message);
            }
            _subscriptionId = null;
        }

        OnConnectionStateChanged?.Invoke(false);
    }

    /// <summary>
    /// Recover missing K-lines during disconnection
    /// </summary>
    private async Task RecoverMissingKlinesAsync(DateTime fromTime)
    {
        if (string.IsNullOrEmpty(_currentSymbol) || !_currentInterval.HasValue)
            return;

        try
        {
            _logger?.LogInformation("Starting K-line recovery: from {FromTime:yyyy-MM-dd HH:mm:ss} UTC", fromTime);

            var result = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                _currentSymbol,
                _currentInterval.Value,
                startTime: fromTime,
                limit: 100
            );

            if (result.Success && result.Data.Any())
            {
                var missingCandles = result.Data
                    .Where(k => k.OpenTime > fromTime)
                    .Select(k => new Candle
                    {
                        OpenTime = k.OpenTime,
                        Open = k.OpenPrice,
                        High = k.HighPrice,
                        Low = k.LowPrice,
                        Close = k.ClosePrice,
                        Volume = k.Volume,
                        CloseTime = k.CloseTime,
                        NumberOfTrades = k.TradeCount
                    })
                    .ToList();

                if (missingCandles.Any())
                {
                    _logger?.LogInformation("Recovered {Count} missing K-lines", missingCandles.Count);

                    OnMissingKlinesRecovered?.Invoke(missingCandles);

                    foreach (var candle in missingCandles.OrderBy(c => c.OpenTime))
                    {
                        OnKlineUpdate?.Invoke(candle);
                    }
                }
                else
                {
                    _logger?.LogInformation("No missing K-lines during disconnection");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to recover missing K-lines");
            throw;
        }
    }

    /// <summary>
    /// Get historical K-lines (for external calls)
    /// </summary>
    public async Task<List<Candle>> GetHistoricalKlinesAsync(string symbol, KlineInterval interval, int limit = 500)
    {
        try
        {
            var result = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                symbol,
                interval,
                limit: limit
            );

            if (result.Success)
            {
                return result.Data.Select(k => new Candle
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
            }

            _logger?.LogError("Failed to get historical K-lines: {Error}", result.Error?.Message);
            return new List<Candle>();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception getting historical K-lines");
            return new List<Candle>();
        }
    }

    /// <summary>
    /// Validate candle data
    /// </summary>
    private bool ValidateCandle(Candle candle, out string? errorMessage)
    {
        errorMessage = null;

        if (candle.Open <= 0 || candle.High <= 0 || candle.Low <= 0 || candle.Close <= 0)
        {
            errorMessage = "Price values must be positive";
            return false;
        }

        if (candle.High < candle.Low)
        {
            errorMessage = "High must be >= Low";
            return false;
        }

        if (candle.High < candle.Open || candle.High < candle.Close)
        {
            errorMessage = "High must be >= Open and Close";
            return false;
        }

        if (candle.Low > candle.Open || candle.Low > candle.Close)
        {
            errorMessage = "Low must be <= Open and Close";
            return false;
        }

        if (candle.Volume < 0)
        {
            errorMessage = "Volume cannot be negative";
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _isManualDisconnect = true;

        StopHeartbeat();
        StopRecoveryTimer();

        try
        {
            _socketClient.Dispose();
            _restClient.Dispose();
            _logger?.LogInformation("BinanceWebSocketService disposed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error disposing WebSocket resources");
        }
    }
}
