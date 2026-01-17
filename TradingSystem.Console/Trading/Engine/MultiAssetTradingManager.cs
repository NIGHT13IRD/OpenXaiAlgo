using System.Collections.Concurrent;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Console.Api;
using TradingSystem.Console.Configuration;
using TradingSystem.Console.Services;

namespace TradingSystem.Console.Trading;

/// <summary>
/// Multi-Asset Trading Manager - Linux Console Version
/// 
/// Core Features:
/// - Manage multiple AssetTradingEngine instances
/// - Unified WebSocket data dispatching
/// - Manage global lifecycle (Start/Stop)
/// </summary>
public class MultiAssetTradingManager : IMultiAssetTradingManager, IDisposable
{
    private readonly ILogger<MultiAssetTradingManager> _logger;
    private readonly BinanceConfig _binanceConfig;
    private readonly List<AssetConfig> _assetConfigs;
    private readonly DingTalkConfig _dingTalkConfig;
    private readonly TelegramConfig _telegramConfig;
    
    // Binance Clients
    private BinanceRestClient? _restClient;
    private BinanceSocketClient? _socketClient;
    
    // Notification Services
    private DingTalkNotificationService? _dingTalkService;
    private TelegramNotificationService? _telegramService;
    
    // Strategy Loader
    private readonly StrategyLoaderService _strategyLoader;

    // Trading Engines for each asset
    private readonly ConcurrentDictionary<string, AssetTradingEngine> _engines = new();
    
    // WebSocket Subscriptions
    private readonly ConcurrentDictionary<string, UpdateSubscription> _subscriptions = new();
    
    private bool _isInitialized;
    private bool _isDisposed;

