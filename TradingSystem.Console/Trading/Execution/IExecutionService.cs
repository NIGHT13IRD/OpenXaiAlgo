namespace TradingSystem.Console.Trading.Execution;

/// <summary>
/// 🔥 Order Execution Result
/// </summary>
public class OrderResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    
    public decimal FilledQuantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal TotalCost { get; set; }
    public decimal Commission { get; set; }
    public string? OrderId { get; set; }
    
    // Remaining Amount/Quantity (Partial Fill)
    public decimal RemainingAmount { get; set; }
    public decimal RemainingQuantity { get; set; }
}

/// <summary>
/// 🔥 Position Info
/// </summary>
public class PositionInfo
{
    public string Symbol { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal AvailableBalance { get; set; }  // USDT
    public decimal AssetValue { get; set; }        // Position Value
    public bool HasPosition => Quantity > 0 && AssetValue > 1m;
}

/// <summary>
/// 🔥 Order Execution Service Interface - Abstract Exchange Interaction
/// </summary>
public interface IExecutionService
{
    /// <summary>
    /// Execute Buy (Market Order, supports retry and partial fill)
    /// </summary>
    /// <param name="symbol">Symbol</param>
    /// <param name="amount">Buy Amount (USDT)</param>
    /// <param name="currentPrice">Current Price (for quantity calculation)</param>
    /// <param name="ct">Cancellation Token</param>
    Task<OrderResult> ExecuteBuyAsync(string symbol, decimal amount, decimal currentPrice, CancellationToken ct = default);
    
    /// <summary>
    /// Execute Sell (Market Order, supports retry and partial fill)
    /// </summary>
    /// <param name="symbol">Symbol</param>
    /// <param name="quantity">Sell Quantity</param>
    /// <param name="reason">Sell Reason (for logging)</param>
    /// <param name="ct">Cancellation Token</param>
    Task<OrderResult> ExecuteSellAsync(string symbol, decimal quantity, string reason, CancellationToken ct = default);
    
    /// <summary>
    /// Get Position Info
    /// </summary>
    Task<PositionInfo> GetPositionAsync(string symbol, CancellationToken ct = default);
    
    /// <summary>
    /// Get Available Balance
    /// </summary>
    Task<decimal> GetAvailableBalanceAsync(string asset, CancellationToken ct = default);
    
    /// <summary>
    /// Get Current Price
    /// </summary>
    Task<decimal> GetCurrentPriceAsync(string symbol, CancellationToken ct = default);
}
