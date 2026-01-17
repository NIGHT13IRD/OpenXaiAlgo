using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingSystem.Console.Services;

/// <summary>
/// Trade Logger - Records all trade history for analysis
/// Supports CSV append and Daily JSON summary
/// <summary>
/// Trade Logger
/// </summary>
/// </summary>
public class TradeLogger
{
    private readonly string _dataFolder;
    private readonly string _csvPath;
    private readonly string _dailySummaryFolder;
    private readonly object _lock = new();
    private readonly ILogger<TradeLogger>? _logger;

    // CSV Headers
    private const string CsvHeader = "DateTime,Symbol,Side,Quantity,Price,Amount,Fee,PnL,PnLPercent,Capital,Reason,OrderId,IsMultiAsset";

    public TradeLogger(string? basePath = null, ILogger<TradeLogger>? logger = null)
    {
        _logger = logger;
        _dataFolder = basePath ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(_dataFolder);

        _csvPath = Path.Combine(_dataFolder, "trade_history.csv");
        _dailySummaryFolder = Path.Combine(_dataFolder, "daily_summary");
        Directory.CreateDirectory(_dailySummaryFolder);

        // If CSV file does not exist, create and write header
        if (!File.Exists(_csvPath))
        {
            File.WriteAllText(_csvPath, CsvHeader + Environment.NewLine, Encoding.UTF8);
            _logger?.LogInformation("Trade log file created: {Path}", _csvPath);
        }

        _logger?.LogInformation("Trade logger initialized: {Folder}", _dataFolder);
    }

    /// <summary>
    /// Log Buy Trade
    /// </summary>
    public void LogBuy(
        string symbol,
        decimal quantity,
        decimal price,
        decimal amount,
        decimal fee,
        decimal capitalAfter,
        string? orderId = null,
        string? reason = null,
        bool isMultiAsset = false)
    {
        var entry = new TradeLogEntry
        {
            DateTime = DateTime.UtcNow,
            Symbol = symbol,
            Side = "BUY",
            Quantity = quantity,
            Price = price,
            Amount = amount,
            Fee = fee,
            PnL = null,  // No PnL on buy
            PnLPercent = null,
            Capital = capitalAfter,
            Reason = reason ?? "Signal Buy",
            OrderId = orderId ?? "",
            IsMultiAsset = isMultiAsset
        };

        AppendToCsv(entry);
        _logger?.LogInformation("📝 Trade Log: BUY {Symbol} {Quantity:N6} @ ${Price:N4}", symbol, quantity, price);
    }

    /// <summary>
    /// Log Sell Trade
    /// </summary>
    public void LogSell(
        string symbol,
        decimal quantity,
        decimal price,
        decimal amount,
        decimal fee,
        decimal pnl,
        decimal pnlPercent,
        decimal capitalAfter,
        string? orderId = null,
        string? reason = null,
        bool isMultiAsset = false)
    {
        var entry = new TradeLogEntry
        {
            DateTime = DateTime.UtcNow,
            Symbol = symbol,
            Side = "SELL",
            Quantity = quantity,
            Price = price,
            Amount = amount,
            Fee = fee,
            PnL = pnl,
            PnLPercent = pnlPercent,
            Capital = capitalAfter,
            Reason = reason ?? "Signal Sell",
            OrderId = orderId ?? "",
            IsMultiAsset = isMultiAsset
        };

        AppendToCsv(entry);
        UpdateDailySummary(entry);

        var pnlEmoji = pnl >= 0 ? "📈" : "📉";
        _logger?.LogInformation("📝 Trade Log: SELL {Symbol} {Quantity:N6} @ ${Price:N4}, PnL: {Emoji} ${PnL:N2} ({PnLPercent:P2})",
            symbol, quantity, price, pnlEmoji, pnl, pnlPercent);
    }

