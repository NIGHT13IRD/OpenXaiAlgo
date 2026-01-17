namespace TradingSystem.Console.Trading.Strategy;

/// <summary>
/// 🔥 Strategy Interface - Abstract signal generation logic
/// </summary>
public interface IStrategy
{
    /// <summary>
    /// Strategy Name
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Initialize strategy parameters
    /// </summary>
    /// <param name="parameters">Configuration parameters dictionary</param>
    void Initialize(Dictionary<string, object> parameters);

    /// <summary>
    /// Process candle data and calculate signal
    /// </summary>
    /// <param name="candles">Candle list</param>
    /// <returns>Current signal</returns>
    TradingSignal ProcessCandles(IReadOnlyList<Candle> candles);
    
    /// <summary>
    /// Check if should enter position
    /// </summary>
    /// <param name="signal">Current signal</param>
    /// <param name="state">Trading state</param>
    /// <returns>Whether to enter</returns>
    bool ShouldEnter(TradingSignal signal, TradingState state);
    
    /// <summary>
    /// Check if should exit position (Signal exit, not stop loss)
    /// </summary>
    /// <param name="signal">Current signal</param>
    /// <param name="state">Trading state</param>
    /// <returns>Whether to exit</returns>
    bool ShouldExit(TradingSignal signal, TradingState state);
}
