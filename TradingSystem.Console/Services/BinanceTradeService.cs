using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace TradingSystem.Console.Services;

/// <summary>
/// Binance Trade Service
/// Responsible for executing orders and querying order status
/// </summary>
public class BinanceTradeService : IDisposable
{
    private readonly BinanceRestClient _client;
    private readonly ILogger<BinanceTradeService>? _logger;
    private readonly DingTalkNotificationService? _notification;
    private readonly bool _isTestMode;
    private readonly bool _useTestnet;

    // Trading Parameters
    public decimal DefaultCommission { get; set; } = 0.001m;  // 0.1% Commission

    // Public Test Mode Property
    public bool IsTestMode => _isTestMode;
    public bool UseTestnet => _useTestnet;

    // Cache symbol precision (Use ConcurrentDictionary for thread safety)
    private readonly ConcurrentDictionary<string, (int pricePrecision, int quantityPrecision)> _symbolPrecisionCache = new();

    // 🔥 [New] Cache symbol filter info (MinNotional, StepSize, TickSize)
    private readonly ConcurrentDictionary<string, SymbolFilterInfo> _symbolFilterCache = new();

    // 🔥 ClientOrderId Prefix (To identify orders from this system)
    private const string CLIENT_ORDER_PREFIX = "TS_";
    private static int _orderSequence = 0;

    /// <summary>
    /// Generate unique ClientOrderId
    /// Format: TS_timestamp_sequence
    /// </summary>
    private static string GenerateClientOrderId()
    {
        var sequence = Interlocked.Increment(ref _orderSequence);
        return $"{CLIENT_ORDER_PREFIX}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{sequence}";
    }

