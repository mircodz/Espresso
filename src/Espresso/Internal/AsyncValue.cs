using System.Threading.Tasks;

namespace Espresso.Internal;

/// <summary>
/// Helpers for reasoning about a cache value that is a <see cref="Task{V}"/> (the async cache stores
/// futures as values). A future is "ready" once it has completed successfully with a non-null value
/// (Caffeine's <c>Async.isReady</c>); any other future is treated as in flight, so its entry is
/// physically present in the map but logically absent to size/expiry.
/// <para>
/// A future that completes with <c>null</c> or fails is not kept: <c>HandleCompletion</c> removes it.
/// Until it does, such a future is not ready, so it is never weighed, expired or evicted as a value.
/// </para>
/// </summary>
internal static class AsyncValue
{
    /// <summary>Returns whether the future has completed successfully with a non-null value.</summary>
    public static bool IsReady<V>(Task<V>? future) where V : class => GetIfReady(future) != null;

    /// <summary>Returns the completed non-null value, or <c>null</c> if not done, failed, or null.</summary>
    public static V? GetIfReady<V>(Task<V>? future) where V : class
        => (future is { IsCompletedSuccessfully: true }) ? future.Result : null;

    /// <summary>
    /// Blocks until the future completes and returns the value, or <c>null</c> if it failed or was
    /// cancelled. Used by the synchronous view (join semantics).
    /// </summary>
    public static V? GetWhenSuccessful<V>(Task<V>? future) where V : class
    {
        if (future == null)
        {
            return null;
        }
        try
        {
            return future.GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }
}
