using Binance.Net.Clients;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Spot.Socket;
using Microsoft.Extensions.Logging;
using TradingSystem.Console.Api;
using TradingSystem.Console.Configuration;
using TradingSystem.Console.Services;
using TradingSystem.Console.Utils;

namespace TradingSystem.Console.Trading;

/// <summary>
/// Asset Trading Engine
/// Responsibility: Assemble and coordinate service modules, manage full lifecycle of single asset trading
/// - MarketDataService: Market Data
/// - PositionManager: Position Status
/// - TradeService: Trade Execution
/// - TradeSupervisor: Signal Monitoring
/// </summary>
public class AssetTradingEngine : IDisposable
{
    private readonly ILogger _logger;
    private readonly BinanceRestClient _restClient;
    
    // Service Modules
    private readonly MarketDataService _marketData;
    private readonly PositionManager _position;
    private readonly TradeService _trading;
    private readonly TradeSupervisor _supervisor;
    private readonly AssetRiskManager _riskManager;
    private readonly OrderManager _orderManager;
    private readonly StrategyLoaderService _strategyLoader;

    
    // Auto-reconciliation timer
    private DateTime _lastSyncCheckTime = DateTime.UtcNow;
    private readonly TimeSpan _syncCheckInterval = TimeSpan.FromMinutes(5);
    
    public AssetConfig Config { get; private set; }
    public bool IsRunning { get; private set; }
    
    public AssetTradingEngine(
        ILogger logger,
        BinanceRestClient restClient,
        AssetConfig config,
        StrategyLoaderService strategyLoader,
        DingTalkNotificationService? dingTalkService = null,
        TelegramNotificationService? telegramService = null)
    {
        _logger = logger;
        _restClient = restClient;
        Config = config;
        
        // Create Risk Manager
        _riskManager = new AssetRiskManager(
            config.Risk, 
            logger, 
            dingTalkService, 
            telegramService);
        
        // Create Order Manager
        _orderManager = new OrderManager();
        
        // Create Service Modules
        _marketData = new MarketDataService(logger, restClient, config.Symbol, config.Interval);
        _position = new PositionManager(logger, config.Symbol, config.Capital);
        _trading = new TradeService(logger, restClient, config, _position, _orderManager, _riskManager, dingTalkService, telegramService);
        _trading = new TradeService(logger, restClient, config, _position, _orderManager, _riskManager, dingTalkService, telegramService);
        
        // Dynamic Strategy Loading
        _strategyLoader = strategyLoader;
        var strategyName = config.AlphaModel ?? "ExampleStrategy"; // Default for backward compatibility
        var strategy = _strategyLoader.LoadStrategy(strategyName, config.StrategyDll);
        
        // Initialize strategy with parameters if any
        if (config.StrategyParams != null)
        {
            strategy.Initialize(config.StrategyParams);
        }
        else
        {
            strategy.Initialize(new Dictionary<string, object>());
        }

        _supervisor = new TradeSupervisor(logger, config, _position, _riskManager, _trading, strategy);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Initialize Market Data
        await _marketData.InitializeAsync(cancellationToken);
        _position.UpdateCurrentPrice(_marketData.CurrentPrice);
        
        // Check Open Orders
        await _trading.CheckOpenOrdersAsync(cancellationToken);
        
        // Verify Unconfirmed Orders
        await _trading.VerifyUnconfirmedOrderAsync(cancellationToken);
        
        // Verify Position Status
        await _trading.VerifyAndSyncPositionAsync(cancellationToken);
    }
    
    public void Start()
    {
        IsRunning = true;
        _logger.LogInformation("[{Symbol}] Trading engine started", Config.Symbol);
    }

    public void Stop()
    {
        IsRunning = false;
        _marketData.SaveKlines();
        _position.SaveState();
        _logger.LogInformation("[{Symbol}] Trading engine stopped", Config.Symbol);
    }

