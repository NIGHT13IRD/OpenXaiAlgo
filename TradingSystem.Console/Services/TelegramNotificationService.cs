using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent; // Added for ConcurrentQueue

namespace TradingSystem.Console.Services;

/// <summary>
/// Telegram Bot Notification Service
/// Sends trade signals, order status, and daily reports to a specified Telegram channel/group
/// </summary>
public class TelegramNotificationService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly ILogger<TelegramNotificationService>? _logger;
    private bool _isEnabled;

    // Retry Config
    private const int MaxRetries = 3;
    private const int InitialRetryDelayMs = 1000;

    // Rate Limit Config (Telegram limit: 30 messages/sec max, safer to keep it lower)
    private readonly ConcurrentQueue<DateTime> _sendTimestamps = new(); // Changed to ConcurrentQueue
    private const int MaxMessagesPerSecond = 20; // Renamed and kept the value
    private readonly object _rateLimitLock = new();

    /// <summary>
    /// Create Telegram Notification Service
    /// </summary>
    /// <param name="botToken">Bot Token (Get from @BotFather)</param>
    /// <param name="chatId">Chat ID (Group or User ID)</param>
    /// <param name="logger">Logger</param>
    public TelegramNotificationService(string botToken, string chatId, ILogger<TelegramNotificationService>? logger = null)
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };  // 30-second timeout
        _botToken = botToken;
        _chatId = chatId;
        _logger = logger;
        _isEnabled = !string.IsNullOrEmpty(botToken) && !string.IsNullOrEmpty(chatId);

        if (_isEnabled)
        {
            _logger?.LogInformation("Telegram Notification Service Initialized. Enabled: {Enabled}, ChatId: {ChatId}",
            _isEnabled, _chatId); // Corrected variable names
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
    public async Task<bool> SendTextAsync(string content)
    {
        if (!_isEnabled) return false;

        var message = new
        {
            chat_id = _chatId,
            text = content,
            parse_mode = "HTML"
        };

        return await SendMessageAsync(message);
    }

    /// <summary>
    /// Send Markdown Message (Telegram uses MarkdownV2)
    /// </summary>
    public async Task<bool> SendMarkdownAsync(string title, string content)
    {
        if (!_isEnabled) return false;

        // Convert to HTML format (Telegram HTML is more stable)
        var htmlContent = $"<b>{EscapeHtml(title)}</b>\n\n{MarkdownToHtml(content)}";

        var message = new
        {
            chat_id = _chatId,
            text = htmlContent,
            parse_mode = "HTML"
        };

        return await SendMessageAsync(message);
    }

    /// <summary>
    /// Send Trade Signal Notification
    /// </summary>
    public async Task<bool> SendTradeSignalAsync(string symbol, string action, decimal price, string reason)
    {
        var emoji = action == "BUY" ? "🟢" : "🔴";
        var actionText = action == "BUY" ? "Buy Signal" : "Sell Signal";
        var content = $@"<b>📊 {emoji} {actionText}</b>

• <b>Symbol</b>: {symbol}
• <b>Price</b>: ${price:N2}
• <b>Reason</b>: {reason}
• <b>Time</b>: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

<i>🤖 Trading Bot · Auto Signal Report</i>";

        return await SendTextAsync(content);
    }

    /// <summary>
    /// Send Order Execution Notification
    /// </summary>
    public async Task<bool> SendOrderExecutedAsync(string symbol, string side, decimal quantity, decimal price, string orderId)
    {
        var emoji = side == "BUY" ? "🟢 Buy" : "🔴 Sell";
        var content = $@"<b>✅ {emoji} Successful</b>

• <b>Order ID</b>: {orderId}
• <b>Symbol</b>: {symbol}
• <b>Quantity</b>: {quantity:N6}
• <b>Price</b>: ${price:N2}
• <b>Amount</b>: ${quantity * price:N2}
• <b>Time</b>: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

<i>🤖 Trading Bot · Order Report</i>";

        return await SendTextAsync(content);
    }

    /// <summary>
    /// Send Stop Loss Notification
    /// </summary>
    public async Task<bool> SendStopLossAsync(string symbol, decimal entryPrice, decimal exitPrice, decimal lossPercent)
    {
        var content = $@"<b>🛑 Stop Loss Executed</b>

• <b>Symbol</b>: {symbol}
• <b>Entry Price</b>: ${entryPrice:N2}
• <b>Stop Loss Price</b>: ${exitPrice:N2}
• <b>Loss</b>: {lossPercent:P2}
• <b>Time</b>: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

<i>🤖 Trading Bot · Risk Control Signal Report</i>";

        return await SendTextAsync(content);
    }

    /// <summary>
    /// Send Risk Alert
    /// </summary>
    public async Task<bool> SendRiskAlertAsync(string alertType, string message)
    {
        var content = $@"<b>🚨 Risk Alert: {EscapeHtml(alertType)}</b>

{EscapeHtml(message)}

• <b>Time</b>: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

<b>⚠️ Please check system status immediately!</b>

<i>🤖 Trading Bot · Risk Control Signal Report</i>";

        return await SendTextAsync(content);
    }

    /// <summary>
    /// Send Daily Report
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

        var content = $@"<b>📊 {emoji} Daily Trading Report</b>

<b>📌 Account Status</b>
• Start Balance: ${startBalance:N2}
• Current Balance: ${currentBalance:N2}
• Today's PnL: ${pnl:N2} ({pnlPercent:P2})

<b>📌 Trade Statistics</b>
• Total Trades: {tradeCount}
• Winning Trades: {winCount}
• Win Rate: {(tradeCount > 0 ? (decimal)winCount / tradeCount : 0):P2}
• Max Drawdown: {maxDrawdown:P2}

<b>📌 System Status</b>
• Running Status: ✅ Normal
• Report Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

<i>🤖 Trading Bot · Daily Report</i>";

        return await SendTextAsync(content);
    }

    /// <summary>
    /// Send System Start Notification
    /// </summary>
    public async Task<bool> SendSystemStartAsync(string symbol, string interval)
    {
        var content = $@"<b>🚀 Trading Bot Started</b>

• <b>Trading Pair</b>: {symbol}
• <b>Timeframe</b>: {interval}
• <b>Start Time</b>: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
• <b>Mode</b>: Automated Trading

Bot is monitoring market signals...

<i>🤖 Trading Bot · Status Report</i>";

        return await SendTextAsync(content);
    }

    /// <summary>
    /// Send System Stop Notification
    /// </summary>
    public async Task<bool> SendSystemStopAsync(string reason)
    {
        var content = $@"<b>⏹️ Trading Bot Stopped</b>

• <b>Stop Reason</b>: {EscapeHtml(reason)}
• <b>Stop Time</b>: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

Please check system status.

<i>🤖 Trading Bot · Status Report</i>";

        return await SendTextAsync(content);
    }

    /// <summary>
    /// Send Error Notification
    /// </summary>
    public async Task<bool> SendErrorAsync(string errorType, string errorMessage)
    {
        var content = $@"<b>❌ System Exception: {EscapeHtml(errorType)}</b>

• <b>Exception Type</b>: {EscapeHtml(errorType)}
• <b>Error Message</b>: {EscapeHtml(errorMessage)}
• <b>Occurred Time</b>: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

<b>⚠️ Please check immediately!</b>

<i>🤖 Trading Bot · Exception Report</i>";

        return await SendTextAsync(content);
    }

    private async Task<bool> SendMessageAsync(object message)
    {
        // Check rate limit
        if (!CheckRateLimit())
        {
            _logger?.LogWarning("Telegram message sending frequency exceeded, skipped.");
            return false;
        }

        // Send with retry
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
                var json = JsonSerializer.Serialize(message);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(url, httpContent);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    var ok = doc.RootElement.GetProperty("ok").GetBoolean();

                    if (ok)
                    {
                        _logger?.LogDebug("Telegram message sent successfully");
                        RecordSendTime();
                        return true;
                    }
                    else
                    {
                        var description = doc.RootElement.TryGetProperty("description", out var desc)
                            ? desc.GetString()
                            : "Unknown error";
                        _logger?.LogError("Telegram message sending failed: {Description} (Attempt {Attempt}/{MaxRetries})",
                            description, attempt, MaxRetries);
                    }
                }
                else
                {
                    _logger?.LogError("Telegram HTTP request failed: {StatusCode} (Attempt {Attempt}/{MaxRetries})",
                        response.StatusCode, attempt, MaxRetries);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Telegram notification exception (Attempt {Attempt}/{MaxRetries})", attempt, MaxRetries);
            }

            // If not the last attempt, wait and retry
            if (attempt < MaxRetries)
            {
                await Task.Delay(InitialRetryDelayMs * attempt);
            }
        }

        _logger?.LogError("Telegram message sending ultimately failed after {MaxRetries} retries", MaxRetries);
        return false;
    }

    /// <summary>
    /// Check if rate limit exceeded
    /// </summary>
    private bool CheckRateLimit()
    {
        lock (_rateLimitLock)
        {
            var now = DateTime.UtcNow;
            var oneSecondAgo = now.AddSeconds(-1);

            // Remove records older than one second
            while (_sendTimestamps.TryPeek(out var oldestTime) && oldestTime < oneSecondAgo)
            {
                _sendTimestamps.TryDequeue(out _);
            }

            return _sendTimestamps.Count < MaxMessagesPerSecond;
        }
    }

    /// <summary>
    /// Record Send Time
    /// </summary>
    private void RecordSendTime()
    {
        lock (_rateLimitLock)
        {
            _sendTimestamps.Enqueue(DateTime.UtcNow);
        }
    }

    /// <summary>
    /// HTML Escape
    /// </summary>
    private static string EscapeHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    /// <summary>
    /// Simple Markdown to HTML conversion
    /// </summary>
    private static string MarkdownToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        var result = EscapeHtml(markdown);

        // Convert Header ### -> <b>
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"^###\s*(.+)$", "<b>$1</b>",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        // Convert Bold **text** -> <b>text</b>
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"\*\*(.+?)\*\*", "<b>$1</b>");

        // Convert List - item -> • item
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"^-\s*", "• ",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        return result;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
