namespace TradingSystem.Console.Configuration;

/// <summary>
/// 🔧 Binance API Configuration
/// </summary>
public class BinanceConfig
{
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public bool UseTestnet { get; set; } = true;
}

/// <summary>
/// 🌐 HTTP Management API Configuration
/// </summary>
public class ManagementConfig
{
    public int HttpPort { get; set; } = 8080;
    public bool EnableApi { get; set; } = true;
    public string ApiToken { get; set; } = "";
}

/// <summary>
/// 🔔 DingTalk Notification Configuration
/// </summary>
public class DingTalkConfig
{
    public bool Enabled { get; set; } = false;
    public string WebhookUrl { get; set; } = "";
    public string Secret { get; set; } = "";
}

/// <summary>
/// 📱 Telegram Notification Configuration
/// </summary>
public class TelegramConfig
{
    public bool Enabled { get; set; } = false;
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
}

/// <summary>
/// 🛡️ Global Risk Configuration
/// </summary>
public class GlobalRiskConfig
{
    public decimal MaxDailyLossPercent { get; set; } = 0.10m;
    public decimal MaxTotalDrawdownPercent { get; set; } = 0.30m;
    public int MaxConsecutiveLosses { get; set; } = 3;
    public int MaxDailyTrades { get; set; } = 20;
}

/// <summary>
/// 📊 Single Asset Configuration
/// </summary>
public class AssetConfig
{
    public string Symbol { get; set; } = "BTCUSDT";
    public bool Enabled { get; set; } = true;
    public string Interval { get; set; } = "4h";
    public decimal Capital { get; set; } = 1000m;
    public string AlphaModel { get; set; } = "ExampleStrategy";
    /// <summary>
    /// Strategy DLL Name (e.g. "MyStrategy.dll")
    /// </summary>
    public string? StrategyDll { get; set; }
    
    /// <summary>
    /// Strategy Parameter Dictionary
    /// </summary>
    public Dictionary<string, object>? StrategyParams { get; set; }

    public StopLossConfig StopLoss { get; set; } = new();
    public AssetRiskConfig Risk { get; set; } = new();
}

/// <summary>
/// 🛑 Stop Loss Configuration
/// </summary>
public class StopLossConfig
{
    /// <summary>Stop Loss Type: Fixed, Trailing</summary>
    public string Type { get; set; } = "Fixed";
    public decimal FixedPercent { get; set; } = 0.06m;
    public decimal TrailingPercent { get; set; } = 0.03m;
    public decimal TrailingActivation { get; set; } = 0.02m;
}

/// <summary>
/// 🎯 Single Asset Risk Configuration
/// </summary>
public class AssetRiskConfig
{
    public decimal MaxDailyLossPercent { get; set; } = 0.10m;
    public decimal MaxTotalDrawdownPercent { get; set; } = 0.30m;
    public int MaxConsecutiveLosses { get; set; } = 2;
    public int MaxDailyTrades { get; set; } = 10;
}

/// <summary>
/// 📦 Complete Configuration Root Object
/// </summary>
public class AppConfiguration
{
    public BinanceConfig Binance { get; set; } = new();
    public ManagementConfig Management { get; set; } = new();
    public GlobalRiskConfig GlobalRisk { get; set; } = new();
    public List<AssetConfig> Assets { get; set; } = new();
}
