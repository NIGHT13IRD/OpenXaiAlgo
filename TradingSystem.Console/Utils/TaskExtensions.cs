using Microsoft.Extensions.Logging;

namespace TradingSystem.Console.Utils;

/// <summary>
/// 🔥 Task Extension Methods
/// Provides safe Fire-and-Forget task execution, avoiding silent failures from unobserved exceptions
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Safely execute Fire-and-Forget task, capturing and logging any exceptions
    /// </summary>
    /// <param name="task">Task to execute</param>
    /// <param name="logger">Logger for recording exceptions</param>
    /// <param name="context">Context information to identify the task</param>
    public static void SafeFireAndForget(this Task task, ILogger? logger = null, string? context = null)
    {
        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                var innerException = t.Exception.InnerException ?? t.Exception;
                logger?.LogError(innerException, "🔥 Fire-and-forget task failed: {Context}", context ?? "unknown");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Safely execute Fire-and-Forget task (Generic version)
    /// </summary>
    public static void SafeFireAndForget<T>(this Task<T> task, ILogger? logger = null, string? context = null)
    {
        ((Task)task).SafeFireAndForget(logger, context);
    }

    /// <summary>
    /// Fire-and-Forget task execution with callback
    /// </summary>
    /// <param name="task">Task to execute</param>
    /// <param name="onError">Exception handling callback</param>
    public static void SafeFireAndForget(this Task task, Action<Exception>? onError)
    {
        task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                var innerException = t.Exception.InnerException ?? t.Exception;
                onError?.Invoke(innerException);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
