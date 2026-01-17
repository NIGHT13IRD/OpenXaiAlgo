using Microsoft.Extensions.Logging;
using TradingSystem.Console.Configuration;
using TradingSystem.Console.Utils;
using TradingSystem.Console.Trading.Strategy;

namespace TradingSystem.Console.Trading;

/// <summary>
/// 🛡️ Trade Supervisor
/// Responsible for signal processing, stop loss monitoring, trailing stop calculation
/// Decides when to trigger buy/sell
/// </summary>
public class TradeSupervisor
{
    private readonly ILogger _logger;
    private readonly string _symbol;
    private readonly AssetConfig _config;
    private readonly PositionManager _position;
    private readonly AssetRiskManager _riskManager;
    private readonly TradeService _tradeService;
    private readonly IStrategy _strategy;
    
    public TradeSupervisor(
        ILogger logger,
        AssetConfig config,
        PositionManager position,
        AssetRiskManager riskManager,
        TradeService tradeService,
        IStrategy strategy)
    {
        _logger = logger;
        _symbol = config.Symbol;
        _config = config;
        _position = position;
        _riskManager = riskManager;
        _tradeService = tradeService;
        _strategy = strategy;
    }
    
    /// <summary>
    /// Handle Kline Update
    /// </summary>
    public void OnCandleUpdate(IReadOnlyList<Candle> candles, decimal currentPrice)
    {
        var signal = _strategy.ProcessCandles(candles);
        ProcessSignal(signal, currentPrice);
    }

    /// <summary>
    /// Process Trading Signal
    /// </summary>
    public void ProcessSignal(TradingSignal signal, decimal currentPrice)
    {
        _position.UpdateCurrentSignal(signal);
        _logger.LogDebug("[{Symbol}] Signal: {Signal}, InPosition: {InPosition}", 
            _symbol, signal, _position.IsInPosition);

        // Risk Check
        var riskCheck = _riskManager.CanTrade(_position.State);
        if (!riskCheck.IsAllowed)
        {
            if (!_position.State.RiskPaused)
            {
                _logger.LogWarning("[{Symbol}] ⛔ Trading paused: {Reason}", _symbol, riskCheck.Reason);
                _position.SetRiskPaused(true, riskCheck.Reason);
            }
            return;
        }

        // Buy Signal
        if (!_position.IsInPosition && signal == TradingSignal.StrongBull)
        {
            _tradeService.ExecuteBuyAsync(currentPrice).SafeFireAndForget(_logger, "ExecuteBuy");
        }
        // Sell Signal
        else if (_position.IsInPosition && signal == TradingSignal.StrongBear)
        {
            _tradeService.ExecuteSellAsync("Signal Exit").SafeFireAndForget(_logger, "ExecuteSell-SignalExit");
        }
    }
    
    /// <summary>
    /// Check Stop Loss (called on every price update)
    /// </summary>
    public void CheckStopLoss(decimal currentPrice)
    {
        if (!_position.IsInPosition) return;

        // Update Highest Price (for Trailing Stop)
        _position.UpdateHighestPrice(currentPrice);

        var stopLossPrice = CalculateStopLossPrice();
        _position.UpdateStopLossPrice(stopLossPrice);

        if (currentPrice <= stopLossPrice)
        {
            _logger.LogWarning("[{Symbol}] 🛑 Stop loss triggered @ {Price:F2} (SL: {SL:F2})", 
                _symbol, currentPrice, stopLossPrice);
            _tradeService.ExecuteSellAsync("Stop Loss").SafeFireAndForget(_logger, "ExecuteSell-StopLoss");
        }
    }
    
    /// <summary>
    /// Calculate Current Stop Loss Price (supports Fixed and Trailing)
    /// </summary>
    private decimal CalculateStopLossPrice()
    {
        if (_config.StopLoss.Type == "Trailing")
        {
            // Trailing Stop
            var activationPrice = _position.EntryPrice * (1 + _config.StopLoss.TrailingActivation);
            if (_position.HighestPriceSinceEntry >= activationPrice)
            {
                return _position.HighestPriceSinceEntry * (1 - _config.StopLoss.TrailingPercent);
            }
        }
        
        // Fixed Stop Loss
        return _position.EntryPrice * (1 - _config.StopLoss.FixedPercent);
    }
    
    /// <summary>
    /// Perform Daily Checks
    /// </summary>
    public async Task PerformDailyChecksAsync()
    {
        _position.CheckNewDay();
        
        await _riskManager.CheckAndSendDailyReportAsync(_position.State);
        await _riskManager.CheckCapitalCurveAnomalyAsync(_position.State);
    }
    
    /// <summary>
    /// Perform Periodic Reconciliation
    /// </summary>
    public async Task PerformPeriodicSyncAsync()
    {
        await _tradeService.VerifyAndSyncPositionAsync();
    }
}