    public MultiAssetTradingManager(
        ILogger<MultiAssetTradingManager> logger,
        StrategyLoaderService strategyLoader,
        IOptions<BinanceConfig> binanceConfig,
        IOptions<List<AssetConfig>> assetConfigs,
        IOptions<DingTalkConfig> dingTalkConfig,
        IOptions<TelegramConfig> telegramConfig)
    {
        _logger = logger;
        _strategyLoader = strategyLoader;
        _binanceConfig = binanceConfig.Value;
        _assetConfigs = assetConfigs.Value;
        _dingTalkConfig = dingTalkConfig.Value;
        _telegramConfig = telegramConfig.Value;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;

        _logger.LogInformation("🚀 Initializing Multi-Asset Trading Manager...");
        _logger.LogInformation("   Binance: {Environment}", _binanceConfig.UseTestnet ? "TESTNET" : "MAINNET");
        _logger.LogInformation("   Assets: {Total} configured, {Enabled} enabled", _assetConfigs.Count, _assetConfigs.Count(a => a.Enabled));

        // Create Binance Client
        _restClient = new BinanceRestClient(options =>
        {
            if (_binanceConfig.UseTestnet)
            {
                options.Environment = BinanceEnvironment.Testnet;
            }
            options.ApiCredentials = new ApiCredentials(_binanceConfig.ApiKey, _binanceConfig.ApiSecret);
        });

        _socketClient = new BinanceSocketClient(options =>
        {
            if (_binanceConfig.UseTestnet)
            {
                options.Environment = BinanceEnvironment.Testnet;
            }
            options.ApiCredentials = new ApiCredentials(_binanceConfig.ApiKey, _binanceConfig.ApiSecret);
        });

        // Test Connection
        var pingResult = await _restClient.SpotApi.ExchangeData.PingAsync(cancellationToken);
        if (!pingResult.Success)
        {
            throw new Exception($"Failed to connect to Binance: {pingResult.Error?.Message}");
        }
        _logger.LogInformation("✅ Binance connection OK");

        // Check Time Sync
        var timeResult = await _restClient.SpotApi.ExchangeData.GetServerTimeAsync(cancellationToken);
        if (timeResult.Success)
        {
            var timeDiff = Math.Abs((DateTime.UtcNow - timeResult.Data).TotalMilliseconds);
            if (timeDiff > 1000)
            {
                _logger.LogWarning("⚠️ Time difference with Binance: {TimeDiff}ms (should be < 1000ms)", timeDiff);
            }
            else
            {
                _logger.LogInformation("✅ Time sync OK: {TimeDiff}ms", timeDiff);
            }
        }

        // Get Account Balance
        var accountResult = await _restClient.SpotApi.Account.GetAccountInfoAsync(ct: cancellationToken);
        if (accountResult.Success)
        {
            var usdtBalance = accountResult.Data.Balances.FirstOrDefault(b => b.Asset == "USDT");
            _logger.LogInformation("💰 USDT Available: {Balance:F2}", usdtBalance?.Available ?? 0);
        }

        // Initialize DingTalk Notification Service
        if (_dingTalkConfig.Enabled && !string.IsNullOrEmpty(_dingTalkConfig.WebhookUrl))
        {
            _dingTalkService = new DingTalkNotificationService(_dingTalkConfig.WebhookUrl, _dingTalkConfig.Secret);
            _logger.LogInformation("✅ DingTalk notification service enabled");
        }
        else
        {
            _logger.LogInformation("ℹ️ DingTalk notification disabled");
        }
        
        // Initialize Telegram Notification Service
        if (_telegramConfig.Enabled && !string.IsNullOrEmpty(_telegramConfig.BotToken) && !string.IsNullOrEmpty(_telegramConfig.ChatId))
        {
            _telegramService = new TelegramNotificationService(_telegramConfig.BotToken, _telegramConfig.ChatId);
            _logger.LogInformation("✅ Telegram notification service enabled");
        }
        else
        {
            _logger.LogInformation("ℹ️ Telegram notification disabled");
        }
        
        // Send System Start Notification
        var symbols = string.Join(", ", _assetConfigs.Where(a => a.Enabled).Select(a => a.Symbol));
        if (_dingTalkService != null)
        {
            await _dingTalkService.SendSystemStartAsync(symbols, "Multi-Asset");
        }
        if (_telegramService != null)
        {
            await _telegramService.SendSystemStartAsync(symbols, "Multi-Asset");
        }

        // Create trading engine for each asset - using individual asset config, not global config
        foreach (var assetConfig in _assetConfigs)
        {
            var engine = new AssetTradingEngine(
                _logger,
                _restClient,
                assetConfig,
                _strategyLoader,
                _dingTalkService,
                _telegramService);

            await engine.InitializeAsync(cancellationToken);
            _engines[assetConfig.Symbol] = engine;

            _logger.LogInformation("   ✅ {Symbol} engine initialized (Enabled: {Enabled})", assetConfig.Symbol, assetConfig.Enabled);

            // Add delay to avoid API rate limits
            await Task.Delay(100, cancellationToken);
        }

        _isInitialized = true;
        _logger.LogInformation("🎯 Multi-Asset Trading Manager ready");
    }

