namespace TradingSystem.Console.Trading;

/// <summary>
/// Order Ticket - Tracks the complete lifecycle of an order from placement to execution
/// </summary>
public class OrderTicket
{
    private readonly List<OrderEvent> _events = new();
    private readonly object _lock = new();
    
    /// <summary>Ticket ID (Local)</summary>
    public int TicketId { get; }
    
    /// <summary>Exchange Order ID</summary>
    public long? ExchangeOrderId { get; private set; }
    
    /// <summary>Trading Pair</summary>
    public string Symbol { get; }
    
    /// <summary>Order Direction</summary>
    public OrderDirection Direction { get; }
    
    /// <summary>Order Type</summary>
    public OrderType OrderType { get; }
    
    /// <summary>Requested Quantity</summary>
    public decimal RequestedQuantity { get; }
    
    /// <summary>Filled Quantity</summary>
    public decimal FilledQuantity { get; private set; }
    
    /// <summary>Average Fill Price</summary>
    public decimal AveragePrice { get; private set; }
    
    /// <summary>Order Status</summary>
    public OrderStatus Status { get; private set; }
    
    /// <summary>Created Time</summary>
    public DateTime CreatedTime { get; }
    
    /// <summary>Last Update Time</summary>
    public DateTime LastUpdateTime { get; private set; }
    
    /// <summary>Order Tag/Reason</summary>
    public string Tag { get; set; } = "";
    
    /// <summary>Total Fee</summary>
    public decimal TotalFee { get; private set; }
    
    /// <summary>Total Value</summary>
    public decimal TotalValue => FilledQuantity * AveragePrice;
    
    /// <summary>Remaining Quantity</summary>
    public decimal RemainingQuantity => RequestedQuantity - FilledQuantity;
    
    /// <summary>Is Fully Filled</summary>
    public bool IsFilled => Status == OrderStatus.Filled;
    
    /// <summary>Is Closed (Canceled, Rejected, or Filled)</summary>
    public bool IsClosed => Status == OrderStatus.Canceled || 
                            Status == OrderStatus.Rejected ||
                            Status == OrderStatus.Filled;
    
    /// <summary>List of Order Events</summary>
    public IReadOnlyList<OrderEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToList();
            }
        }
    }
    
    // Static counter for generating local ticket ID
    private static int _nextTicketId = 1;
    
    public OrderTicket(string symbol, OrderDirection direction, decimal quantity, OrderType orderType = OrderType.Market)
    {
        TicketId = Interlocked.Increment(ref _nextTicketId);
        Symbol = symbol;
        Direction = direction;
        RequestedQuantity = quantity;
        OrderType = orderType;
        Status = OrderStatus.Pending;
        CreatedTime = DateTime.UtcNow;
        LastUpdateTime = CreatedTime;
    }
    
    /// <summary>
    /// Update Order Status
    /// </summary>
    public void Update(OrderEvent orderEvent)
    {
        lock (_lock)
        {
            _events.Add(orderEvent);
            
            if (orderEvent.ExchangeOrderId.HasValue)
            {
                ExchangeOrderId = orderEvent.ExchangeOrderId;
            }
            
            Status = orderEvent.Status;
            LastUpdateTime = orderEvent.Time;
            
            if (orderEvent.FilledQuantity > 0)
            {
                // Update Average Price (Weighted Average)
                var totalValue = AveragePrice * FilledQuantity + orderEvent.Price * orderEvent.FilledQuantity;
                FilledQuantity += orderEvent.FilledQuantity;
                AveragePrice = FilledQuantity > 0 ? totalValue / FilledQuantity : 0;
            }
            
            TotalFee += orderEvent.Fee;
        }
    }
    
    /// <summary>
    /// Mark Order Submitted
    /// </summary>
    public void MarkSubmitted(long exchangeOrderId)
    {
        Update(new OrderEvent
        {
            ExchangeOrderId = exchangeOrderId,
            Status = OrderStatus.Submitted,
            Time = DateTime.UtcNow,
            Message = "Order submitted to exchange"
        });
    }
    
    /// <summary>
    /// Mark Partially Filled
    /// </summary>
    public void MarkPartiallyFilled(decimal filledQty, decimal price, decimal fee = 0)
    {
        Update(new OrderEvent
        {
            Status = OrderStatus.PartiallyFilled,
            FilledQuantity = filledQty,
            Price = price,
            Fee = fee,
            Time = DateTime.UtcNow,
            Message = $"Partially filled: {filledQty} @ {price}"
        });
    }
    
    /// <summary>
    /// Mark Fully Filled
    /// </summary>
    public void MarkFilled(decimal filledQty, decimal price, decimal fee = 0)
    {
        Update(new OrderEvent
        {
            Status = OrderStatus.Filled,
            FilledQuantity = filledQty,
            Price = price,
            Fee = fee,
            Time = DateTime.UtcNow,
            Message = $"Fully filled: {filledQty} @ {price}"
        });
    }
    
    /// <summary>
    /// Mark Order Rejected
    /// </summary>
    public void MarkRejected(string reason)
    {
        Update(new OrderEvent
        {
            Status = OrderStatus.Rejected,
            Time = DateTime.UtcNow,
            Message = $"Order rejected: {reason}"
        });
    }
    
    /// <summary>
    /// Mark Order Canceled
    /// </summary>
    public void MarkCanceled(string reason = "")
    {
        Update(new OrderEvent
        {
            Status = OrderStatus.Canceled,
            Time = DateTime.UtcNow,
            Message = string.IsNullOrEmpty(reason) ? "Order canceled" : $"Order canceled: {reason}"
        });
    }
    
    public override string ToString()
    {
        return $"OrderTicket #{TicketId} [{Direction} {Symbol}] " +
               $"Status={Status}, Filled={FilledQuantity}/{RequestedQuantity} @ {AveragePrice:N4}";
    }
}

/// <summary>
/// Order Event
/// </summary>
public class OrderEvent
{
    /// <summary>Exchange Order ID</summary>
    public long? ExchangeOrderId { get; set; }
    
    /// <summary>Order Status</summary>
    public OrderStatus Status { get; set; }
    
    /// <summary>Filled Quantity in this event</summary>
    public decimal FilledQuantity { get; set; }
    
    /// <summary>Fill Price in this event</summary>
    public decimal Price { get; set; }
    
    /// <summary>Fee in this event</summary>
    public decimal Fee { get; set; }
    
    /// <summary>Event Time</summary>
    public DateTime Time { get; set; }
    
    /// <summary>Event Message</summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// Order Status
/// </summary>
public enum OrderStatus
{
    /// <summary>Pending (Created locally, not submitted)</summary>
    Pending,
    
    /// <summary>Submitted (Sent to exchange)</summary>
    Submitted,
    
    /// <summary>Partially Filled</summary>
    PartiallyFilled,
    
    /// <summary>Filled</summary>
    Filled,
    
    /// <summary>Canceled</summary>
    Canceled,
    
    /// <summary>Rejected</summary>
    Rejected
}

/// <summary>
/// Order Direction
/// </summary>
public enum OrderDirection
{
    /// <summary>Buy</summary>
    Buy,
    
    /// <summary>Sell</summary>
    Sell
}

/// <summary>
/// Order Type
/// </summary>
public enum OrderType
{
    /// <summary>Market Order</summary>
    Market,
    
    /// <summary>Limit Order</summary>
    Limit
}
