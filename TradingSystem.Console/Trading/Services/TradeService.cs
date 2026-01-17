using Binance.Net.Clients;
using Microsoft.Extensions.Logging;
using System.Threading;
using TradingSystem.Console.Configuration;
using TradingSystem.Console.Services;

namespace TradingSystem.Console.Trading;

/// <summary>
/// ⚡ Transaction Execution Service
/// Encapsulates the complete flow of buy/sell operations: API calls, retry mechanisms, fee handling, balance synchronization
/// </summary>
public class TradeService
{
    private readonly ILogger _logger;
    private readonly BinanceRestClient _restClient;
    private readonly string _symbol;
    private readonly AssetConfig _config;
    private readonly PositionManager _position;
    private readonly OrderManager _orderManager;
    private readonly AssetRiskManager _riskManager;
    private readonly DingTalkNotificationService? _dingTalkService;
    private readonly TelegramNotificationService? _telegramService;
    
    public TradeService(
        ILogger logger,
        BinanceRestClient restClient,
        AssetConfig config,
        PositionManager position,
        OrderManager orderManager,
        AssetRiskManager riskManager,
        DingTalkNotificationService? dingTalkService = null,
        TelegramNotificationService? telegramService = null)
    {
        _logger = logger;
        _restClient = restClient;
        _symbol = config.Symbol;
        _config = config;
        _position = position;
        _orderManager = orderManager;
        _riskManager = riskManager;
        _dingTalkService = dingTalkService;
        _telegramService = telegramService;
    }
    
    private readonly SemaphoreSlim _tradeLock = new(1, 1);

    /// <summary>
    /// Execute Market Buy
    /// </summary>
    public async Task<bool> ExecuteBuyAsync(decimal currentPrice)
    {
        // Prevent concurrent trades
        if (!await _tradeLock.WaitAsync(0))
        {
            _logger.LogWarning("[{Symbol}] ⚠️ Transaction in progress, skipping this buy request", _symbol);
            return false;
        }

        var startTime = DateTime.UtcNow;
        
        try
        {
            if (_config.Capital <= 0)
            {
                _logger.LogError("[{Symbol}] Insufficient capital, cannot open position", _symbol);
                return false;
            }
            
            // Get actual available balance
            var accountResult = await _restClient.SpotApi.Account.GetAccountInfoAsync();
            if (!accountResult.Success)
            {
                _logger.LogError("[{Symbol}] Failed to get account info", _symbol);
                return false;
            }
            var usdtBalance = accountResult.Data.Balances.FirstOrDefault(b => b.Asset == "USDT");
            var actualBalance = usdtBalance?.Available ?? 0;
            
            var buyAmount = Math.Floor(Math.Min(_config.Capital, actualBalance) * 100m) / 100m;
            
            if (buyAmount < 10m)
            {
                _logger.LogWarning("[{Symbol}] Trade amount too small: ${BuyAmount:N2}", _symbol, buyAmount);
                return false;
            }
            
            _logger.LogInformation("[{Symbol}] ⚡ Market Buy: Amount=${BuyAmount:N2}", _symbol, buyAmount);
            
            var estimatedQuantity = buyAmount / currentPrice;
            var buyTicket = _orderManager.CreateTicket(_symbol, OrderDirection.Buy, estimatedQuantity, OrderType.Market, "Signal Buy");

            var orderResult = await _restClient.SpotApi.Trading.PlaceOrderAsync(
                _symbol,
                Binance.Net.Enums.OrderSide.Buy,
                Binance.Net.Enums.SpotOrderType.Market,
                quoteQuantity: buyAmount);

            if (!orderResult.Success || orderResult.Data.QuantityFilled <= 0)
            {
                _logger.LogError("[{Symbol}] ❌ Buy Failed: {Error}", _symbol, orderResult.Error?.Message);
                return false;
            }

            var filledQty = orderResult.Data.QuantityFilled;
            var avgPrice = orderResult.Data.AverageFillPrice ?? (orderResult.Data.QuoteQuantityFilled / filledQty);
            var totalCost = orderResult.Data.QuoteQuantityFilled;
            var commission = totalCost * 0.001m;
            var orderId = orderResult.Data.Id;
            
            buyTicket.MarkSubmitted(orderId);
            buyTicket.MarkFilled(filledQty, avgPrice, commission);

            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

            // Calculate Stop Loss Price
            var stopLoss = avgPrice * (1 - _config.StopLoss.FixedPercent);
            if (stopLoss <= 0 || stopLoss >= avgPrice)
            {
                stopLoss = avgPrice * 0.94m;
            }
            
            // Update Position Status
            _position.RecordEntry(avgPrice, filledQty, totalCost + commission, stopLoss, orderId);

            _logger.LogInformation("[{Symbol}] ✅ Buy Successful: Qty={Qty:N6} AvgPrice=${AvgPrice:N4} StopLoss=${StopLoss:N4} Elapsed={Elapsed:F0}ms",
                _symbol, filledQty, avgPrice, stopLoss, elapsed);

            // Send Notification
            if (_dingTalkService != null)
            {
                _ = _dingTalkService.SendOrderExecutedAsync(
                    _symbol, "BUY", filledQty, avgPrice, orderId.ToString(), stopLoss, _position.Capital);
            }
            if (_telegramService != null)
            {
                _ = _telegramService.SendOrderExecutedAsync(_symbol, "BUY", filledQty, avgPrice, orderId.ToString());
            }

            // 🔥 Force sync position after buy (Fix fee deduction)
            await VerifyAndSyncPositionAsync();

            _position.SaveState();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Error executing buy", _symbol);
            return false;
        }
        finally
        {
            _tradeLock.Release();
        }
    }
    
