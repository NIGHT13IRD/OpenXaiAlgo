using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using TradingSystem.Console.Api;
using TradingSystem.Console.Configuration;
using TradingSystem.Console.Services;
using TradingSystem.Console.Trading;


namespace TradingSystem.Console;

/// <summary>
/// 🐧 Linux VPS Console Trading Program Entry Point
/// Supports systemd daemon management
/// 
/// Core Features:
/// - Multi-asset parallel trading
/// - HTTP API management interface
/// - Independent risk control calculation
/// - WebSocket disconnection reconnection
/// - State persistence
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: "logs/trading-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Auto-restart mechanism
        const int maxRestarts = 5;
        const int baseDelaySeconds = 5;
        var restartCount = 0;
        var lastRestartTime = DateTime.MinValue;
        
        while (true)
        {
            try
            {
                // Reset counter if last restart was over 1 hour ago
                if ((DateTime.UtcNow - lastRestartTime).TotalHours > 1)
                {
                    restartCount = 0;
                }
                
                // Parse command line arguments
                // Default: Live mode
                await RunLiveModeAsync(args);
                
                // Normal exit, no restart needed
                break;
            }
            catch (Exception ex)
            {
                restartCount++;
                lastRestartTime = DateTime.UtcNow;
                
                Log.Fatal(ex, "❌ Application terminated unexpectedly (Restart attempt {Attempt}/{Max})", 
                    restartCount, maxRestarts);
                
                if (restartCount >= maxRestarts)
                {
                    Log.Fatal("❌ Max restart attempts reached. Giving up.");
                    break;
                }
                
                // Exponential backoff delay
                var delaySeconds = baseDelaySeconds * (int)Math.Pow(2, restartCount - 1);
                delaySeconds = Math.Min(delaySeconds, 300);  // Max 5 minutes
                
                Log.Warning("🔄 Auto-restart in {Delay} seconds...", delaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                
                Log.Information("🔄 Attempting restart #{Attempt}...", restartCount);
            }
        }
        
        Log.CloseAndFlush();
    }
    
    private static async Task RunLiveModeAsync(string[] args)
    {
        Log.Information("============================================");
        Log.Information("🚀 TradingSystem Console v2.0 Starting...");
        Log.Information("   Platform: {OS}", Environment.OSVersion);
        Log.Information("   Runtime: {Runtime}", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Log.Information("   Time: {Time} UTC", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        Log.Information("============================================");

        var host = CreateHostBuilder(args).Build();
        await host.RunAsync();
    }
    
    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true);
                // Support environment variable override (for Docker/K8s)
                config.AddEnvironmentVariables(prefix: "TRADING_");
            })
            .ConfigureServices((context, services) =>
            {
                // Bind configuration - Each asset uses independent AssetRiskConfig, no global risk control needed
                services.Configure<BinanceConfig>(context.Configuration.GetSection("Binance"));
                services.Configure<ManagementConfig>(context.Configuration.GetSection("Management"));
                services.Configure<DingTalkConfig>(context.Configuration.GetSection("DingTalk"));
                services.Configure<TelegramConfig>(context.Configuration.GetSection("Telegram"));
                services.Configure<List<AssetConfig>>(context.Configuration.GetSection("Assets"));

                // Register services
                services.AddSingleton<IMultiAssetTradingManager, MultiAssetTradingManager>();
                // Services
                services.AddSingleton<StrategyLoaderService>();
                services.AddSingleton<ManagementApiService>();
                services.AddSingleton<HealthCheckService>();

                // Register background workers
                services.AddHostedService<TradingHostedService>();
            });
}

/// <summary>
/// 🔄 Trading Background Service
/// </summary>
public class TradingHostedService : IHostedService
{
    private readonly ILogger<TradingHostedService> _logger;
    private readonly IMultiAssetTradingManager _tradingManager;
    private readonly ManagementApiService _apiService;
    private readonly HealthCheckService _healthCheckService;
    private readonly ManagementConfig _managementConfig;
    private CancellationTokenSource? _cts;
    private Task? _apiTask;

    public TradingHostedService(
        ILogger<TradingHostedService> logger,
        IMultiAssetTradingManager tradingManager,
        ManagementApiService apiService,
        HealthCheckService healthCheckService,
        IOptions<ManagementConfig> managementConfig)
    {
        _logger = logger;
        _tradingManager = tradingManager;
        _apiService = apiService;
        _healthCheckService = healthCheckService;
        _managementConfig = managementConfig.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // Start simple health check only if Management API is disabled to avoid port conflict
            // If Management API is enabled, it provides /health and /status endpoints
            if (!_managementConfig.EnableApi)
            {
                await _healthCheckService.StartAsync();
            }
            else
            {
                _logger.LogInformation("ℹ️ HealthCheckService skipped because Management API is enabled (Port conflict avoidance)");
            }

            // Initialize trading manager
            await _tradingManager.InitializeAsync(_cts.Token);

            // Start HTTP API
            _apiTask = _apiService.StartAsync(_cts.Token);

            // Start all enabled asset trading
            await _tradingManager.StartAllAsync(_cts.Token);

            _logger.LogInformation("✅ Trading system started successfully");
            _logger.LogInformation("📡 Use HTTP API to manage trading:");
            _logger.LogInformation("   curl http://localhost:8080/status  (Status)");
            _logger.LogInformation("   curl http://localhost:8080/health  (Health)");
            _logger.LogInformation("   curl -X POST http://localhost:8080/risk/reset/all");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to start trading system");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 Stopping trading system...");

        // Send system stop notification (DingTalk+Telegram)
        try
        {
            await _tradingManager.SendSystemStopNotificationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send stop notification");
        }

        _cts?.Cancel();
        _apiService.Stop();
        await _healthCheckService.StopAsync();

        await _tradingManager.StopAllAsync();

        if (_apiTask != null)
        {
            try
            {
                await _apiTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _logger.LogInformation("✅ Trading system stopped");
    }
}