    /// <summary>
    /// Append to CSV
    /// </summary>
    private void AppendToCsv(TradeLogEntry entry)
    {
        lock (_lock)
        {
            try
            {
                var line = string.Join(",",
                    entry.DateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    entry.Symbol,
                    entry.Side,
                    entry.Quantity.ToString("F8", CultureInfo.InvariantCulture),
                    entry.Price.ToString("F8", CultureInfo.InvariantCulture),
                    entry.Amount.ToString("F4", CultureInfo.InvariantCulture),
                    entry.Fee.ToString("F8", CultureInfo.InvariantCulture),
                    entry.PnL?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                    entry.PnLPercent?.ToString("F6", CultureInfo.InvariantCulture) ?? "",
                    entry.Capital.ToString("F4", CultureInfo.InvariantCulture),
                    EscapeCsvField(entry.Reason),
                    entry.OrderId,
                    entry.IsMultiAsset ? "1" : "0"
                );

                File.AppendAllText(_csvPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to write to trade log CSV");
            }
        }
    }

    /// <summary>
    /// Update Daily Summary
    /// </summary>
    private void UpdateDailySummary(TradeLogEntry entry)
    {
        if (entry.Side != "SELL" || entry.PnL == null) return;

        lock (_lock)
        {
            try
            {
                var today = entry.DateTime.Date;
                var summaryPath = Path.Combine(_dailySummaryFolder, $"{today:yyyy-MM-dd}.json");

                DailySummary summary;

                if (File.Exists(summaryPath))
                {
                    var json = File.ReadAllText(summaryPath);
                    summary = JsonSerializer.Deserialize<DailySummary>(json) ?? new DailySummary { Date = today };
                }
                else
                {
                    summary = new DailySummary { Date = today };
                }

                // Update Stats
                summary.TotalTrades++;
                summary.TotalPnL += entry.PnL.Value;
                summary.TotalFees += entry.Fee;

                if (entry.PnL > 0)
                {
                    summary.WinTrades++;
                    summary.TotalProfit += entry.PnL.Value;
                }
                else
                {
                    summary.LossTrades++;
                    summary.TotalLoss += Math.Abs(entry.PnL.Value);
                }

                summary.WinRate = summary.TotalTrades > 0
                    ? (decimal)summary.WinTrades / summary.TotalTrades
                    : 0;

                summary.ProfitFactor = summary.TotalLoss > 0
                    ? summary.TotalProfit / summary.TotalLoss
                    : (summary.TotalProfit > 0 ? decimal.MaxValue : 0);

                summary.LastUpdated = DateTime.UtcNow;

                // Stats by Symbol
                if (!summary.SymbolStats.ContainsKey(entry.Symbol))
                {
                    summary.SymbolStats[entry.Symbol] = new SymbolDailyStat();
                }
                var symbolStat = summary.SymbolStats[entry.Symbol];
                symbolStat.Trades++;
                symbolStat.PnL += entry.PnL.Value;
                if (entry.PnL > 0) symbolStat.Wins++;

                // Save
                var options = new JsonSerializerOptions { WriteIndented = true };
                var newJson = JsonSerializer.Serialize(summary, options);
                File.WriteAllText(summaryPath, newJson, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to update daily summary");
            }
        }
    }

    /// <summary>
    /// Get summary for specific date
    /// </summary>
    public DailySummary? GetDailySummary(DateTime date)
    {
        var summaryPath = Path.Combine(_dailySummaryFolder, $"{date:yyyy-MM-dd}.json");
        if (!File.Exists(summaryPath)) return null;

        try
        {
            var json = File.ReadAllText(summaryPath);
            return JsonSerializer.Deserialize<DailySummary>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get summary for recent N days
    /// </summary>
    public List<DailySummary> GetRecentSummaries(int days = 7)
    {
        var summaries = new List<DailySummary>();
        var today = DateTime.UtcNow.Date;

        for (int i = 0; i < days; i++)
        {
            var date = today.AddDays(-i);
            var summary = GetDailySummary(date);
            if (summary != null)
            {
                summaries.Add(summary);
            }
        }

        return summaries;
    }

    /// <summary>
    /// Get Overall Stats
    /// </summary>
    public OverallStats GetOverallStats()
    {
        var stats = new OverallStats();

        try
        {
            if (!File.Exists(_csvPath)) return stats;

            var lines = File.ReadAllLines(_csvPath).Skip(1); // Skip Header

            foreach (var line in lines)
            {
                var parts = ParseCsvLine(line);
                if (parts.Length < 9) continue;

                var side = parts[2];
                if (side == "SELL" && decimal.TryParse(parts[7], NumberStyles.Any, CultureInfo.InvariantCulture, out var pnl))
                {
                    stats.TotalTrades++;
                    stats.TotalPnL += pnl;
                    if (pnl > 0)
                    {
                        stats.WinTrades++;
                        stats.TotalProfit += pnl;
                        if (pnl > stats.LargestWin) stats.LargestWin = pnl;
                    }
                    else
                    {
                        stats.LossTrades++;
                        stats.TotalLoss += Math.Abs(pnl);
                        if (Math.Abs(pnl) > stats.LargestLoss) stats.LargestLoss = Math.Abs(pnl);
                    }
                }
            }

            stats.WinRate = stats.TotalTrades > 0 ? (decimal)stats.WinTrades / stats.TotalTrades : 0;
            stats.ProfitFactor = stats.TotalLoss > 0 ? stats.TotalProfit / stats.TotalLoss : 0;
            stats.AveragePnL = stats.TotalTrades > 0 ? stats.TotalPnL / stats.TotalTrades : 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to calculate overall stats");
        }

        return stats;
    }

    /// <summary>
    /// Escape CSV Field
    /// </summary>
    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }

    /// <summary>
    /// Parse CSV Line (Supports quoted fields)
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());

        return result.ToArray();
    }
}

/// <summary>
/// Trade Log Entry
/// </summary>
public class TradeLogEntry
{
    public DateTime DateTime { get; set; }
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";  // BUY / SELL
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Amount { get; set; }
    public decimal Fee { get; set; }
    public decimal? PnL { get; set; }
    public decimal? PnLPercent { get; set; }
    public decimal Capital { get; set; }
    public string Reason { get; set; } = "";
    public string OrderId { get; set; } = "";
    public bool IsMultiAsset { get; set; }
}

/// <summary>
/// Daily Summary
/// </summary>
public class DailySummary
{
    public DateTime Date { get; set; }
    public int TotalTrades { get; set; }
    public int WinTrades { get; set; }
    public int LossTrades { get; set; }
    public decimal WinRate { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal TotalProfit { get; set; }
    public decimal TotalLoss { get; set; }
    public decimal TotalFees { get; set; }
    public decimal ProfitFactor { get; set; }
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Stats by Symbol
    /// </summary>
    public Dictionary<string, SymbolDailyStat> SymbolStats { get; set; } = new();
}

/// <summary>
/// Symbol Daily Stats
/// </summary>
public class SymbolDailyStat
{
    public int Trades { get; set; }
    public int Wins { get; set; }
    public decimal PnL { get; set; }
}

/// <summary>
/// Overall Stats
/// </summary>
public class OverallStats
{
    public int TotalTrades { get; set; }
    public int WinTrades { get; set; }
    public int LossTrades { get; set; }
    public decimal WinRate { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal TotalProfit { get; set; }
    public decimal TotalLoss { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal AveragePnL { get; set; }
    public decimal LargestWin { get; set; }
    public decimal LargestLoss { get; set; }
}
