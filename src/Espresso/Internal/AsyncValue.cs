using System.Threading.Tasks;

namespace Espresso.Internal;

/// <summary>
/// Helpers for reasoning about a cache value that is a <see cref="Task{V}"/> (the async cache stores
/// futures as values). An "in-flight" future is one that has not yet completed successfully; such an
/// entry is physically present in the map but logically absent to size/expiry.
/// <para>
/// The async cache never keeps a future that completes to <c>null</c> or fails — <c>HandleCompletion</c>
/// removes those. So for entries that remain in the map, "completed successfully" is equivalent to
/// "ready with a non-null value", which lets the engine test readiness on the non-generic
/// <see cref="Task"/> without knowing the element type.
/// </para>
/// </summary>
internal static class AsyncValue
{
    /// <summary>Returns whether the future has completed successfully (an entry that stays cached).</summary>
    public static bool IsReady(Task? future) => future is { IsCompletedSuccessfully: true };

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
