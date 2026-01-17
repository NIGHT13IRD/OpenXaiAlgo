using Microsoft.Extensions.Logging;

namespace TradingSystem.Console.Trading;

/// <summary>
/// Order Manager - Manages all order tickets
/// <summary>
/// Order Manager
/// Responsible for creation, update and lifecycle maintenance of orders
/// </summary>
/// 
/// Features:
/// - Create and track order tickets
/// - Order lifecycle management (Pending -> Submitted -> Filled/Canceled/Rejected)
/// - Query orders by local ID or exchange ID
/// - Order statistics and cleanup
/// </summary>
public class OrderManager
{
    private readonly Dictionary<int, OrderTicket> _tickets = new();
    private readonly Dictionary<long, OrderTicket> _exchangeIdMap = new();
    private readonly object _lock = new();
    private readonly ILogger<OrderManager>? _logger;
    
    /// <summary>
    /// Order Update Event
    /// </summary>
    public event Action<OrderTicket, OrderEvent>? OnOrderUpdate;
    
    /// <summary>
    /// Order Completed Event (Filled, Canceled, Rejected)
    /// </summary>
    public event Action<OrderTicket>? OnOrderCompleted;
    
    public OrderManager(ILogger<OrderManager>? logger = null)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Create new order ticket
    /// </summary>
    public OrderTicket CreateTicket(string symbol, OrderDirection direction, decimal quantity, 
        OrderType orderType = OrderType.Market, string tag = "")
    {
        var ticket = new OrderTicket(symbol, direction, quantity, orderType)
        {
            Tag = tag
        };
        
        lock (_lock)
        {
            _tickets[ticket.TicketId] = ticket;
        }
        
        _logger?.LogDebug("Created order ticket #{TicketId}: {Direction} {Symbol} {Quantity}", 
            ticket.TicketId, direction, symbol, quantity);
        
        return ticket;
    }
    
    /// <summary>
    /// Get order by local ID
    /// </summary>
    public OrderTicket? GetTicket(int ticketId)
    {
        lock (_lock)
        {
            return _tickets.TryGetValue(ticketId, out var ticket) ? ticket : null;
        }
    }
    
    /// <summary>
    /// Get order by exchange ID
    /// </summary>
    public OrderTicket? GetTicketByExchangeId(long exchangeOrderId)
    {
        lock (_lock)
        {
            return _exchangeIdMap.TryGetValue(exchangeOrderId, out var ticket) ? ticket : null;
        }
    }
    
    /// <summary>
    /// Update order status
    /// </summary>
    public void UpdateTicket(int ticketId, OrderEvent orderEvent)
    {
        OrderTicket? ticket;
        lock (_lock)
        {
            if (!_tickets.TryGetValue(ticketId, out ticket))
                return;
            
            ticket.Update(orderEvent);
            
            // Build exchange ID mapping
            if (orderEvent.ExchangeOrderId.HasValue && !_exchangeIdMap.ContainsKey(orderEvent.ExchangeOrderId.Value))
            {
                _exchangeIdMap[orderEvent.ExchangeOrderId.Value] = ticket;
            }
        }
        
        // Trigger event (outside lock)
        OnOrderUpdate?.Invoke(ticket, orderEvent);
        
        if (ticket.IsClosed)
        {
            OnOrderCompleted?.Invoke(ticket);
            _logger?.LogInformation("Order #{TicketId} Completed: {Status}", ticket.TicketId, ticket.Status);
        }
    }
    
    /// <summary>
    /// Get all active orders (unfinished)
    /// </summary>
    public List<OrderTicket> GetActiveOrders()
    {
        lock (_lock)
        {
            return _tickets.Values.Where(t => !t.IsClosed).ToList();
        }
    }
    
    /// <summary>
    /// Get all orders
    /// </summary>
    public List<OrderTicket> GetAllOrders()
    {
        lock (_lock)
        {
            return _tickets.Values.ToList();
        }
    }
    
    /// <summary>
    /// Get orders by symbol
    /// </summary>
    public List<OrderTicket> GetOrdersBySymbol(string symbol)
    {
        lock (_lock)
        {
            return _tickets.Values.Where(t => t.Symbol == symbol).ToList();
        }
    }
    
