using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingSystem.Console.Services;

/// <summary>
/// 🔥 HTTP Health Check Service
/// Provides simple HTTP endpoints for monitoring system status
/// Suitable for VPS deployment and external monitoring tool integration
/// </summary>
public class HealthCheckService : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly ILogger<HealthCheckService>? _logger;
    private Task? _listenerTask;
    private bool _isRunning;
    private readonly int _port;

    // System Status (Updated externally)
    private static HealthStatus _currentStatus = new();
    private static readonly object _statusLock = new();

    /// <summary>
    /// Default Port
    /// </summary>
    public const int DefaultPort = 8080;

    public bool IsRunning => _isRunning;

    public HealthCheckService(int port = DefaultPort, ILogger<HealthCheckService>? logger = null)
    {
        _port = port;
        _logger = logger;
        _listener = new HttpListener();
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Start Health Check Service
    /// </summary>
    public Task StartAsync()
    {
        if (_isRunning)
        {
            _logger?.LogWarning("Health check service is already running");
            return Task.CompletedTask;
        }

        try
        {
            _listener.Prefixes.Add($"http://+:{_port}/");
            _listener.Start();
            _isRunning = true;

            _logger?.LogInformation("✅ Health check service started: http://localhost:{Port}/health", _port);

            _listenerTask = Task.Run(() => ListenAsync(_cts.Token));
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            // Permission denied, try localhost
            _logger?.LogWarning("Cannot bind to all interfaces, trying localhost...");

            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            _isRunning = true;

            _logger?.LogInformation("✅ Health check service started (Local only): http://localhost:{Port}/health", _port);

            _listenerTask = Task.Run(() => ListenAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start health check service");
            _isRunning = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop Health Check Service
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning) return;

        try
        {
            _cts.Cancel();
            _listener.Stop();

            if (_listenerTask != null)
            {
                await _listenerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }

            _isRunning = false;
            _logger?.LogInformation("Health check service stopped");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception stopping health check service");
        }
    }

    /// <summary>
    /// Listen for HTTP requests
    /// </summary>
    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = ProcessRequestAsync(context);  // Async processing, no blocking
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Health check request processing exception");
            }
        }
    }

    /// <summary>
    /// Process HTTP request
    /// </summary>
    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var path = request.Url?.AbsolutePath ?? "/";

            switch (path.ToLower())
            {
                case "/health":
                case "/":
                    await HandleHealthCheckAsync(response);
                    break;

                case "/status":
                    await HandleDetailedStatusAsync(response);
                    break;

                case "/metrics":
                    await HandleMetricsAsync(response);
                    break;

                default:
                    response.StatusCode = 404;
                    await WriteResponseAsync(response, new { error = "Not Found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            await WriteResponseAsync(response, new { error = ex.Message });
        }
        finally
        {
            response.Close();
        }
    }

    /// <summary>
    /// Handle Health Check Request
    /// GET /health
    /// </summary>
    private async Task HandleHealthCheckAsync(HttpListenerResponse response)
    {
        var status = GetCurrentStatus();

        // Set HTTP status code based on health status
        response.StatusCode = status.IsHealthy ? 200 : 503;

        var result = new
        {
            status = status.IsHealthy ? "healthy" : "unhealthy",
            timestamp = DateTime.UtcNow.ToString("o"),
            uptime = status.Uptime.ToString(@"d\.hh\:mm\:ss"),
            version = "1.0.0"
        };

        await WriteResponseAsync(response, result);
    }

    /// <summary>
    /// Handle Detailed Status Request
    /// GET /status
    /// </summary>
    private async Task HandleDetailedStatusAsync(HttpListenerResponse response)
    {
        var status = GetCurrentStatus();

        response.StatusCode = 200;

        var result = new
        {
            status = status.IsHealthy ? "healthy" : "unhealthy",
            timestamp = DateTime.UtcNow.ToString("o"),
            uptime = status.Uptime.ToString(@"d\.hh\:mm\:ss"),
            trading = new
            {
                isRunning = status.IsAutoTradingRunning,
                activeSymbols = status.ActiveSymbols,
                lastTradeTime = status.LastTradeTime?.ToString("o"),
                totalTradesToday = status.TotalTradesToday,
                todayPnL = status.TodayPnL
            },
            websocket = new
            {
                isConnected = status.IsWebSocketConnected,
                lastMessageTime = status.LastWebSocketMessage?.ToString("o"),
                reconnectCount = status.WebSocketReconnectCount
            },
            risk = new
            {
                isPaused = status.IsRiskPaused,
                pauseReason = status.RiskPauseReason,
                dailyLossPercent = status.DailyLossPercent,
                maxDrawdown = status.MaxDrawdown
            },
            system = new
            {
                memoryUsageMB = GC.GetTotalMemory(false) / 1024 / 1024,
                threadCount = System.Diagnostics.Process.GetCurrentProcess().Threads.Count
            }
        };

        await WriteResponseAsync(response, result);
    }

    /// <summary>
    /// Handle Prometheus Metrics Request
    /// GET /metrics
    /// </summary>
    private async Task HandleMetricsAsync(HttpListenerResponse response)
    {
        var status = GetCurrentStatus();

        var sb = new StringBuilder();

        // Add Prometheus metrics
        sb.AppendLine("# HELP tradingsystem_up Whether the system is running");
        sb.AppendLine("# TYPE tradingsystem_up gauge");
        sb.AppendLine($"tradingsystem_up {(status.IsHealthy ? 1 : 0)}");

        sb.AppendLine("# HELP tradingsystem_trading_active Whether auto trading is active");
        sb.AppendLine("# TYPE tradingsystem_trading_active gauge");
        sb.AppendLine($"tradingsystem_trading_active {(status.IsAutoTradingRunning ? 1 : 0)}");

        sb.AppendLine("# HELP tradingsystem_websocket_connected Whether WebSocket is connected");
        sb.AppendLine("# TYPE tradingsystem_websocket_connected gauge");
        sb.AppendLine($"tradingsystem_websocket_connected {(status.IsWebSocketConnected ? 1 : 0)}");

        sb.AppendLine("# HELP tradingsystem_trades_today total trades today");
        sb.AppendLine("# TYPE tradingsystem_trades_today counter");
        sb.AppendLine($"tradingsystem_trades_today {status.TotalTradesToday}");

        sb.AppendLine("# HELP tradingsystem_pnl_today PnL today");
        sb.AppendLine("# TYPE tradingsystem_pnl_today gauge");
        sb.AppendLine($"tradingsystem_pnl_today {status.TodayPnL:F2}");

        sb.AppendLine("# HELP tradingsystem_daily_loss_percent Daily loss percentage");
        sb.AppendLine("# TYPE tradingsystem_daily_loss_percent gauge");
        sb.AppendLine($"tradingsystem_daily_loss_percent {status.DailyLossPercent:F4}");

        sb.AppendLine("# HELP tradingsystem_websocket_reconnects WebSocket reconnect count");
        sb.AppendLine("# TYPE tradingsystem_websocket_reconnects counter");
        sb.AppendLine($"tradingsystem_websocket_reconnects {status.WebSocketReconnectCount}");

        sb.AppendLine("# HELP tradingsystem_uptime_seconds Uptime in seconds");
        sb.AppendLine("# TYPE tradingsystem_uptime_seconds counter");
        sb.AppendLine($"tradingsystem_uptime_seconds {status.Uptime.TotalSeconds:F0}");

        sb.AppendLine("# HELP tradingsystem_memory_bytes Memory usage in bytes");
        sb.AppendLine("# TYPE tradingsystem_memory_bytes gauge");
        sb.AppendLine($"tradingsystem_memory_bytes {GC.GetTotalMemory(false)}");

        response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        var buffer = Encoding.UTF8.GetBytes(sb.ToString());
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
    }

    /// <summary>
    /// Write JSON Response
    /// </summary>
    private async Task WriteResponseAsync(HttpListenerResponse response, object data)
    {
        response.ContentType = "application/json; charset=utf-8";
        response.Headers.Add("Access-Control-Allow-Origin", "*");  // Allow CORS

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var buffer = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
    }

    #region 🔄 Status Update Methods (Static, for external calls)

    /// <summary>
    /// Update System Health Status
    /// </summary>
    public static void UpdateStatus(Action<HealthStatus> updateAction)
    {
        lock (_statusLock)
        {
            updateAction(_currentStatus);
        }
    }

    /// <summary>
    /// Get Current Status
    /// </summary>
    public static HealthStatus GetCurrentStatus()
    {
        lock (_statusLock)
        {
            return _currentStatus.Clone();
        }
    }

    /// <summary>
    /// Set Auto Trading Status
    /// </summary>
    public static void SetTradingStatus(bool isRunning, string[]? activeSymbols = null)
    {
        UpdateStatus(s =>
        {
            s.IsAutoTradingRunning = isRunning;
            s.ActiveSymbols = activeSymbols ?? Array.Empty<string>();
        });
    }

    /// <summary>
    /// Set WebSocket Status
    /// </summary>
    public static void SetWebSocketStatus(bool isConnected, int reconnectCount = 0)
    {
        UpdateStatus(s =>
        {
            s.IsWebSocketConnected = isConnected;
            s.LastWebSocketMessage = isConnected ? DateTime.UtcNow : s.LastWebSocketMessage;
            s.WebSocketReconnectCount = reconnectCount;
        });
    }

    /// <summary>
    /// Record Trade
    /// </summary>
    public static void RecordTrade(decimal pnl)
    {
        UpdateStatus(s =>
        {
            s.TotalTradesToday++;
            s.TodayPnL += pnl;
            s.LastTradeTime = DateTime.UtcNow;
        });
    }

    /// <summary>
    /// Set Risk Status
    /// </summary>
    public static void SetRiskStatus(bool isPaused, string? reason = null, decimal dailyLoss = 0, decimal maxDrawdown = 0)
    {
        UpdateStatus(s =>
        {
            s.IsRiskPaused = isPaused;
            s.RiskPauseReason = reason;
            s.DailyLossPercent = dailyLoss;
            s.MaxDrawdown = maxDrawdown;
        });
    }

    /// <summary>
    /// Reset Daily
    /// </summary>
    public static void ResetDaily()
    {
        UpdateStatus(s =>
        {
            s.TotalTradesToday = 0;
            s.TodayPnL = 0;
            s.DailyLossPercent = 0;
        });
    }

    #endregion

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _listener.Close();
            _cts.Dispose();
        }
        catch { }
    }
}