    public async Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Manager not initialized");
        }

        _logger.LogInformation("▶️ Starting all enabled assets...");

        foreach (var assetConfig in _assetConfigs.Where(a => a.Enabled))
        {
            await StartAssetAsync(assetConfig.Symbol);
        }
    }

    public async Task<bool> StartAssetAsync(string symbol)
    {
        if (!_engines.TryGetValue(symbol, out var engine))
        {
            _logger.LogWarning("Asset {Symbol} not found", symbol);
            return false;
        }

        if (engine.IsRunning)
        {
            _logger.LogWarning("{Symbol} is already running", symbol);
            return true;
        }

        try
        {
            // Subscribe to Kline Data
            var interval = ParseInterval(engine.Config.Interval);
            var subscribeResult = await _socketClient!.SpotApi.ExchangeData.SubscribeToKlineUpdatesAsync(
                symbol,
                interval,
                data =>
                {
                    // Exception protection to prevent callback errors from disconnecting WebSocket
                    try
                    {
                        engine.OnKlineUpdate(data.Data.Data);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing kline for {Symbol}", symbol);
                    }
                },
                CancellationToken.None);

            if (!subscribeResult.Success)
            {
                _logger.LogError("Failed to subscribe {Symbol}: {Error}", symbol, subscribeResult.Error?.Message);
                return false;
            }

            // Setup Disconnection Reconnection
            subscribeResult.Data.ConnectionLost += () =>
            {
                _logger.LogWarning("⚠️ {Symbol} WebSocket connection lost, reconnecting...", symbol);
                _ = SendWebSocketAlertAsync(symbol, false);
            };
            subscribeResult.Data.ConnectionRestored += (time) =>
            {
                _logger.LogInformation("✅ {Symbol} WebSocket reconnected at {Time}", symbol, time);
                _ = SendWebSocketAlertAsync(symbol, true, time);
                
                // Auto-backfill Kline after reconnection
                _ = engine.BackfillKlinesAsync();
            };

            _subscriptions[symbol] = subscribeResult.Data;
            engine.Start();

            _logger.LogInformation("▶️ {Symbol} trading started (Interval: {Interval})", symbol, engine.Config.Interval);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start {Symbol}", symbol);
            return false;
        }
    }

    public async Task<bool> StopAssetAsync(string symbol)
    {
        if (!_engines.TryGetValue(symbol, out var engine))
        {
            _logger.LogWarning("Asset {Symbol} not found", symbol);
            return false;
        }

        try
        {
            engine.Stop();

            if (_subscriptions.TryRemove(symbol, out var subscription))
            {
                await subscription.CloseAsync();
            }

            _logger.LogInformation("⏹️ {Symbol} trading stopped", symbol);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop {Symbol}", symbol);
            return false;
        }
    }

    public async Task StopAllAsync()
    {
        _logger.LogInformation("⏹️ Stopping all assets...");

        foreach (var symbol in _engines.Keys.ToList())
        {
            await StopAssetAsync(symbol);
        }

        // Send System Stop Notification
        if (_dingTalkService != null)
        {
            await _dingTalkService.SendSystemStopAsync("Manual shutdown");
        }

        _logger.LogInformation("✅ All assets stopped");
    }

    public AssetStatus? GetAssetStatus(string symbol)
    {
        if (!_engines.TryGetValue(symbol, out var engine))
            return null;

        return engine.GetStatus();
    }

    public List<AssetStatus> GetAllStatus()
    {
        return _engines.Values.Select(e => e.GetStatus()).ToList();
    }

    public bool ResetAssetRisk(string symbol)
    {
        if (!_engines.TryGetValue(symbol, out var engine))
            return false;

        engine.ResetRisk();
        _logger.LogWarning("🔄 {Symbol} risk counters reset", symbol);
        return true;
    }

    public void ResetAllRisk()
    {
        foreach (var engine in _engines.Values)
        {
            engine.ResetRisk();
        }
        _logger.LogWarning("🔄 All risk counters reset");
    }

    public List<AssetConfig> GetAllConfigs()
    {
        return _engines.Values.Select(e => e.Config).ToList();
    }

    public bool UpdateAssetConfig(string symbol, AssetConfigUpdate update)
    {
        if (!_engines.TryGetValue(symbol, out var engine))
            return false;

        engine.UpdateConfig(update);
        _logger.LogInformation($"⚙️ {symbol} config updated");
        return true;
    }

    private static KlineInterval ParseInterval(string interval)
    {
        return interval.ToLower() switch
        {
            "1m" => KlineInterval.OneMinute,
            "5m" => KlineInterval.FiveMinutes,
            "15m" => KlineInterval.FifteenMinutes,
            "30m" => KlineInterval.ThirtyMinutes,
            "1h" => KlineInterval.OneHour,
            "4h" => KlineInterval.FourHour,
            "1d" => KlineInterval.OneDay,
            _ => KlineInterval.FourHour
        };
    }

    private async Task SendWebSocketAlertAsync(string symbol, bool isConnected, TimeSpan? time = null)
    {
        try
        {
            if (isConnected)
            {
                var timeStr = time.HasValue ? time.Value.ToString(@"hh\:mm\:ss") : "unknown";
                
                if (_dingTalkService != null) 
                    await _dingTalkService.SendMarkdownAsync($"✅ {symbol} Reconnected", 
                        $"### ✅ WebSocket Reconnected\n\n" +
                        $"- **Symbol**: {symbol}\n" +
                        $"- **Duration**: {timeStr}\n" +
                        $"- **Time**: {DateTime.UtcNow:HH:mm:ss} UTC");
                
                if (_telegramService != null) 
                    await _telegramService.SendTextAsync(
                        $"<b>✅ {symbol} WebSocket Reconnected</b>\n\n" +
                        $"• Duration: {timeStr}\n" +
                        $"• Time: {DateTime.UtcNow:HH:mm:ss} UTC");
            }
            else
            {
                if (_dingTalkService != null) 
                    await _dingTalkService.SendErrorAsync("WebSocket Disconnected", $"{symbol} connection lost, reconnecting...");
                
                if (_telegramService != null) 
                    await _telegramService.SendErrorAsync("WebSocket Disconnected", $"{symbol} connection lost, reconnecting...");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WebSocket alert");
        }
    }

    /// <summary>
    /// Send System Stop Notification (DingTalk+Telegram)
    /// </summary>
    public async Task SendSystemStopNotificationAsync()
    {
        var symbols = string.Join(", ", _engines.Keys);
        var reason = $"Manual shutdown (Symbols: {symbols})";
        
        try
        {
            if (_dingTalkService != null)
            {
                await _dingTalkService.SendSystemStopAsync(reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send DingTalk stop notification");
        }
        
        try
        {
            if (_telegramService != null)
            {
                await _telegramService.SendSystemStopAsync(reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send Telegram stop notification");
        }
    }
    
    /// <summary>
    /// Hot Reload Config (No restart required)
    /// </summary>
    public async Task<bool> ReloadConfigAsync(string configPath = "appsettings.json")
    {
        try
        {
            _logger.LogInformation("🔄 Starting config hot reload...");
            
            var fullPath = Path.Combine(AppContext.BaseDirectory, configPath);
            if (!File.Exists(fullPath))
            {
                _logger.LogError("Config file not found: {Path}", fullPath);
                return false;
            }
            
            var json = await File.ReadAllTextAsync(fullPath);
            var config = System.Text.Json.JsonDocument.Parse(json);
            
            // Parse Assets Config
            var assetsElement = config.RootElement.GetProperty("Assets");
            var newConfigs = System.Text.Json.JsonSerializer.Deserialize<List<AssetConfig>>(assetsElement.GetRawText());
            
            if (newConfigs == null || newConfigs.Count == 0)
            {
                _logger.LogWarning("Assets configuration not found in config file");
                return false;
            }
            
            var updatedCount = 0;
            
            foreach (var newConfig in newConfigs)
            {
                if (_engines.TryGetValue(newConfig.Symbol, out var engine))
                {
                    // Update existing engine config
                    var update = new AssetConfigUpdate
                    {
                        Capital = newConfig.Capital,
                        Interval = newConfig.Interval,
                        AlphaModel = newConfig.AlphaModel,
                        StopLoss = newConfig.StopLoss,
                        Risk = newConfig.Risk
                    };
                    
                    engine.UpdateConfig(update);
                    updatedCount++;
                    
                    _logger.LogInformation("[{Symbol}] ✅ Config Hot Updated: Capital=${Capital}, StopLoss={StopLoss:P2}", 
                        newConfig.Symbol, newConfig.Capital, newConfig.StopLoss?.FixedPercent ?? 0);
                }
                else
                {
                    _logger.LogInformation("[{Symbol}] ℹ️ New asset config detected, restart required to enable", newConfig.Symbol);
                }
            }
            
            _logger.LogInformation("🔄 Hot Update Completed: {Count} assets updated", updatedCount);
            
            // Send Notification
            var message = $"🔄 Config Hot Update Completed\n\nUpdated {updatedCount} asset configs\n\nNo restart required, effective immediately!";
            if (_dingTalkService != null)
            {
                await _dingTalkService.SendTextAsync(message);
            }
            if (_telegramService != null)
            {
                await _telegramService.SendTextAsync(message);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Config hot update failed");
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Task.Run(async () => await StopAllAsync()).GetAwaiter().GetResult();

        foreach (var engine in _engines.Values)
        {
            engine.Dispose();
        }
        _engines.Clear();

        _restClient?.Dispose();
        _socketClient?.Dispose();
        _dingTalkService?.Dispose();
        _telegramService?.Dispose();

        _logger.LogInformation("🔚 Multi-Asset Trading Manager disposed");
    }
}
