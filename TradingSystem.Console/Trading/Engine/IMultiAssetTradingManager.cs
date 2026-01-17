using TradingSystem.Console.Api;
using TradingSystem.Console.Configuration;

namespace TradingSystem.Console.Trading;

/// <summary>
/// 🔌 Multi-Asset Trading Manager Interface
/// </summary>
public interface IMultiAssetTradingManager
{
    /// <summary>Initialize all assets</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    
    /// <summary>Start all enabled asset trading</summary>
    Task StartAllAsync(CancellationToken cancellationToken = default);
    
    /// <summary>Stop all asset trading</summary>
    Task StopAllAsync();
    
    /// <summary>Start single asset</summary>
    Task<bool> StartAssetAsync(string symbol);
    
    /// <summary>Stop single asset</summary>
    Task<bool> StopAssetAsync(string symbol);
    
    /// <summary>Get single asset status</summary>
    AssetStatus? GetAssetStatus(string symbol);
    
    /// <summary>Get all asset statuses</summary>
    List<AssetStatus> GetAllStatus();
    
    /// <summary>Reset single asset risk</summary>
    bool ResetAssetRisk(string symbol);
    
    /// <summary>Reset all asset risk</summary>
    void ResetAllRisk();
    
    /// <summary>Get all configurations</summary>
    List<AssetConfig> GetAllConfigs();
    
    /// <summary>Update asset configuration</summary>
    bool UpdateAssetConfig(string symbol, AssetConfigUpdate update);
    
    /// <summary>Send System Stop Notification (DingTalk+Telegram)</summary>
    Task SendSystemStopNotificationAsync();
    
    /// <summary>Hot Reload Config (No restart required)</summary>
    Task<bool> ReloadConfigAsync(string configPath = "appsettings.json");
}
