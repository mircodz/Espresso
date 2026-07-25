using System.Threading.Tasks;

namespace Espresso.Internal;

/// <summary>
/// Wraps a user <see cref="IExpiry{K,V}"/> for an async cache whose stored values are
/// <see cref="Task{V}"/>. While a future is still loading the entry is pinned with the async sentinel
/// (<see cref="BoundedLocalCache{K,V}.AsyncExpiry"/>, ~150 years) so it never expires mid-load; once the
/// value is ready the real per-entry duration is computed from the delegate. An update whose current
/// duration still holds the sentinel is routed to <see cref="IExpiry{K,V}.ExpireAfterCreate"/> — this is
/// the completion transition from in-flight to a real value.
/// </summary>
internal sealed class AsyncExpiry<K, V> : IExpiry<K, Task<V>>
    where K : notnull
    where V : class
{
    private readonly IExpiry<K, V> _delegate;

    public AsyncExpiry(IExpiry<K, V> expiry) => _delegate = expiry;

    public long ExpireAfterCreate(K key, Task<V> future, long currentTime)
    {
        V? value = AsyncValue.GetIfReady(future);
        if (value != null)
        {
            long duration = _delegate.ExpireAfterCreate(key, value, currentTime);
            return System.Math.Min(duration, BoundedLocalCache<K, V>.MaximumExpiry);
        }
        return BoundedLocalCache<K, V>.AsyncExpiry;
    }

    public long ExpireAfterUpdate(K key, Task<V> future, long currentTime, long currentDuration)
    {
        V? value = AsyncValue.GetIfReady(future);
        if (value != null)
        {
            long duration = (currentDuration > BoundedLocalCache<K, V>.MaximumExpiry)
                ? _delegate.ExpireAfterCreate(key, value, currentTime)
                : _delegate.ExpireAfterUpdate(key, value, currentTime, currentDuration);
            return System.Math.Min(duration, BoundedLocalCache<K, V>.MaximumExpiry);
        }
        return BoundedLocalCache<K, V>.AsyncExpiry;
    }

    public long ExpireAfterRead(K key, Task<V> future, long currentTime, long currentDuration)
    {
        V? value = AsyncValue.GetIfReady(future);
        if (value != null)
        {
            long duration = _delegate.ExpireAfterRead(key, value, currentTime, currentDuration);
            return System.Math.Min(duration, BoundedLocalCache<K, V>.MaximumExpiry);
        }
        return BoundedLocalCache<K, V>.AsyncExpiry;
    }
}