    /// <summary>
    /// Execute Market Sell
    /// </summary>
    public async Task<bool> ExecuteSellAsync(string reason)
    {
        if (!_position.IsInPosition) return false;

        // Prevent concurrent trades
        if (!await _tradeLock.WaitAsync(0))
        {
            _logger.LogWarning("[{Symbol}] ⚠️ Transaction in progress, skipping this sell request", _symbol);
            return false;
        }

        var startTime = DateTime.UtcNow;

        try
        {
            var entryPrice = _position.EntryPrice;
            var originalStopLoss = _position.StopLossPrice;
            
            // Get current position quantity (May be updated during retry)
            var sellQuantity = _position.Quantity;

            _logger.LogInformation("[{Symbol}] 🔴 Market Sell: Qty={Qty:N6} Reason={Reason}", _symbol, sellQuantity, reason);

            var sellTicket = _orderManager.CreateTicket(_symbol, OrderDirection.Sell, sellQuantity, OrderType.Market, reason);

            const int maxRetries = 3;
            for (int retry = 0; retry < maxRetries; retry++)
            {
                if (retry > 0)
                {
                    await Task.Delay(500); // Increase retry interval
                    _logger.LogWarning("[{Symbol}] 🔄 Retry Sell ({Retry}/{MaxRetries}) Qty={Qty}", _symbol, retry + 1, maxRetries, sellQuantity);
                }

                var orderResult = await _restClient.SpotApi.Trading.PlaceOrderAsync(
                    _symbol,
                    Binance.Net.Enums.OrderSide.Sell,
                    Binance.Net.Enums.SpotOrderType.Market,
                    sellQuantity);

                if (orderResult.Success && orderResult.Data.QuantityFilled > 0)
                {
                    var filledQty = orderResult.Data.QuantityFilled;
                    var avgPrice = orderResult.Data.AverageFillPrice ?? (orderResult.Data.QuoteQuantityFilled / filledQty);
                    var totalSellValue = orderResult.Data.QuoteQuantityFilled;
                    var commission = totalSellValue * 0.001m;
                    var orderId = orderResult.Data.Id.ToString();

                    sellTicket.MarkSubmitted(orderResult.Data.Id);
                    sellTicket.MarkFilled(filledQty, avgPrice, commission);

                    var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    var pnl = _position.RecordExit(totalSellValue, commission);
                    var pnlPercent = _position.State.EntryCost > 0 ? pnl / _position.State.EntryCost : 0;

                    // Risk Control Callback
                    if (pnl < 0)
                    {
                        _riskManager.OnStopLoss(_position.State, Math.Abs(pnlPercent), entryPrice, originalStopLoss);
                    }
                    else
                    {
                        _riskManager.OnProfitClose(_position.State);
                    }

                    var pnlEmoji = pnl >= 0 ? "💚" : "💔";
                    _logger.LogInformation("[{Symbol}] ✅ Sell Successful: Qty={Qty:N6} @ ${Price:N4} Elapsed={Elapsed:F0}ms",
                        _symbol, filledQty, avgPrice, elapsed);
                    _logger.LogInformation("[{Symbol}] {PnlEmoji} PnL: ${Pnl:F2} ({PnlPercent:P2})",
                        _symbol, pnlEmoji, pnl, pnlPercent);

                    // Send Notification
                    if (_dingTalkService != null)
                    {
                        _ = _dingTalkService.SendOrderExecutedAsync(_symbol, "SELL", filledQty, avgPrice, orderId);
                        if (reason.Contains("Stop") || reason.Contains("Loss"))
                        {
                            _ = _dingTalkService.SendStopLossAsync(_symbol, entryPrice, avgPrice, Math.Abs(pnlPercent));
                        }
                    }
                    if (_telegramService != null)
                    {
                        _ = _telegramService.SendOrderExecutedAsync(_symbol, "SELL", filledQty, avgPrice, orderId);
                        if (reason.Contains("Stop") || reason.Contains("Loss"))
                        {
                            _ = _telegramService.SendStopLossAsync(_symbol, entryPrice, avgPrice, Math.Abs(pnlPercent));
                        }
                    }

                    _position.SaveState();
                    return true;
                }

                // Handle Failure Cases
                var errorMessage = orderResult.Error?.Message ?? "Unknown error";
                _logger.LogWarning("[{Symbol}] Sell Failed: {Error}", _symbol, errorMessage);
                
                // 🔥 Critical Fix: Force sync and retry on insufficient balance
                if (errorMessage.Contains("insufficient balance") || orderResult.Error?.Code == -2010)
                {
                    _logger.LogWarning("[{Symbol}] ⚠️ Insufficient balance detected, force syncing on-chain balance...", _symbol);
                    await VerifyAndSyncPositionAsync();
                    
                    // Update to actual balance
                    if (_position.Quantity < sellQuantity)
                    {
                        sellQuantity = _position.Quantity; // Use new synced quantity
                        _logger.LogInformation("[{Symbol}] 🔄 Corrected sell quantity to {Qty}", _symbol, sellQuantity);
                        
                        // If quantity is too small, give up
                        if (sellQuantity * _position.State.CurrentPrice < 5m) // Assuming min nominal value 5 USDT
                        {
                            _logger.LogError("[{Symbol}] ❌ Remaining quantity too small (${Value:N2}), cannot sell, force clearing position state", _symbol, sellQuantity * _position.State.CurrentPrice);
                            _position.RecordExit(0, 0); // Force close
                            _position.SaveState();
                            return false;
                        }
                    }
                    else
                    {
                         // If position unchanged (or more) after sync, it's another issue, or sync failed
                         // Slightly reduce quantity to handle precision issues (e.g. 99.9%)
                         var reducedQty = Math.Floor(sellQuantity * 0.999m * 10000m) / 10000m; // Keep 4 decimals
                         if (reducedQty < sellQuantity)
                         {
                             sellQuantity = reducedQty;
                             _logger.LogWarning("[{Symbol}] 📉 Attempting to reduce sell quantity to {Qty} (99.9%)", _symbol, sellQuantity);
                         }
                    }
                }
            }

            _logger.LogError("[{Symbol}] ❌ Sell completely failed, retried {MaxRetries} times", _symbol, maxRetries);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Error executing sell", _symbol);
            return false;
        }
        finally
        {
            _tradeLock.Release();
        }
    }
    