    /// <summary>
    /// Kline Update Callback (from WebSocket)
    /// </summary>
    public void OnKlineUpdate(IBinanceStreamKline kline)
    {
        if (!IsRunning) return;

        try
        {
            // Update Market Data
            var isClosed = _marketData.OnKlineUpdate(kline);
            _position.UpdateCurrentPrice(_marketData.CurrentPrice);

            if (isClosed)
            {
                // Daily Check
                _position.CheckNewDay();
                
                // Process Signal
                _supervisor.OnCandleUpdate(_marketData.GetCandles(), _marketData.CurrentPrice);
                
                // Periodic Reconciliation
                var timeSinceLastSync = DateTime.UtcNow - _lastSyncCheckTime;
                if (timeSinceLastSync >= _syncCheckInterval)
                {
                    _lastSyncCheckTime = DateTime.UtcNow;
                    _supervisor.PerformPeriodicSyncAsync().SafeFireAndForget(_logger, "PeriodicSync");
                }
                
                // Daily Report Check
                _supervisor.PerformDailyChecksAsync().SafeFireAndForget(_logger, "DailyChecks");
                
                // Cleanup Order History
                if (DateTime.UtcNow.Minute == 0 && DateTime.UtcNow.Second < 10)
                {
                    _orderManager.CleanupOldOrders(keepCount: 500);
                }
            }
            else
            {
                // Real-time Stop Loss Check
                _supervisor.CheckStopLoss(_marketData.CurrentPrice);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Symbol}] Error processing kline", Config.Symbol);
        }
    }

    /// <summary>
    /// Get Order Manager
    /// </summary>
    public OrderManager GetOrderManager() => _orderManager;

    /// <summary>
    /// Get Current Status
    /// </summary>
    public AssetStatus GetStatus()
    {
        var state = _position.State;
        return new AssetStatus
        {
            Symbol = Config.Symbol,
            IsRunning = IsRunning,
            IsInPosition = state.IsInPosition,
            Capital = state.Capital,
            CurrentPrice = state.CurrentPrice,
            EntryPrice = state.EntryPrice,
            Quantity = state.Quantity,
            StopLossPrice = state.StopLossPrice,
            UnrealizedPnL = state.IsInPosition 
                ? (state.CurrentPrice - state.EntryPrice) * state.Quantity 
                : 0,
            TodayPnL = state.TodayPnL,
            TodayTrades = state.TodayTrades,
            CurrentSignal = state.CurrentSignal,
            RiskPaused = state.RiskPaused,
            RiskPauseReason = state.RiskPauseReason,
            CandleCount = _marketData.CandleCount
        };
    }

    /// <summary>
    /// Reset Risk
    /// </summary>
    public void ResetRisk()
    {
        _riskManager.ResetDrawdownPause(_position.State, resetPeakCapital: true);
        _position.SaveState();
        _logger.LogWarning("[{Symbol}] 🔄 Risk controls reset", Config.Symbol);
    }

    /// <summary>
    /// Kline Backfill
    /// </summary>
    public async Task BackfillKlinesAsync(CancellationToken ct = default)
    {
        await _marketData.BackfillKlinesAsync(ct);
    }

    /// <summary>
    /// Manual Position Sync
    /// </summary>
    public async Task SyncToActualPositionAsync(CancellationToken ct = default)
    {
        await _trading.VerifyAndSyncPositionAsync(ct);
        _position.SaveState();
    }

    /// <summary>
    /// Update Config
    /// </summary>
    public void UpdateConfig(Api.AssetConfigUpdate update)
    {
        // Partial config update (modify properties directly)
        if (update.Capital.HasValue)
        {
            Config.Capital = update.Capital.Value;
        }
        if (!string.IsNullOrEmpty(update.Interval))
        {
            Config.Interval = update.Interval;
        }
        if (!string.IsNullOrEmpty(update.AlphaModel))
        {
            Config.AlphaModel = update.AlphaModel;
        }
        if (update.StopLoss != null)
        {
            Config.StopLoss = update.StopLoss;
        }
        if (update.Risk != null)
        {
            Config.Risk = update.Risk;
        }
        _logger.LogInformation("[{Symbol}] Config updated", Config.Symbol);
    }

    public void Dispose()
    {
        Stop();
    }
}