    /// <summary>
    /// Create Trade Service
    /// </summary>
    public BinanceTradeService(
        string apiKey,
        string apiSecret,
        bool isTestMode = true,
        bool useTestnet = false,
        DingTalkNotificationService? notification = null,
        ILogger<BinanceTradeService>? logger = null)
    {
        _logger = logger;
        _isTestMode = isTestMode;
        _useTestnet = useTestnet;
        _notification = notification;

        _client = new BinanceRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            options.AutoTimestamp = true;
            options.ReceiveWindow = TimeSpan.FromSeconds(15);
            if (useTestnet)
            {
                options.Environment = BinanceEnvironment.Testnet;
            }
        });

        var modeText = useTestnet ? "Testnet" : (isTestMode ? "Simulated Trading" : "Live Trading");
        _logger?.LogInformation("Binance Trade Service initialized ({Mode})", modeText);
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var timeSyncOk = await CheckTimeSyncAsync();
            if (!timeSyncOk)
            {
                _logger?.LogWarning("⚠️ Time sync check failed, API calls may be affected");
            }

            var result = await _client.SpotApi.Account.GetAccountInfoAsync();

            if (result.Success)
            {
                _logger?.LogInformation("Binance API Connected Successfully");
                return true;
            }
            else
            {
                _logger?.LogError("Binance API Connection Failed: {Error}", result.Error?.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Binance API Connection Exception");
            return false;
        }
    }

    public async Task<bool> CheckTimeSyncAsync()
    {
        try
        {
            var result = await _client.SpotApi.ExchangeData.GetServerTimeAsync();
            if (!result.Success)
            {
                _logger?.LogWarning("Failed to get Binance server time: {Error}", result.Error?.Message);
                return false;
            }

            var serverTime = result.Data;
            var localTime = DateTime.UtcNow;
            var timeDiff = Math.Abs((serverTime - localTime).TotalMilliseconds);

            _logger?.LogInformation("⏱️ Time Sync Check: Local={Local:HH:mm:ss.fff} Server={Server:HH:mm:ss.fff} Diff={Diff:F0}ms",
                localTime, serverTime, timeDiff);

            if (timeDiff > 1000)
            {
                _logger?.LogError("❌ Time sync anomalous! Diff={Diff:F0}ms (>1000ms)", timeDiff);
                _logger?.LogError("Please check VPS/System time, or enable NTP auto-sync!");
                return false;
            }
            else if (timeDiff > 500)
            {
                _logger?.LogWarning("⚠️ Time diff slightly high: {Diff:F0}ms, verify system time", timeDiff);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Time Sync Check Failed");
            return false;
        }
    }

    public async Task<decimal> GetBalanceAsync(string asset = "USDT")
    {
        try
        {
            var result = await _client.SpotApi.Account.GetAccountInfoAsync();
            if (result.Success)
            {
                var balance = result.Data.Balances.FirstOrDefault(b => b.Asset == asset);
                return balance?.Available ?? 0m;
            }
            else
            {
                _logger?.LogError("Failed to get balance: {Error}", result.Error?.Message);
                return 0m;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Get Balance Exception");
            return 0m;
        }
    }

    public async Task<decimal> GetPriceAsync(string symbol)
    {
        try
        {
            var result = await _client.SpotApi.ExchangeData.GetPriceAsync(symbol);
            if (result.Success)
            {
                return result.Data.Price;
            }
            else
            {
                _logger?.LogError("Failed to get price: {Error}", result.Error?.Message);
                return 0m;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Get Price Exception");
            return 0m;
        }
    }

    public async Task<TradeExecutionResult> MarketBuyAsync(string symbol, decimal quoteAmount, decimal? referencePrice = null)
    {
        _logger?.LogInformation("Execute Market Buy: {Symbol}, Amount: ${Amount:N2}", symbol, quoteAmount);

        if (string.IsNullOrEmpty(symbol))
        {
            return new TradeExecutionResult { Success = false, ErrorMessage = "Symbol cannot be empty" };
        }
        if (quoteAmount < 10m)
        {
            return new TradeExecutionResult { Success = false, ErrorMessage = $"Trade amount too small: ${quoteAmount:N2}, Min $10" };
        }

        if (_isTestMode)
        {
            return await SimulateBuyAsync(symbol, quoteAmount, referencePrice);
        }

        var clientOrderId = GenerateClientOrderId();

        try
        {
            var balance = await GetBalanceAsync("USDT");
            if (balance < quoteAmount)
            {
                _logger?.LogError("Insufficient USDT Balance: Available={Balance}, Required={Amount}", balance, quoteAmount);
                return new TradeExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"Insufficient Balance: Available ${balance:N2}, Required ${quoteAmount:N2}"
                };
            }

            var formattedAmount = FormatQuoteAmount(quoteAmount);
            _logger?.LogInformation("Formatted Buy Amount: {Original} -> {Formatted}", quoteAmount, formattedAmount);
            _logger?.LogInformation("Buy Order ClientOrderId: {ClientOrderId}", clientOrderId);

            var result = await _client.SpotApi.Trading.PlaceOrderAsync(
                symbol,
                OrderSide.Buy,
                SpotOrderType.Market,
                quoteQuantity: formattedAmount,
                newClientOrderId: clientOrderId
            );

            if (result.Success)
            {
                var order = result.Data;
                var executedQty = order.QuantityFilled;

                if (executedQty == 0)
                {
                    _logger?.LogError("Buy Exception: Filled Qty is 0, Order ID: {OrderId}", order.Id);
                    return new TradeExecutionResult
                    {
                        Success = false,
                        ErrorMessage = "Order filled quantity is 0"
                    };
                }

                var avgPrice = order.QuoteQuantityFilled / executedQty;

                if (order.Status != OrderStatus.Filled)
                {
                    _logger?.LogWarning("Buy Order Partially Filled: {Status}, Filled: {Qty}, Order ID: {OrderId}",
                        order.Status, executedQty, order.Id);
                }

                _logger?.LogInformation("Buy Successful - Order ID: {OrderId}, Qty: {Qty}, AvgPrice: ${Price:N2}",
                    order.Id, executedQty, avgPrice);

                if (_notification != null)
                {
                    await _notification.SendOrderExecutedAsync(
                        symbol, "BUY", executedQty, avgPrice, order.Id.ToString());
                }

                return new TradeExecutionResult
                {
                    Success = true,
                    OrderId = order.Id.ToString(),
                    Symbol = symbol,
                    Side = "BUY",
                    ExecutedQuantity = executedQty,
                    ExecutedPrice = avgPrice,
                    Commission = executedQty * avgPrice * DefaultCommission,
                    OrderStatus = order.Status.ToString()
                };
            }
            else
            {
                _logger?.LogError("Buy Failed: {Error}", result.Error?.Message);
                if (_notification != null)
                {
                    await _notification.SendErrorAsync("Order Failed", result.Error?.Message ?? "Unknown Error");
                }

                return new TradeExecutionResult
                {
                    Success = false,
                    ErrorMessage = result.Error?.Message
                };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Buy Exception");

            _logger?.LogWarning("⚠️ Attempting to query order status, ClientOrderId: {ClientOrderId}", clientOrderId);

            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    var delayMs = (int)Math.Pow(2, retry) * 1000;
                    _logger?.LogInformation("Waiting {Delay}ms to query order status (Attempt {Retry})", delayMs, retry + 1);
                    await Task.Delay(delayMs);

                    var queryResult = await _client.SpotApi.Trading.GetOrderAsync(
                        symbol,
                        origClientOrderId: clientOrderId
                    );

                    if (queryResult.Success && queryResult.Data != null)
                    {
                        var order = queryResult.Data;
                        if (order.Status == OrderStatus.Filled ||
                            order.Status == OrderStatus.PartiallyFilled)
                        {
                            _logger?.LogWarning("🔥 Ghost Order Detected: Order actually filled! ID: {OrderId}, Qty: {Qty}",
                                order.Id, order.QuantityFilled);

                            var executedQty = order.QuantityFilled;
                            var avgPrice = executedQty > 0 ? order.QuoteQuantityFilled / executedQty : 0;

                            if (_notification != null)
                            {
                                await _notification.SendRiskAlertAsync("Ghost Order Warning",
                                    $"Order found filled after network exception\n• Order ID: {order.Id}\n• Qty: {executedQty}\n• AvgPrice: ${avgPrice:N4}");
                            }

                            return new TradeExecutionResult
                            {
                                Success = true,
                                OrderId = order.Id.ToString(),
                                Symbol = symbol,
                                Side = "BUY",
                                ExecutedQuantity = executedQty,
                                ExecutedPrice = avgPrice,
                                Commission = executedQty * avgPrice * DefaultCommission,
                                OrderStatus = order.Status.ToString()
                            };
                        }
                        else if (order.Status == OrderStatus.New ||
                                 order.Status == OrderStatus.PendingCancel)
                        {
                            _logger?.LogInformation("Order Status: {Status}, Waiting...", order.Status);
                            continue;
                        }
                        else
                        {
                            _logger?.LogInformation("Order Status Query Result: {Status}, Not Filled", order.Status);
                            break;
                        }
                    }
                    else if (queryResult.Error?.Code == -2013)
                    {
                        _logger?.LogInformation("Order does not exist, confirmed not filled");
                        break;
                    }
                    else
                    {
                        _logger?.LogWarning("Query Failed: {Error}, Retrying...", queryResult.Error?.Message);
                    }
                }
                catch (Exception queryEx)
                {
                    _logger?.LogError(queryEx, "Failed to query order status (Attempt {Retry})", retry + 1);
                }
            }

            if (_notification != null)
            {
                await _notification.SendErrorAsync("Order Exception", ex.Message);
            }

            return new TradeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<TradeExecutionResult> MarketSellAsync(string symbol, decimal quantity, decimal? referencePrice = null)
    {
        _logger?.LogInformation("Execute Market Sell: {Symbol}, Qty: {Quantity}", symbol, quantity);

        if (string.IsNullOrEmpty(symbol))
        {
            return new TradeExecutionResult { Success = false, ErrorMessage = "Symbol cannot be empty" };
        }
        if (quantity <= 0)
        {
            return new TradeExecutionResult { Success = false, ErrorMessage = $"Invalid sell quantity: {quantity}" };
        }

        if (_isTestMode)
        {
            return await SimulateSellAsync(symbol, quantity, referencePrice);
        }

        var clientOrderId = GenerateClientOrderId();

        try
        {
            var adjustedQuantity = await AdjustQuantityPrecisionAsync(symbol, quantity);

            if (adjustedQuantity <= 0)
            {
                return new TradeExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"Adjusted quantity is 0 or negative: {adjustedQuantity}"
                };
            }

            var baseAsset = symbol.Replace("USDT", "");
            var balance = await GetBalanceAsync(baseAsset);
            if (balance < adjustedQuantity)
            {
                _logger?.LogError("{Asset} Insufficient Balance: Available={Balance}, Required Sell={Quantity}",
                    baseAsset, balance, adjustedQuantity);
                return new TradeExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"{baseAsset} Insufficient Balance: Available {balance}, Required {adjustedQuantity}"
                };
            }

            _logger?.LogInformation("Adjusted Sell Quantity: {Adjusted} (Original: {Original})", adjustedQuantity, quantity);
            _logger?.LogInformation("Sell Order ClientOrderId: {ClientOrderId}", clientOrderId);

            var result = await _client.SpotApi.Trading.PlaceOrderAsync(
                symbol,
                OrderSide.Sell,
                SpotOrderType.Market,
                quantity: adjustedQuantity,
                newClientOrderId: clientOrderId
            );

            if (result.Success)
            {
                var order = result.Data;
                var executedQty = order.QuantityFilled;

                if (executedQty == 0)
                {
                    _logger?.LogError("Sell Exception: Filled Qty is 0, Order ID: {OrderId}", order.Id);
                    return new TradeExecutionResult
                    {
                        Success = false,
                        ErrorMessage = "Order filled quantity is 0"
                    };
                }

                var avgPrice = order.QuoteQuantityFilled / executedQty;

                if (order.Status != OrderStatus.Filled)
                {
                    _logger?.LogWarning("Sell Order Partially Filled: {Status}, Filled: {Qty}, Order ID: {OrderId}",
                        order.Status, executedQty, order.Id);
                }

                _logger?.LogInformation("Sell Successful - Order ID: {OrderId}, Qty: {Qty}, AvgPrice: ${Price:N2}",
                    order.Id, executedQty, avgPrice);

                if (_notification != null)
                {
                    await _notification.SendOrderExecutedAsync(
                        symbol, "SELL", executedQty, avgPrice, order.Id.ToString());
                }

                return new TradeExecutionResult
                {
                    Success = true,
                    OrderId = order.Id.ToString(),
                    Symbol = symbol,
                    Side = "SELL",
                    ExecutedQuantity = executedQty,
                    ExecutedPrice = avgPrice,
                    Commission = executedQty * avgPrice * DefaultCommission,
                    OrderStatus = order.Status.ToString()
                };
            }
            else
            {
                _logger?.LogError("Sell Failed: {Error}", result.Error?.Message);
                if (_notification != null)
                {
                    await _notification.SendErrorAsync("Order Failed", result.Error?.Message ?? "Unknown Error");
                }

                return new TradeExecutionResult
                {
                    Success = false,
                    ErrorMessage = result.Error?.Message
                };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Sell Exception");

            _logger?.LogWarning("⚠️ Attempting to query sell order status, ClientOrderId: {ClientOrderId}", clientOrderId);

            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    var delayMs = (int)Math.Pow(2, retry) * 1000;
                    _logger?.LogInformation("Waiting {Delay}ms to query sell order status (Attempt {Retry})", delayMs, retry + 1);
                    await Task.Delay(delayMs);

                    var queryResult = await _client.SpotApi.Trading.GetOrderAsync(
                        symbol,
                        origClientOrderId: clientOrderId
                    );

                    if (queryResult.Success && queryResult.Data != null)
                    {
                        var order = queryResult.Data;
                        if (order.Status == OrderStatus.Filled ||
                            order.Status == OrderStatus.PartiallyFilled)
                        {
                            _logger?.LogWarning("🔥 Ghost Order Detected: Sell order actually filled! ID: {OrderId}, Qty: {Qty}",
                                order.Id, order.QuantityFilled);

                            var executedQty = order.QuantityFilled;
                            var avgPrice = executedQty > 0 ? order.QuoteQuantityFilled / executedQty : 0;

                            if (_notification != null)
                            {
                                await _notification.SendRiskAlertAsync("Ghost Order Warning",
                                    $"Sell order filled after network exception\n• Order ID: {order.Id}\n• Qty: {executedQty}\n• AvgPrice: ${avgPrice:N4}");
                            }

                            return new TradeExecutionResult
                            {
                                Success = true,
                                OrderId = order.Id.ToString(),
                                Symbol = symbol,
                                Side = "SELL",
                                ExecutedQuantity = executedQty,
                                ExecutedPrice = avgPrice,
                                Commission = executedQty * avgPrice * DefaultCommission,
                                OrderStatus = order.Status.ToString()
                            };
                        }
                        else if (order.Status == OrderStatus.New ||
                                 order.Status == OrderStatus.PendingCancel)
                        {
                            _logger?.LogInformation("Sell Order Status: {Status}, Waiting...", order.Status);
                            continue;
                        }
                        else
                        {
                            _logger?.LogInformation("Sell Order Status: {Status}, Not Filled", order.Status);
                            break;
                        }
                    }
                    else if (queryResult.Error?.Code == -2013)
                    {
                        _logger?.LogInformation("Sell order does not exist, confirmed not filled");
                        break;
                    }
                    else
                    {
                        _logger?.LogWarning("Query Failed: {Error}, Retrying...", queryResult.Error?.Message);
                    }
                }
                catch (Exception queryEx)
                {
                    _logger?.LogError(queryEx, "Failed to query sell order status (Attempt {Retry})", retry + 1);
                }
            }

            if (_notification != null)
            {
                await _notification.SendErrorAsync("Order Exception", ex.Message);
            }

            return new TradeExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private decimal FormatQuoteAmount(decimal amount)
    {
        var truncated = Math.Floor(amount * 100m) / 100m;
        return Math.Max(truncated, 10m);
    }

    private async Task<decimal> AdjustQuantityPrecisionAsync(string symbol, decimal quantity)
    {
        try
        {
            if (!_symbolPrecisionCache.TryGetValue(symbol, out var precision))
            {
                var precisionResult = await GetSymbolPrecisionAsync(symbol);
                if (precisionResult.HasValue)
                {
                    precision = precisionResult.Value;
                    _symbolPrecisionCache.TryAdd(symbol, precision);
                }
                else
                {
                    precision = (8, 8);
                }
            }

            var multiplier = (decimal)Math.Pow(10, precision.quantityPrecision);
            return Math.Floor(quantity * multiplier) / multiplier;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get precision, using raw quantity");
            return quantity;
        }
    }

    private async Task<TradeExecutionResult> SimulateBuyAsync(string symbol, decimal quoteAmount, decimal? referencePrice = null)
    {
        var price = referencePrice ?? await GetPriceAsync(symbol);
        if (price == 0)
        {
            return new TradeExecutionResult
            {
                Success = false,
                ErrorMessage = "Cannot get price"
            };
        }

        var quantity = quoteAmount / price;
        var commission = quoteAmount * DefaultCommission;

        _logger?.LogInformation("[SIM] Buy Success - Qty: {Qty:N6}, Price: ${Price:N2}", quantity, price);

        return new TradeExecutionResult
        {
            Success = true,
            OrderId = $"SIM_{DateTime.UtcNow.Ticks}",
            Symbol = symbol,
            Side = "BUY",
            ExecutedQuantity = quantity,
            ExecutedPrice = price,
            Commission = commission,
            IsSimulated = true
        };
    }

    private async Task<TradeExecutionResult> SimulateSellAsync(string symbol, decimal quantity, decimal? referencePrice = null)
    {
        var price = referencePrice ?? await GetPriceAsync(symbol);
        if (price == 0)
        {
            return new TradeExecutionResult
            {
                Success = false,
                ErrorMessage = "Cannot get price"
            };
        }

        var amount = quantity * price;
        var commission = amount * DefaultCommission;

        _logger?.LogInformation("[SIM] Sell Success - Qty: {Qty:N6}, Price: ${Price:N2}", quantity, price);

        return new TradeExecutionResult
        {
            Success = true,
            OrderId = $"SIM_{DateTime.UtcNow.Ticks}",
            Symbol = symbol,
            Side = "SELL",
            ExecutedQuantity = quantity,
            ExecutedPrice = price,
            Commission = commission,
            IsSimulated = true
        };
    }

    public async Task<(int pricePrecision, int quantityPrecision)?> GetSymbolPrecisionAsync(string symbol)
    {
        try
        {
            var result = await _client.SpotApi.ExchangeData.GetExchangeInfoAsync(symbol);
            if (result.Success)
            {
                var symbolInfo = result.Data.Symbols.FirstOrDefault();
                if (symbolInfo != null)
                {
                    int quantityPrecision = symbolInfo.BaseAssetPrecision;
                    int pricePrecision = 8;

                    var lotSizeFilter = symbolInfo.LotSizeFilter;
                    var priceFilter = symbolInfo.PriceFilter;

                    if (lotSizeFilter != null && lotSizeFilter.StepSize > 0)
                    {
                        var stepStr = lotSizeFilter.StepSize.ToString("G29");
                        var decimalIndex = stepStr.IndexOf('.');
                        if (decimalIndex >= 0)
                        {
                            stepStr = stepStr.TrimEnd('0');
                            quantityPrecision = stepStr.Length - decimalIndex - 1;
                        }
                    }

                    if (priceFilter != null && priceFilter.TickSize > 0)
                    {
                        var tickStr = priceFilter.TickSize.ToString("G29");
                        var decimalIndex = tickStr.IndexOf('.');
                        if (decimalIndex >= 0)
                        {
                            tickStr = tickStr.TrimEnd('0');
                            pricePrecision = tickStr.Length - decimalIndex - 1;
                        }
                    }

                    _logger?.LogDebug("Pair {Symbol} Precision: Price={PricePrecision}, Qty={QuantityPrecision}",
                        symbol, pricePrecision, quantityPrecision);
                    return (pricePrecision, quantityPrecision);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Get precision info exception");
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _client?.Dispose();
            _logger?.LogDebug("BinanceTradeService resources disposed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Dispose BinanceTradeService exception");
        }
    }

    #region Helper Methods

    /// <summary>
    /// Get all asset balances and position status
    /// </summary>
    public async Task<AccountStatusResult> GetAccountStatusAsync()
    {
        var result = new AccountStatusResult();
        
        try
        {
            var accountInfo = await _client.SpotApi.Account.GetAccountInfoAsync();
            if (!accountInfo.Success)
            {
                result.Success = false;
                result.ErrorMessage = accountInfo.Error?.Message ?? "Failed to get account info";
                return result;
            }

            result.Success = true;
            
            // Get all assets with balance
            foreach (var balance in accountInfo.Data.Balances)
            {
                var total = balance.Available + balance.Locked;
                if (total > 0.00000001m)
                {
                    result.Balances.Add(new AssetBalance
                    {
                        Asset = balance.Asset,
                        Available = balance.Available,
                        Locked = balance.Locked,
                        Total = total
                    });
                }
            }

            // Record USDT separately
            var usdtBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == "USDT");
            result.UsdtAvailable = usdtBalance?.Available ?? 0m;
            result.UsdtLocked = usdtBalance?.Locked ?? 0m;
            result.UsdtTotal = result.UsdtAvailable + result.UsdtLocked;

            // Get current value of held assets
            var nonUsdtAssets = result.Balances
                .Where(b => b.Asset != "USDT" && b.Asset != "BUSD" && b.Asset != "USDC")
                .ToList();

            // Fetch all asset prices in parallel
            var priceTasks = nonUsdtAssets.Select(async asset =>
            {
                try
                {
                    var symbol = asset.Asset + "USDT";
                    var price = await GetPriceAsync(symbol);
                    return (asset, symbol, price);
                }
                catch
                {
                    return (asset, asset.Asset + "USDT", 0m);
                }
            }).ToList();

            var priceResults = await Task.WhenAll(priceTasks);

            foreach (var (asset, symbol, price) in priceResults)
            {
                if (price > 0)
                {
                    asset.UsdtValue = asset.Total * price;
                    asset.CurrentPrice = price;
                    result.TotalPositionValue += asset.UsdtValue;
                    
                    result.Positions.Add(new PositionInfo
                    {
                        Symbol = symbol,
                        Asset = asset.Asset,
                        Quantity = asset.Total,
                        AvailableQuantity = asset.Available,
                        CurrentPrice = price,
                        UsdtValue = asset.UsdtValue
                    });
                }
            }

            result.TotalAccountValue = result.UsdtTotal + result.TotalPositionValue;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _logger?.LogError(ex, "Exception getting account status");
        }

        return result;
    }

    /// <summary>
    /// Get Symbol Filter Info (MinNotional, StepSize, TickSize)
    /// </summary>
    public async Task<SymbolFilterInfo?> GetSymbolFilterInfoAsync(string symbol)
    {
        // Check cache
        if (_symbolFilterCache.TryGetValue(symbol, out var cached) && !cached.IsExpired)
        {
            return cached;
        }

        try
        {
            var result = await _client.SpotApi.ExchangeData.GetExchangeInfoAsync(symbol);
            if (!result.Success)
            {
                _logger?.LogWarning("Failed to get symbol info for {Symbol}: {Error}", symbol, result.Error?.Message);
                return null;
            }

            var symbolInfo = result.Data.Symbols.FirstOrDefault();
            if (symbolInfo == null)
            {
                _logger?.LogWarning("Symbol {Symbol} does not exist", symbol);
                return null;
            }

            var filterInfo = new SymbolFilterInfo
            {
                Symbol = symbol,
                CachedAt = DateTime.UtcNow
            };

            // Parse LOT_SIZE filter
            if (lotSizeFilter != null)
            {
                filterInfo.StepSize = lotSizeFilter.StepSize;
                filterInfo.MinQuantity = lotSizeFilter.MinQuantity;
                filterInfo.MaxQuantity = lotSizeFilter.MaxQuantity;
            }

            // Parse PRICE_FILTER filter
            var priceFilter = symbolInfo.PriceFilter;
            if (priceFilter != null)
            {
                filterInfo.TickSize = priceFilter.TickSize;
            }

            // Parse MIN_NOTIONAL / NOTIONAL filter
            var minNotionalFilter = symbolInfo.MinNotionalFilter;
            if (minNotionalFilter != null)
            {
                filterInfo.MinNotional = minNotionalFilter.MinNotional;
            }
            else
            {
                var notionalFilter = symbolInfo.NotionalFilter;
                if (notionalFilter != null)
                {
                    filterInfo.MinNotional = notionalFilter.MinNotional;
                }
            }

            // Update cache
            _symbolFilterCache[symbol] = filterInfo;

            _logger?.LogInformation("[Filter] {Symbol}: MinNotional={MinNotional}, StepSize={StepSize}, TickSize={TickSize}",
                symbol, filterInfo.MinNotional, filterInfo.StepSize, filterInfo.TickSize);
            return filterInfo;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Exception getting symbol filter info");
            return null;
        }
    }

    /// <summary>
    /// Normalize Quantity (Floor)
    /// </summary>
    public decimal NormalizeQuantity(decimal quantity, decimal stepSize)
    {
        if (stepSize <= 0) return quantity;
        return Math.Floor(quantity / stepSize) * stepSize;
    }

    /// <summary>
    /// Normalize Price (Truncate)
    /// </summary>
    public decimal NormalizePrice(decimal price, decimal tickSize)
    {
        if (tickSize <= 0) return price;
        return Math.Truncate(price / tickSize) * tickSize;
    }

    /// <summary>
    /// Get Order Status
    /// </summary>
    public async Task<OrderStatusResult?> GetOrderStatusAsync(string symbol, string orderId)
    {
        try
        {
            if (!long.TryParse(orderId, out var orderIdLong))
            {
                _logger?.LogWarning("Invalid Order ID format: {OrderId}", orderId);
                return null;
            }
            
            var result = await _client.SpotApi.Trading.GetOrderAsync(symbol, orderIdLong);
            if (result.Success)
            {
                var order = result.Data;
                return new OrderStatusResult
                {
                    OrderId = order.Id.ToString(),
                    Symbol = order.Symbol,
                    Status = order.Status.ToString(),
                    Side = order.Side.ToString(),
                    OriginalQuantity = order.Quantity,
                    ExecutedQuantity = order.QuantityFilled,
                    Price = order.Price,
                    AvgPrice = order.QuantityFilled > 0 ? order.QuoteQuantityFilled / order.QuantityFilled : 0,
                    IsFilled = order.Status == OrderStatus.Filled,
                    IsPartiallyFilled = order.Status == OrderStatus.PartiallyFilled
                };
            }
            else
            {
                _logger?.LogError("Failed to query order: {Error}", result.Error?.Message);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception querying order");
            return null;
        }
    }

    /// <summary>
    /// Cancel Order
    /// </summary>
    public async Task<bool> CancelOrderAsync(string symbol, string orderId)
    {
        try
        {
            if (_isTestMode)
            {
                _logger?.LogInformation("[SIM] Cancel Order: {Symbol} {OrderId}", symbol, orderId);
                return true;
            }

            if (!long.TryParse(orderId, out var orderIdLong))
            {
                _logger?.LogWarning("Invalid Order ID format: {OrderId}", orderId);
                return false;
            }

            var result = await _client.SpotApi.Trading.CancelOrderAsync(symbol, orderIdLong);
            if (result.Success)
            {
                _logger?.LogInformation("Order canceled: {Symbol} {OrderId}", symbol, orderId);
                return true;
            }

            _logger?.LogError("Failed to cancel order: {Error}", result.Error?.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception canceling order");
            return false;
        }
    }

    /// <summary>
    /// Get Asset Balance
    /// </summary>
    public async Task<decimal> GetAssetBalanceAsync(string symbol)
    {
        try
        {
            var baseAsset = symbol.Replace("USDT", "").Replace("BUSD", "").Replace("USDC", "");
            
            var result = await _client.SpotApi.Account.GetAccountInfoAsync();
            if (result.Success)
            {
                var balance = result.Data.Balances.FirstOrDefault(b => b.Asset == baseAsset);
                return balance?.Available ?? 0m;
            }
            else
            {
                _logger?.LogError("Failed to get asset balance: {Error}", result.Error?.Message);
                return 0m;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception getting asset balance");
            return 0m;
        }
    }

    /// <summary>
    /// Verify Position Sync Status
    /// </summary>
    public async Task<PositionSyncResult> VerifyPositionAsync(string symbol, bool localIsInPosition, decimal localQuantity)
    {
        try
        {
            var actualBalance = await GetAssetBalanceAsync(symbol);
            var hasActualPosition = actualBalance > 0.00000001m;
            
            var result = new PositionSyncResult
            {
                LocalIsInPosition = localIsInPosition,
                LocalQuantity = localQuantity,
                ActualBalance = actualBalance,
                ActualHasPosition = hasActualPosition,
                IsSynced = localIsInPosition == hasActualPosition
            };
            
            if (!result.IsSynced)
            {
                if (localIsInPosition && !hasActualPosition)
                {
                    result.SyncIssue = "Local has position, but actual account has none";
                }
                else if (!localIsInPosition && hasActualPosition)
                {
                    result.SyncIssue = $"Local has no position, but actual account has {actualBalance} holding";
                }
                
                _logger?.LogWarning("Position sync issue: {SyncIssue}", result.SyncIssue);
            }
            else if (localIsInPosition && Math.Abs(localQuantity - actualBalance) / localQuantity > 0.01m)
            {
                result.IsSynced = false;
                result.SyncIssue = $"Quantity mismatch: Local={localQuantity}, Actual={actualBalance}";
                _logger?.LogWarning("{SyncIssue}", result.SyncIssue);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception verifying position");
            return new PositionSyncResult
            {
                LocalIsInPosition = localIsInPosition,
                LocalQuantity = localQuantity,
                IsSynced = false,
                SyncIssue = $"Verification exception: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get Average Buy Price
    /// </summary>
    public async Task<decimal> GetAverageEntryPriceAsync(string symbol)
    {
        try
        {
            var result = await _client.SpotApi.Trading.GetOrdersAsync(symbol, limit: 50);
            
            if (result.Success && result.Data != null)
            {
                var lastBuyOrder = result.Data
                    .Where(o => o.Side == OrderSide.Buy && 
                               (o.Status == OrderStatus.Filled || 
                                o.Status == OrderStatus.PartiallyFilled))
                    .OrderByDescending(o => o.CreateTime)
                    .FirstOrDefault();
                
                if (lastBuyOrder != null && lastBuyOrder.QuantityFilled > 0)
                {
                    var avgPrice = lastBuyOrder.QuoteQuantityFilled / lastBuyOrder.QuantityFilled;
                    _logger?.LogInformation("Found recent buy order: {AvgPrice}", avgPrice);
                    return avgPrice;
                }
            }
            
            return 0m;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception getting buy price");
            return 0m;
        }
    }

    /// <summary>
    /// Get Current Price
    /// </summary>
    public async Task<decimal> GetCurrentPriceAsync(string symbol)
    {
        try
        {
            var result = await _client.SpotApi.ExchangeData.GetPriceAsync(symbol);
            
            if (result.Success && result.Data != null)
            {
                return result.Data.Price;
            }
            
            return 0m;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception getting current price");
            return 0m;
        }
    }

    /// <summary>
    /// Validate Trade Parameters
    /// </summary>
    public async Task<TradeValidationResult> ValidateTradeAsync(string symbol, string side, decimal amount)
    {
        var result = new TradeValidationResult { IsValid = true };
        var errors = new List<string>();
        var warnings = new List<string>();

        try
        {
            _logger?.LogInformation("=== Trade Validation Start ===");
            _logger?.LogInformation("Symbol: {Symbol}, Side: {Side}, Amount: {Amount}", symbol, side, amount);

            // 1. Validate Symbol
            var exchangeInfo = await _client.SpotApi.ExchangeData.GetExchangeInfoAsync(symbol);
            if (!exchangeInfo.Success)
            {
                errors.Add($"Failed to get symbol info: {exchangeInfo.Error?.Message}");
                result.IsValid = false;
            }
            else
            {
                var symbolInfo = exchangeInfo.Data.Symbols.FirstOrDefault();
                if (symbolInfo == null)
                {
                    errors.Add($"Symbol {symbol} does not exist");
                    result.IsValid = false;
                }
                else if (symbolInfo.Status != SymbolStatus.Trading)
                {
                    errors.Add($"Symbol {symbol} not trading, status: {symbolInfo.Status}");
                    result.IsValid = false;
                }
                else
                {
                    result.QuantityPrecision = symbolInfo.BaseAssetPrecision;
                    _logger?.LogInformation("Symbol Precision: {Precision}", result.QuantityPrecision);
                }
            }

            // 2. Validate Balance
            var accountResult = await _client.SpotApi.Account.GetAccountInfoAsync();
            if (!accountResult.Success)
            {
                errors.Add($"Failed to get account info: {accountResult.Error?.Message}");
                result.IsValid = false;
            }
            else
            {
                if (side.ToUpper() == "BUY")
                {
                    var usdtBalance = accountResult.Data.Balances.FirstOrDefault(b => b.Asset == "USDT");
                    result.AvailableBalance = usdtBalance?.Available ?? 0m;
                    
                    if (result.AvailableBalance < amount)
                    {
                        errors.Add($"Insufficient USDT balance: Available {result.AvailableBalance}, Required {amount}");
                        result.IsValid = false;
                    }
                    else
                    {
                        _logger?.LogInformation("USDT Balance sufficient: {Balance}", result.AvailableBalance);
                    }
                }
                else if (side.ToUpper() == "SELL")
                {
                    var baseAsset = symbol.Replace("USDT", "");
                    var assetBalance = accountResult.Data.Balances.FirstOrDefault(b => b.Asset == baseAsset);
                    result.AvailableBalance = assetBalance?.Available ?? 0m;
                    
                    if (result.AvailableBalance < amount)
                    {
                        errors.Add($"{baseAsset} Insufficient Balance: Available {result.AvailableBalance}, Required {amount}");
                        result.IsValid = false;
                    }
                    else
                    {
                        _logger?.LogInformation("{Asset} Balance sufficient: {Balance}", baseAsset, result.AvailableBalance);
                    }
                }
            }

            // 3. Validate Min Trade Amount
            if (side.ToUpper() == "BUY")
            {
                var formattedAmount = FormatQuoteAmount(amount);
                if (formattedAmount < 10m)
                {
                    errors.Add($"Buy amount too small: {formattedAmount}, Min 10 USDT");
                    result.IsValid = false;
                }
                result.FormattedAmount = formattedAmount;
                _logger?.LogInformation("Formatted Amount: {Formatted}", formattedAmount);
            }

            // 4. Get Current Price Reference
            var priceResult = await _client.SpotApi.ExchangeData.GetPriceAsync(symbol);
            if (priceResult.Success)
            {
                result.CurrentPrice = priceResult.Data.Price;
                _logger?.LogInformation("Current Price: {Price}", result.CurrentPrice);
            }

            result.Errors = errors;
            result.Warnings = warnings;
            
            _logger?.LogInformation("=== Validation Result: {Result} ===", result.IsValid ? "Passed" : "Failed");
            foreach (var error in errors)
            {
                _logger?.LogError("Validation Error: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Validation Exception: {ex.Message}");
            result.IsValid = false;
            result.Errors = errors;
            _logger?.LogError(ex, "Trade Validation Exception");
        }

        return result;
    }

    /// <summary>
    /// Run Full System Test
    /// </summary>
    public async Task<SystemTestResult> RunSystemTestAsync(string symbol = "ETHUSDT")
    {
        var result = new SystemTestResult();
        _logger?.LogInformation("========== System Test Start ==========");

        try
        {
            // Test 1: API Connection
            result.ApiConnectionTest = await TestConnectionAsync();
            _logger?.LogInformation("[1/6] API Connection Test: {Result}", result.ApiConnectionTest ? "✓ Passed" : "✗ Failed");

            // Test 2: Get Account Info
            var accountInfo = await _client.SpotApi.Account.GetAccountInfoAsync();
            result.AccountInfoTest = accountInfo.Success;
            _logger?.LogInformation("[2/6] Account Info Test: {Result}", result.AccountInfoTest ? "✓ Passed" : "✗ Failed");

            // Test 3: Get Symbol Info
            var exchangeInfo = await _client.SpotApi.ExchangeData.GetExchangeInfoAsync(symbol);
            result.ExchangeInfoTest = exchangeInfo.Success;
            _logger?.LogInformation("[3/6] Symbol Info Test: {Result}", result.ExchangeInfoTest ? "✓ Passed" : "✗ Failed");

            // Test 4: Get Real-time Price
            var price = await GetPriceAsync(symbol);
            result.PriceQueryTest = price > 0;
            result.CurrentPrice = price;
            _logger?.LogInformation("[4/6] Price Query Test: {Result}", result.PriceQueryTest ? $"✓ Passed (${price:N2})" : "✗ Failed");

            // Test 5: Balance Query
            var usdtBalance = await GetBalanceAsync("USDT");
            var ethBalance = await GetBalanceAsync("ETH");
            result.BalanceQueryTest = true;
            result.UsdtBalance = usdtBalance;
            result.EthBalance = ethBalance;
            _logger?.LogInformation("[5/6] Balance Query Test: ✓ USDT={Usdt:N2}, ETH={Eth:N6}", usdtBalance, ethBalance);

            // Test 6: Trade Validation
            var buyValidation = await ValidateTradeAsync(symbol, "BUY", 100m);
            result.TradeValidationTest = buyValidation.IsValid;
            result.ValidationErrors = buyValidation.Errors;
            _logger?.LogInformation("[6/6] Trade Validation Test: {Result}", result.TradeValidationTest ? "✓ Passed" : "✗ Failed");

            result.AllTestsPassed = result.ApiConnectionTest && 
                                     result.AccountInfoTest && 
                                     result.ExchangeInfoTest && 
                                     result.PriceQueryTest && 
                                     result.BalanceQueryTest &&
                                     result.TradeValidationTest;

            _logger?.LogInformation("========== Test Complete: {Result} ==========", result.AllTestsPassed ? "All Passed" : "Failed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "System Test Exception");
            result.AllTestsPassed = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    #region Manual Order Methods

    /// <summary>
    /// Market Buy (Simplified)
    /// </summary>
    public async Task<bool> PlaceBuyOrderAsync(string symbol, decimal quantity)
    {
        var result = await MarketBuyAsync(symbol, quantity);
        return result.Success;
    }

    /// <summary>
    /// Market Sell (Simplified)
    /// </summary>
    public async Task<bool> PlaceSellOrderAsync(string symbol, decimal quantity)
    {
        var result = await MarketSellAsync(symbol, quantity);
        return result.Success;
    }

    /// <summary>
    /// Limit Buy
    /// </summary>
    public async Task<bool> PlaceLimitBuyOrderAsync(string symbol, decimal quantity, decimal price)
    {
        _logger?.LogInformation("Executing Limit Buy: {Symbol}, Qty: {Quantity}, Price: {Price}", symbol, quantity, price);
        
        if (_isTestMode && !_useTestnet)
        {
            _logger?.LogInformation("[SIM] Limit Buy: {Quantity} @ {Price}", quantity, price);
            return true;
        }
        
        try
        {
            var adjustedQuantity = await AdjustQuantityPrecisionAsync(symbol, quantity);
            var adjustedPrice = await AdjustPricePrecisionAsync(symbol, price);
            
            if (adjustedQuantity <= 0)
            {
                _logger?.LogError("Limit Buy Failed: Quantity 0 after adjustment");
                return false;
            }
            
            var result = await _client.SpotApi.Trading.PlaceOrderAsync(
                symbol,
                OrderSide.Buy,
                SpotOrderType.Limit,
                quantity: adjustedQuantity,
                price: adjustedPrice,
                timeInForce: TimeInForce.GoodTillCanceled
            );
            
            if (result.Success)
            {
                _logger?.LogInformation("Limit Buy Order Submitted - ID: {OrderId}", result.Data.Id);
                return true;
            }
            else
            {
                _logger?.LogError("Limit Buy Failed: {Error}", result.Error?.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Limit Buy Exception");
            return false;
        }
    }

    /// <summary>
    /// Limit Sell
    /// </summary>
    public async Task<bool> PlaceLimitSellOrderAsync(string symbol, decimal quantity, decimal price)
    {
        _logger?.LogInformation("Executing Limit Sell: {Symbol}, Qty: {Quantity}, Price: {Price}", symbol, quantity, price);
        
        if (_isTestMode && !_useTestnet)
        {
            _logger?.LogInformation("[SIM] Limit Sell: {Quantity} @ {Price}", quantity, price);
            return true;
        }
        
        try
        {
            var adjustedQuantity = await AdjustQuantityPrecisionAsync(symbol, quantity);
            var adjustedPrice = await AdjustPricePrecisionAsync(symbol, price);
            
            if (adjustedQuantity <= 0)
            {
                _logger?.LogError("Limit Sell Failed: Quantity 0 after adjustment");
                return false;
            }
            
            var result = await _client.SpotApi.Trading.PlaceOrderAsync(
                symbol,
                OrderSide.Sell,
                SpotOrderType.Limit,
                quantity: adjustedQuantity,
                price: adjustedPrice,
                timeInForce: TimeInForce.GoodTillCanceled
            );
            
            if (result.Success)
            {
                _logger?.LogInformation("Limit Sell Order Submitted - ID: {OrderId}", result.Data.Id);
                return true;
            }
            else
            {
                _logger?.LogError("Limit Sell Failed: {Error}", result.Error?.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Limit Sell Exception");
            return false;
        }
    }

    /// <summary>
    /// Adjust Price Precision
    /// </summary>
    private async Task<decimal> AdjustPricePrecisionAsync(string symbol, decimal price)
    {
        try
        {
            if (!_symbolPrecisionCache.TryGetValue(symbol, out var precision))
            {
                var precisionResult = await GetSymbolPrecisionAsync(symbol);
                if (precisionResult.HasValue)
                {
                    precision = precisionResult.Value;
                    _symbolPrecisionCache.TryAdd(symbol, precision);
                }
                else
                {
                    precision = (2, 8);
                }
            }
            
            var multiplier = (decimal)Math.Pow(10, precision.pricePrecision);
            return Math.Truncate(price * multiplier) / multiplier;
        }
        catch
        {
            return Math.Truncate(price * 100m) / 100m;
        }
    }

    /// <summary>
    /// Get Recent Orders
    /// </summary>
    public async Task<List<Binance.Net.Objects.Models.Spot.BinanceOrder>> GetRecentOrdersAsync(string symbol, int limit = 10)
    {
        try
        {
            var result = await _client.SpotApi.Trading.GetOrdersAsync(symbol, limit: limit);
            
            if (result.Success)
            {
                return result.Data.OrderByDescending(o => o.CreateTime).Take(limit).ToList();
            }
            else
            {
                _logger?.LogError("Failed to get order history: {Error}", result.Error?.Message);
                return new List<Binance.Net.Objects.Models.Spot.BinanceOrder>();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception getting order history");
            return new List<Binance.Net.Objects.Models.Spot.BinanceOrder>();
        }
    }

    #endregion

    #endregion
}

/// <summary>
/// Trade Execution Result
/// </summary>
public class TradeExecutionResult
{
    public bool Success { get; set; }
    public string? OrderId { get; set; }
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal ExecutedQuantity { get; set; }
    public decimal ExecutedPrice { get; set; }
    public decimal Commission { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsSimulated { get; set; }
    public string? OrderStatus { get; set; }

    public decimal TotalValue => ExecutedQuantity * ExecutedPrice;
}

/// <summary>
/// Symbol Filter Info
/// </summary>
public class SymbolFilterInfo
{
    public string Symbol { get; set; } = "";
    public decimal MinNotional { get; set; } = 5m;
    public decimal StepSize { get; set; } = 0.00001m;
    public decimal TickSize { get; set; } = 0.01m;
    public decimal MinQuantity { get; set; } = 0.00001m;
    public decimal MaxQuantity { get; set; } = 9999999m;
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;
    public bool IsExpired => (DateTime.UtcNow - CachedAt).TotalHours > 1;
}

/// <summary>
/// Trade Validation Result
/// </summary>
public class TradeValidationResult
{
    public bool IsValid { get; set; }
    public decimal AvailableBalance { get; set; }
    public decimal FormattedAmount { get; set; }
    public decimal CurrentPrice { get; set; }
    public int QuantityPrecision { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// System Test Result
/// </summary>
public class SystemTestResult
{
    public bool AllTestsPassed { get; set; }
    public bool ApiConnectionTest { get; set; }
    public bool AccountInfoTest { get; set; }
    public bool ExchangeInfoTest { get; set; }
    public bool PriceQueryTest { get; set; }
    public bool BalanceQueryTest { get; set; }
    public bool TradeValidationTest { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal UsdtBalance { get; set; }
    public decimal EthBalance { get; set; }
    public List<string> ValidationErrors { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Order Status Result
/// </summary>
public class OrderStatusResult
{
    public string OrderId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Status { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal OriginalQuantity { get; set; }
    public decimal ExecutedQuantity { get; set; }
    public decimal Price { get; set; }
    public decimal AvgPrice { get; set; }
    public bool IsFilled { get; set; }
    public bool IsPartiallyFilled { get; set; }
}

/// <summary>
/// Position Sync Result
/// </summary>
public class PositionSyncResult
{
    public bool LocalIsInPosition { get; set; }
    public decimal LocalQuantity { get; set; }
    public decimal ActualBalance { get; set; }
    public bool ActualHasPosition { get; set; }
    public bool IsSynced { get; set; }
    public string? SyncIssue { get; set; }
}

/// <summary>
/// Account Status Result
/// </summary>
public class AccountStatusResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    
    // USDT Balance
    public decimal UsdtAvailable { get; set; }
    public decimal UsdtLocked { get; set; }
    public decimal UsdtTotal { get; set; }
    
    // Total Position Value
    public decimal TotalPositionValue { get; set; }
    
    // Total Account Value
    public decimal TotalAccountValue { get; set; }
    
    // All Asset Balances
    public List<AssetBalance> Balances { get; set; } = new();
    
    // Position Info
    public List<PositionInfo> Positions { get; set; } = new();
}

/// <summary>
/// Asset Balance Info
/// </summary>
public class AssetBalance
{
    public string Asset { get; set; } = "";
    public decimal Available { get; set; }
    public decimal Locked { get; set; }
    public decimal Total { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal UsdtValue { get; set; }
}

/// <summary>
/// Position Info
/// </summary>
public class PositionInfo
{
    public string Symbol { get; set; } = "";
    public string Asset { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal AvailableQuantity { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal UsdtValue { get; set; }
}

