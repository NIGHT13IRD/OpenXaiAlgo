using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using TradingSystem.Console.Trading.Strategy;

namespace TradingSystem.Console.Services;

/// <summary>
/// 🔌 Strategy Loading Service
/// Responsible for loading strategy plugins from external DLLs
/// </summary>
public class StrategyLoaderService
{
    private readonly ILogger<StrategyLoaderService> _logger;
    private readonly string _pluginsDirectory;

    public StrategyLoaderService(ILogger<StrategyLoaderService> logger)
    {
        _logger = logger;
        _pluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
        
        if (!Directory.Exists(_pluginsDirectory))
        {
            Directory.CreateDirectory(_pluginsDirectory);
        }
    }

    /// <summary>
    /// Load strategy by name
    /// </summary>
    /// <param name="strategyName">Strategy Class Name (e.g. "ExampleStrategy")</param>
    /// <param name="dllName">DLL Filename (Optional, default same as strategy name)</param>
    /// <returns>Strategy Instance</returns>
    public IStrategy LoadStrategy(string strategyName, string? dllName = null)
    {
        try
        {
            dllName ??= strategyName;
            if (!dllName.EndsWith(".dll")) dllName += ".dll";

            // 1. Find Path
            // Prioritize plugins directory
            var dllPath = Path.Combine(_pluginsDirectory, dllName);
            if (!File.Exists(dllPath))
            {
                // Then find in base directory
                dllPath = Path.Combine(AppContext.BaseDirectory, dllName);
            }

            if (!File.Exists(dllPath))
            {
                throw new FileNotFoundException($"Strategy file not found: {dllName} (Search Path: {_pluginsDirectory})");
            }

            _logger.LogInformation("🔌 Loading strategy from: {Path}", dllPath);

            // 2. Load Assembly
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);

            // 3. Find Type
            // Fuzzy match: Find class that implements IStrategy and name contains strategyName
            var strategyType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(IStrategy).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract && t.Name.Contains(strategyName));

            if (strategyType == null)
            {
                throw new Exception($"Class implementing IStrategy {strategyName} not found in {dllName}");
            }

            // 4. Instantiate
            var strategy = Activator.CreateInstance(strategyType) as IStrategy;
            if (strategy == null)
            {
                throw new Exception($"Cannot instantiate strategy {strategyType.Name}");
            }

            _logger.LogInformation("✅ Strategy loaded: {Name} ({Type})", strategy.Name, strategyType.FullName);
            return strategy;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load strategy: {Name}", strategyName);
            throw;
        }
    }
}
