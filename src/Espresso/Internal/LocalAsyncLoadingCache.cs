using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Espresso.Internal;

/// <summary>
/// An <see cref="IAsyncLoadingCache{K,V}"/> that loads values via an <see cref="IAsyncCacheLoader{K,V}"/>.
/// </summary>
internal sealed class LocalAsyncLoadingCache<K, V> : LocalAsyncCache<K, V>, IAsyncLoadingCache<K, V>
    where K : notnull
    where V : class
{
    private readonly IAsyncCacheLoader<K, V> _loader;

    internal LocalAsyncLoadingCache(ILocalCache<K, Task<V>> cache, IAsyncCacheLoader<K, V> loader)
        : base(cache)
    {
        _loader = loader;

        // Install the async-reload delegate so the engine's refresh-on-read path can reload async
        // caches. Given the current (ready) stored future, it starts an AsyncReload, adapts it to a
        // new in-flight future, attaches completion handling, and returns it to become the new value.
        if (cache is BoundedLocalCache<K, Task<V>> bounded)
        {
            bounded.SetAsyncReload((key, oldFuture) =>
            {
                V? current = AsyncValue.GetIfReady(oldFuture);
                if (current == null)
                {
                    return null; // the current future is not ready — not eligible for refresh
                }
                long startTime = Cache.Ticker.Read();
                Task<V> refreshed = Adapt(_loader.AsyncReload(key, current, CancellationToken.None));
                // If the reload already completed unsuccessfully (e.g. a synchronous failure/null),
                // don't install it — that would replace the good value with a failed future. Leave the
                // existing value in place and record the failure.
                if (refreshed.IsCompleted && !refreshed.IsCompletedSuccessfully)
                {
                    Cache.StatsCounter.RecordLoadFailure(Cache.Ticker.Read() - startTime);
                    return null;
                }
                // Otherwise the reload is genuinely in flight: a later failure reverts to the old value
                // (best-effort refresh) rather than removing the entry.
                HandleRefreshCompletion(key, refreshed, oldFuture, startTime);
                return refreshed;
            });
        }
    }

    public Task<V> Get(K key)
        => GetInternal(key, (k, ct) => _loader.AsyncLoad(k, ct), recordStats: true);

    public Task<IReadOnlyDictionary<K, V>> GetAll(IEnumerable<K> keys)
        => GetAllInternal(keys, (missing, ct) => _loader.AsyncLoadAll(missing, ct));
}
