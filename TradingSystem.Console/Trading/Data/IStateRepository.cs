namespace TradingSystem.Console.Trading.Data;

/// <summary>
/// 🔥 State Repository Interface - Abstract Persistence Implementation
/// </summary>
public interface IStateRepository
{
    /// <summary>
    /// Load Trading State
    /// </summary>
    Task<TradingState?> LoadStateAsync(string symbol);
    
    /// <summary>
    /// Save Trading State
    /// </summary>
    Task SaveStateAsync(TradingState state);
    
    /// <summary>
    /// Load Kline Data
    /// </summary>
    Task<IList<Candle>?> LoadKlinesAsync(string symbol);
    
    /// <summary>
    /// Save Kline Data
    /// </summary>
    Task SaveKlinesAsync(string symbol, IList<Candle> candles);
}
