using TradingSystem.Console.Trading;
using TradingSystem.Console.Trading.Strategy;

namespace TradingSystem.Strategies.Example;

/// <summary>
/// 🟢 Example Strategy
/// A simple demonstration of how to implement the IStrategy interface.
/// This strategy randomly enters and exits for testing purposes.
/// </summary>
public class ExampleStrategy : IStrategy
{
    private Random _random = new Random();
    
    public string Name => "ExampleStrategy";

    public void Initialize(Dictionary<string, object> parameters)
    {
        // Initialize logic here
        // Example: int period = int.Parse(parameters["Period"].ToString());
    }
    
    public TradingSignal ProcessCandles(IReadOnlyList<Candle> candles)
    {
        // Implement your specific logic here
        // For demonstration, we just return Neutral
        if (candles.Count < 10) return TradingSignal.Neutral;

        // Simulate a signal
        // return TradingSignal.StrongBull; 
        
        return TradingSignal.Neutral;
    }
    
    public bool ShouldEnter(TradingSignal signal, TradingState state)
    {
        return !state.IsInPosition && signal == TradingSignal.StrongBull;
    }
    
    public bool ShouldExit(TradingSignal signal, TradingState state)
    {
        return state.IsInPosition && signal == TradingSignal.StrongBear;
    }
}
