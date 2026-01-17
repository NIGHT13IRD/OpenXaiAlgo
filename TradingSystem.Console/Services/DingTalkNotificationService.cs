using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace TradingSystem.Console.Services;

/// <summary>
/// DingTalk Robot Notification Service
/// Docs: https://open.dingtalk.com/document/robots/custom-robot-access
/// </summary>
public class DingTalkNotificationService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;
    private readonly string? _secret;
    private bool _isEnabled;
    
    // Retry Config
    private const int MaxRetries = 3;
    private const int InitialRetryDelayMs = 1000;
    
    // Rate Limit Config (DingTalk limit: 20 messages/min)
    private readonly Queue<DateTime> _sendTimes = new();
    private const int MAX_MESSAGES_PER_MINUTE = 20;
    private readonly object _rateLimitLock = new();

    /// <summary>
    /// Create DingTalk Notification Service
    /// </summary>
    /// <param name="webhookUrl">Webhook URL</param>
    /// <param name="secret">Signing secret (optional)</param>
    public DingTalkNotificationService(string webhookUrl, string? secret = null)
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };  // 30 second timeout
        _webhookUrl = webhookUrl;
        _secret = secret;
        _isEnabled = !string.IsNullOrEmpty(webhookUrl);
        
        if (_isEnabled)
        {
            Log.Information("✅ DingTalk Notification Service Initialized");
        }
    }

    /// <summary>
    /// Enable/Disable Notification
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// <summary>
    /// Send Text Message
    /// </summary>
    public async Task<bool> SendTextAsync(string content, bool isAtAll = false)
    {
        if (!_isEnabled) return false;

        var message = new
        {
            msgtype = "text",
            text = new { content },
            at = new { isAtAll }
        };

        return await SendMessageAsync(message);
    }

    /// <summary>
    /// Send Markdown Message
    /// </summary>
    public async Task<bool> SendMarkdownAsync(string title, string content)
    {
        if (!_isEnabled) return false;

        var message = new
        {
            msgtype = "markdown",
            markdown = new { title, text = content }
        };

        return await SendMessageAsync(message);
    }

    /// <summary>
    /// Send Trade Signal Notification
    /// </summary>
    public async Task<bool> SendTradeSignalAsync(string symbol, string action, decimal price, string reason)
    {
        var title = $"📊 Trade Signal: {symbol}";
        var emoji = action == "BUY" ? "🟢" : "🔴";
        var actionText = action == "BUY" ? "Buy Signal" : "Sell Signal";
        var content = $@"### {emoji} {actionText}

- **Symbol**: {symbol}
- **Price**: ${price:N2}
- **Reason**: {reason}
- **Time**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

> Trading Bot · Automated Signal Report";

        return await SendMarkdownAsync(title, content);
    }

    /// <summary>
    /// Send Order Execution Notification
    /// </summary>
    public async Task<bool> SendOrderExecutedAsync(string symbol, string side, decimal quantity, decimal price, string orderId)
    {
        var title = $"✅ Order Executed: {symbol}";
        var emoji = side == "BUY" ? "🟢 Buy" : "🔴 Sell";
        var content = $@"### {emoji} Success

- **Order ID**: {orderId}
- **Symbol**: {symbol}
- **Quantity**: {quantity:N6}
- **Price**: ${price:N2}
- **Amount**: ${quantity * price:N2}
- **Time**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

> Trading Bot · Order Report";

        return await SendMarkdownAsync(title, content);
    }

    /// <summary>
    /// Send Order Execution Notification (with stop loss and remaining capital info)
    /// </summary>
    public async Task<bool> SendOrderExecutedAsync(string symbol, string side, decimal quantity, decimal price, string orderId, decimal? stopLoss, decimal? remainCapital)
    {
        var title = $"✅ Order Executed: {symbol}";
        var emoji = side == "BUY" ? "🟢 Buy" : "🔴 Sell";

        var stopLossText = (stopLoss.HasValue && stopLoss.Value > 0) ? $"- **Stop Loss Price**: ${stopLoss.Value:N4}\n" : "";
        var remainText = remainCapital.HasValue ? $"- **Remaining Capital**: ${remainCapital.Value:N2}\n" : "";

        var content = $@"### {emoji} Success

- **Order ID**: {orderId}
- **Symbol**: {symbol}
- **Quantity**: {quantity:N6}
- **Price**: ${price:N2}
- **Amount**: ${quantity * price:N2}
{stopLossText}{remainText}- **Time**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

> Trading Bot · Order Report";

        return await SendMarkdownAsync(title, content);
    }

    /// <summary>
    /// Send Stop Loss Notification
    /// </summary>
    public async Task<bool> SendStopLossAsync(string symbol, decimal entryPrice, decimal exitPrice, decimal lossPercent)
    {
        var title = $"⚠️ Stop Loss Triggered: {symbol}";
        var content = $@"### 🛑 Stop Loss Executed

- **Symbol**: {symbol}
- **Entry Price**: ${entryPrice:N2}
- **Stop Loss Price**: ${exitPrice:N2}
- **Loss**: {lossPercent:P2}
- **Time**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

> Trading Bot · Risk Control Signal Report";

        return await SendMarkdownAsync(title, content);
    }

    /// <summary>
    /// Send Risk Alert Notification
    /// </summary>
    public async Task<bool> SendRiskAlertAsync(string alertType, string message)
    {
        var title = $"🚨 Risk Alert: {alertType}";
        var content = $@"### ⚠️ {alertType}

{message}

- **Time**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

**Please check system status immediately!**

> Trading Bot · Risk Control Signal Report";

        return await SendMarkdownAsync(title, content);
    }

    /// <summary>
    /// Send Daily Report Notification
    /// </summary>
    public async Task<bool> SendDailyReportAsync(
        string symbol,
        decimal startBalance,
        decimal currentBalance,
        int tradeCount,
        int winCount,
        decimal maxDrawdown)
    {
        var pnl = currentBalance - startBalance;
        var pnlPercent = startBalance > 0 ? pnl / startBalance : 0;
        var emoji = pnl >= 0 ? "📈" : "📉";
        var winRate = tradeCount > 0 ? (decimal)winCount / tradeCount : 0;

        var title = "📊 Daily Trading Report";
        var content = $@"### {emoji} Today's Trading Summary

- **Trading Pair**: {symbol}
- **Starting Capital**: ${startBalance:N2}
- **Current Capital**: ${currentBalance:N2}
- **P&L**: {(pnl >= 0 ? "+" : "")}{pnl:N2} ({(pnlPercent >= 0 ? "+" : "")}{pnlPercent:P2})
- **Max Drawdown**: {maxDrawdown:P2}

**Trading Statistics**
- **Trade Count**: {tradeCount}
- **Winning Trades**: {winCount}
- **Win Rate**: {winRate:P1}

> Trading Bot · Daily Report";

        return await SendMarkdownAsync(title, content);
    }

    /// <summary>
    /// Send System Start Notification
    /// </summary>
    public async Task<bool> SendSystemStartAsync(string symbol, string interval)
    {
        var title = "🚀 System Started";
        var content = $@"### ✅ Trading Bot Started

- **Trading Pair**: {symbol}
- **Timeframe**: {interval}
- **Start Time**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
- **Mode**: Automated Trading

Bot is monitoring market signals...

> Trading Bot · Status Report";

        return await SendMarkdownAsync(title, content);
    }

    /// <summary>
    /// Send System Stop Notification
    /// </summary>
    public async Task<bool> SendSystemStopAsync(string reason)
    {
        var title = "⏹️ System Stopped";
        var content = $@"### ❌ Trading Bot Stopped

- **Reason**: {reason}
- **Stop Time**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

Please check system status.

> Trading Bot · Status Report";

        return await SendMarkdownAsync(title, content);
    }

    /// <summary>
    /// Send Error Notification
    /// </summary>
    public async Task<bool> SendErrorAsync(string errorType, string errorMessage)
    {
        var title = $"❌ System Error: {errorType}";
        var content = $@"### 🔥 An Error Occurred

- **Error Type**: {errorType}
- **Error Message**: {errorMessage}
- **Time**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

**Please check immediately!**

> Trading Bot · Error Report";

        return await SendMarkdownAsync(title, content);
    }

    /// <summary>
    /// Send message internal logic
    /// </summary>
    private async Task<bool> SendMessageAsync(object message)
    {
        // Check rate limit
        if (!CheckRateLimit())
        {
            Log.Warning("⚠️ DingTalk message send frequency exceeded, skipped");
            return false;
        }
        
        // Send with retry
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var url = GetSignedUrl();
                var json = JsonSerializer.Serialize(message);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(url, httpContent);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    // Check DingTalk error code
                    using var doc = JsonDocument.Parse(responseBody);
                    var errcode = doc.RootElement.GetProperty("errcode").GetInt32();
                    
                    if (errcode == 0)
                    {
                        Log.Debug("✅ DingTalk message sent successfully");
                        RecordSendTime();
                        return true;
                    }
                    else
                    {
                        var errmsg = doc.RootElement.GetProperty("errmsg").GetString();
                        Log.Error("❌ DingTalk send failed: {Errmsg} (Attempt {Attempt}/{MaxRetry})", errmsg, attempt, MaxRetries);
                        
                        // If rate limit error, wait longer
                        if (errcode == 130101)  // Frequency limit
                        {
                            await Task.Delay(5000);
                        }
                    }
                }
                else
                {
                    Log.Error("❌ DingTalk HTTP request failed: {StatusCode} (Attempt {Attempt}/{MaxRetry})", response.StatusCode, attempt, MaxRetries);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ DingTalk notification exception (Attempt {Attempt}/{MaxRetry})", attempt, MaxRetries);
            }
            
            // If not last attempt, wait and retry
            if (attempt < MaxRetries)
            {
                await Task.Delay(InitialRetryDelayMs * attempt);  // Exponential backoff
            }
        }
        
        Log.Error("❌ DingTalk message sending ultimately failed after {MaxRetry} retries", MaxRetries);
        return false;
    }
    
    /// <summary>
    /// Check if frequency limit exceeded
    /// </summary>
    private bool CheckRateLimit()
    {
        lock (_rateLimitLock)
        {
            var now = DateTime.UtcNow;
            var oneMinuteAgo = now.AddMinutes(-1);
            
            // Remove records older than one minute
            while (_sendTimes.Count > 0 && _sendTimes.Peek() < oneMinuteAgo)
            {
                _sendTimes.Dequeue();
            }
            
            return _sendTimes.Count < MAX_MESSAGES_PER_MINUTE;
        }
    }
    
    /// <summary>
    /// Record send time
    /// </summary>
    private void RecordSendTime()
    {
        lock (_rateLimitLock)
        {
            _sendTimes.Enqueue(DateTime.UtcNow);
        }
    }

    private string GetSignedUrl()
    {
        if (string.IsNullOrEmpty(_secret))
        {
            return _webhookUrl;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stringToSign = $"{timestamp}\n{_secret}";
        
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        var sign = Uri.EscapeDataString(Convert.ToBase64String(hash));

        return $"{_webhookUrl}&timestamp={timestamp}&sign={sign}";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