    /// <summary>
    /// Verify and Sync Position (For fee correction and reconciliation)
    /// </summary>
    public async Task VerifyAndSyncPositionAsync(CancellationToken ct = default)
    {
        try
        {
            var baseAsset = _symbol.Replace("USDT", "");
            var accountResult = await _restClient.SpotApi.Account.GetAccountInfoAsync(ct: ct);
            
            if (accountResult.Success)
            {
                var assetBalance = accountResult.Data.Balances.FirstOrDefault(b => b.Asset == baseAsset);
                var actualQuantity = assetBalance?.Available ?? 0;
                var currentPrice = _position.State.CurrentPrice;
                var hasActualPosition = actualQuantity * currentPrice > 1m;

                if (_position.IsInPosition && hasActualPosition)
                {
                    // Sync Quantity (Fix fee deduction)
                    if (Math.Abs(actualQuantity - _position.Quantity) > _position.Quantity * 0.0001m)
                    {
                        _logger.LogWarning("[{Symbol}] ⚠️ Sync Position Quantity: {Old} -> {New}", 
                            _symbol, _position.Quantity, actualQuantity);
                        _position.SyncQuantity(actualQuantity);
                    }
                }
                else if (_position.IsInPosition != hasActualPosition)
                {
                    // State out of sync
                    _position.SyncFromExchange(hasActualPosition, actualQuantity, currentPrice, _config.StopLoss.FixedPercent);
                    _position.SaveState();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Error verifying position", _symbol);
        }
    }
    
    /// <summary>
    /// Check Open Orders
    /// </summary>
    public async Task CheckOpenOrdersAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("[{Symbol}] 🔍 Checking open orders...", _symbol);
            
            var openOrdersResult = await _restClient.SpotApi.Trading.GetOpenOrdersAsync(_symbol, ct: ct);
            
            if (!openOrdersResult.Success)
            {
                _logger.LogWarning("[{Symbol}] ⚠️ Failed to get open orders", _symbol);
                return;
            }
            
            var openOrders = openOrdersResult.Data.ToList();
            
            if (openOrders.Count == 0)
            {
                _logger.LogInformation("[{Symbol}] ✅ No open orders", _symbol);
                return;
            }
            
            _logger.LogWarning("[{Symbol}] ⚠️ Found {Count} open orders", _symbol, openOrders.Count);
            
            foreach (var order in openOrders)
            {
                _logger.LogWarning("[{Symbol}]   - ID: {OrderId}, {Side} {Qty} @ {Price}", 
                    _symbol, order.Id, order.Side, order.Quantity, order.Price);
                
                if (order.Side == Binance.Net.Enums.OrderSide.Buy && !_position.IsInPosition)
                {
                    _position.SetPendingBuyOrder(order.Id, order.Price, order.Quantity);
                }
                else if (order.Side == Binance.Net.Enums.OrderSide.Sell && _position.IsInPosition)
                {
                    _position.SetPendingSellOrder(order.Id, order.Price, order.Quantity);
                }
            }
            
            _position.SaveState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Exception checking open orders", _symbol);
        }
    }
    
