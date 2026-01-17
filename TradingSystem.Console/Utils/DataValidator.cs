using TradingSystem.Console.Services;

namespace TradingSystem.Console.Utils;

/// <summary>
/// Data validation utility
/// </summary>
public static class DataValidator
{
    /// <summary>
    /// Validate candle data
    /// </summary>
    public static bool ValidateCandle(Candle candle, out string? errorMessage)
    {
        errorMessage = null;

        // Check if prices are positive
        if (candle.Open <= 0 || candle.High <= 0 || candle.Low <= 0 || candle.Close <= 0)
        {
            errorMessage = $"Prices must be positive (O:{candle.Open} H:{candle.High} L:{candle.Low} C:{candle.Close}) at {candle.OpenTime}";
            return false;
        }

        // Check if prices are NaN or Infinity
        if (double.IsNaN((double)candle.Open) || double.IsInfinity((double)candle.Open) ||
            double.IsNaN((double)candle.High) || double.IsInfinity((double)candle.High) ||
            double.IsNaN((double)candle.Low) || double.IsInfinity((double)candle.Low) ||
            double.IsNaN((double)candle.Close) || double.IsInfinity((double)candle.Close))
        {
            errorMessage = $"Prices contain invalid values (NaN/Infinity) at {candle.OpenTime}";
            return false;
        }

        // Check High >= Low
        if (candle.High < candle.Low)
        {
            errorMessage = $"High price cannot be lower than Low price (H:{candle.High} < L:{candle.Low}) at {candle.OpenTime}";
            return false;
        }

        // Check High >= Open, High >= Close
        if (candle.High < candle.Open || candle.High < candle.Close)
        {
            errorMessage = $"High price must be >= Open and Close prices (H:{candle.High} O:{candle.Open} C:{candle.Close}) at {candle.OpenTime}";
            return false;
        }

        // Check Low <= Open, Low <= Close
        if (candle.Low > candle.Open || candle.Low > candle.Close)
        {
            errorMessage = $"Low price must be <= Open and Close prices (L:{candle.Low} O:{candle.Open} C:{candle.Close}) at {candle.OpenTime}";
            return false;
        }

        // Check volume
        if (candle.Volume < 0)
        {
            errorMessage = $"Volume cannot be negative (V:{candle.Volume}) at {candle.OpenTime}";
            return false;
        }

        // Check number of trades
        if (candle.NumberOfTrades < 0)
        {
            errorMessage = $"Number of trades cannot be negative (N:{candle.NumberOfTrades}) at {candle.OpenTime}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validate candle list
    /// </summary>
    public static (bool isValid, List<Candle> validCandles, int invalidCount) ValidateCandles(List<Candle> candles)
    {
        var validCandles = new List<Candle>();
        int invalidCount = 0;

        foreach (var candle in candles)
        {
            if (ValidateCandle(candle, out string? errorMessage))
            {
                validCandles.Add(candle);
            }
            else
            {
                invalidCount++;
                System.Console.WriteLine($"[WARNING] Candle data validation failed: {errorMessage}");
            }
        }

        if (invalidCount > 0)
        {
            System.Console.WriteLine($"[WARNING] Candle validation completed: {validCandles.Count} valid, {invalidCount} invalid");
        }

        return (invalidCount == 0, validCandles, invalidCount);
    }

    /// <summary>
    /// Check if candle list is sorted by time
    /// </summary>
    public static bool ValidateTimeSequence(List<Candle> candles)
    {
        for (int i = 1; i < candles.Count; i++)
        {
            if (candles[i].OpenTime <= candles[i - 1].OpenTime)
            {
                System.Console.WriteLine($"[WARNING] Candle time sequence error: index {i} time {candles[i].OpenTime} <= index {i-1} time {candles[i-1].OpenTime}");
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Check for duplicate candles
    /// </summary>
    public static bool HasDuplicateCandles(List<Candle> candles, out List<DateTime> duplicateTimes)
    {
        duplicateTimes = new List<DateTime>();
        var timeSet = new HashSet<DateTime>();

        foreach (var candle in candles)
        {
            if (!timeSet.Add(candle.OpenTime))
            {
                duplicateTimes.Add(candle.OpenTime);
            }
        }

        if (duplicateTimes.Count > 0)
        {
            System.Console.WriteLine($"[WARNING] Found {duplicateTimes.Count} candles with duplicate timestamps");
        }

        return duplicateTimes.Count > 0;
    }
}