/// <summary>
/// System Health Status
/// </summary>
public class HealthStatus
{
    // Basic Status
    public bool IsHealthy => IsAutoTradingRunning || !IsRiskPaused;
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public TimeSpan Uptime => DateTime.UtcNow - StartTime;

    // Trading Status
    public bool IsAutoTradingRunning { get; set; }
    public string[] ActiveSymbols { get; set; } = Array.Empty<string>();
    public DateTime? LastTradeTime { get; set; }
    public int TotalTradesToday { get; set; }
    public decimal TodayPnL { get; set; }

    // WebSocket Status
    public bool IsWebSocketConnected { get; set; }
    public DateTime? LastWebSocketMessage { get; set; }
    public int WebSocketReconnectCount { get; set; }

    // Risk Status
    public bool IsRiskPaused { get; set; }
    public string? RiskPauseReason { get; set; }
    public decimal DailyLossPercent { get; set; }
    public decimal MaxDrawdown { get; set; }

    public HealthStatus Clone()
    {
        return new HealthStatus
        {
            StartTime = this.StartTime,
            IsAutoTradingRunning = this.IsAutoTradingRunning,
            ActiveSymbols = (string[])this.ActiveSymbols.Clone(),
            LastTradeTime = this.LastTradeTime,
            TotalTradesToday = this.TotalTradesToday,
            TodayPnL = this.TodayPnL,
            IsWebSocketConnected = this.IsWebSocketConnected,
            LastWebSocketMessage = this.LastWebSocketMessage,
            WebSocketReconnectCount = this.WebSocketReconnectCount,
            IsRiskPaused = this.IsRiskPaused,
            RiskPauseReason = this.RiskPauseReason,
            DailyLossPercent = this.DailyLossPercent,
            MaxDrawdown = this.MaxDrawdown
        };
    }
}