    /// <summary>
    /// Verify Unconfirmed Order
    /// </summary>
    public async Task VerifyUnconfirmedOrderAsync(CancellationToken ct = default)
    {
        var state = _position.State;
        if (state.LastOrderConfirmed || state.LastOrderRequestTime == null)
        {
            return;
        }
        
        var timeSinceRequest = DateTime.UtcNow - state.LastOrderRequestTime.Value;
        if (timeSinceRequest.TotalHours > 1)
        {
            state.LastOrderConfirmed = true;
            _position.SaveState();
            return;
        }
        
        _logger.LogWarning("[{Symbol}] 🔍 Unconfirmed order request found, querying...", _symbol);
        
        try
        {
            var ordersResult = await _restClient.SpotApi.Trading.GetOrdersAsync(
                _symbol, 
                startTime: state.LastOrderRequestTime.Value.AddMinutes(-5),
                limit: 20,
                ct: ct);
            
            if (!ordersResult.Success) return;
            
            var matchingOrder = ordersResult.Data.FirstOrDefault(o =>
                o.Side.ToString().ToUpper() == state.LastOrderRequestSide &&
                Math.Abs(o.Quantity - state.LastOrderRequestQuantity) < 0.0001m &&
                o.CreateTime >= state.LastOrderRequestTime.Value.AddSeconds(-10));
            
            if (matchingOrder != null && matchingOrder.QuantityFilled > 0)
            {
                state.LastOrderConfirmed = true;
                state.LastConfirmedOrderId = matchingOrder.Id;
                
                if (state.LastOrderRequestSide == "BUY" && !_position.IsInPosition)
                {
                    var avgPrice = matchingOrder.AverageFillPrice ?? state.LastOrderRequestPrice;
                    var stopLoss = avgPrice * (1 - _config.StopLoss.FixedPercent);
                    _position.RecordEntry(avgPrice, matchingOrder.QuantityFilled, 
                        matchingOrder.QuoteQuantityFilled, stopLoss, matchingOrder.Id);
                }
                
                _position.SaveState();
                _logger.LogWarning("[{Symbol}] ✅ Order Confirmed: {OrderId}", _symbol, matchingOrder.Id);
            }
            else
            {
                state.LastOrderConfirmed = true;
                _position.SaveState();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Exception verifying unconfirmed order", _symbol);
        }
    }
}