    /// <summary>
    /// Get today's order statistics
    /// </summary>
    public OrderStatistics GetTodayStatistics()
    {
        var today = DateTime.UtcNow.Date;
        
        lock (_lock)
        {
            var todayOrders = _tickets.Values
                .Where(t => t.CreatedTime.Date == today)
                .ToList();
            
            return new OrderStatistics
            {
                TotalOrders = todayOrders.Count,
                FilledOrders = todayOrders.Count(t => t.Status == OrderStatus.Filled),
                CanceledOrders = todayOrders.Count(t => t.Status == OrderStatus.Canceled),
                RejectedOrders = todayOrders.Count(t => t.Status == OrderStatus.Rejected),
                TotalBuyValue = todayOrders
                    .Where(t => t.Direction == OrderDirection.Buy && t.IsFilled)
                    .Sum(t => t.TotalValue),
                TotalSellValue = todayOrders
                    .Where(t => t.Direction == OrderDirection.Sell && t.IsFilled)
                    .Sum(t => t.TotalValue),
                TotalFees = todayOrders.Sum(t => t.TotalFee)
            };
        }
    }
    
    /// <summary>
    /// Prune old orders (keep last N days)
    /// </summary>
    public void PruneOldOrders(int keepDays = 7)
    {
        var cutoff = DateTime.UtcNow.AddDays(-keepDays);
        
        lock (_lock)
        {
            var toRemove = _tickets
                .Where(kv => kv.Value.IsClosed && kv.Value.LastUpdateTime < cutoff)
                .Select(kv => kv.Key)
                .ToList();
            
            foreach (var id in toRemove)
            {
                if (_tickets.TryGetValue(id, out var ticket))
                {
                    _tickets.Remove(id);
                    if (ticket.ExchangeOrderId.HasValue)
                    {
                        _exchangeIdMap.Remove(ticket.ExchangeOrderId.Value);
                    }
                }
            }
            
            if (toRemove.Count > 0)
            {
                _logger?.LogInformation("Pruned {Count} old orders older than {Days} days", toRemove.Count, keepDays);
            }
        }
    }
    
    /// <summary>
    /// Clean up old orders (keep meaningful orders, prevent memory leak)
    /// </summary>
    public void CleanupOldOrders(int keepCount = 1000)
    {
        // First prune filled orders older than 7 days
        PruneOldOrders(7);
        
        lock (_lock)
        {
            var oldOrders = _tickets.Values
                .Where(t => t.IsClosed && 
                       (DateTime.UtcNow - t.LastUpdateTime).TotalHours > 24)
                .OrderByDescending(t => t.LastUpdateTime)
                .Skip(keepCount)
                .Select(t => t.TicketId)
                .ToList();
            
            foreach (var ticketId in oldOrders)
            {
                _tickets.Remove(ticketId);
                
                // Also clean up exchange ID mapping
                var mapping = _exchangeIdMap.FirstOrDefault(kv => kv.Value.TicketId == ticketId);
                if (mapping.Value != null)
                {
                    _exchangeIdMap.Remove(mapping.Key);
                }
            }
            
            if (oldOrders.Count > 0)
            {
                _logger?.LogInformation("✓ Pruned {Count} old order tickets (retaining last {Remaining})", 
                    oldOrders.Count, _tickets.Count);
            }
        }
    }
    
    /// <summary>
    /// Get order count
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _tickets.Count;
            }
        }
    }
}

/// <summary>
/// Order Statistics
/// </summary>
public class OrderStatistics
{
    /// <summary>Total Orders</summary>
    public int TotalOrders { get; set; }
    
    /// <summary>Filled Orders</summary>
    public int FilledOrders { get; set; }
    
    /// <summary>Canceled Orders</summary>
    public int CanceledOrders { get; set; }
    
    /// <summary>Rejected Orders</summary>
    public int RejectedOrders { get; set; }
    
    /// <summary>Total Buy Value</summary>
    public decimal TotalBuyValue { get; set; }
    
    /// <summary>Total Sell Value</summary>
    public decimal TotalSellValue { get; set; }
    
    /// <summary>Total Fees</summary>
    public decimal TotalFees { get; set; }
    
    /// <summary>Success Rate</summary>
    public decimal SuccessRate => TotalOrders > 0 
        ? (decimal)FilledOrders / TotalOrders * 100 
        : 0;
}
