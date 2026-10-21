using System;
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

    internal LocalAsyncLoadingCache(BoundedLocalCache<K, Task<V>> cache, IAsyncCacheLoader<K, V> loader)
        : base(cache)
    {
        _loader = loader;

        // Install the async-reload delegate so the engine's refresh-on-read path can reload async
        // caches. Given the current (ready) stored future, it starts an AsyncReload and maps its outcome
        // to the value the engine commits: a completed future holding the reloaded value (the current
        // future itself when the reload returned the same instance, so no replacement is reported), null
        // when the reload produced no value, or a fault when the reload failed or was canceled.
        cache.SetAsyncReload((key, oldFuture) =>
        {
            V? current = AsyncValue.GetIfReady(oldFuture);
            if (current == null)
            {
                return null; // the current future is not ready, so it is not eligible for refresh
            }
            Task<V?> reload = _loader.AsyncReload(key, current, CancellationToken.None)
                ?? throw new InvalidOperationException("The loader returned a null reload task");
            return reload.ContinueWith(static (t, state) =>
            {
                var (oldFuture, current) = ((Task<V>, V))state!;
                V? value = t.GetAwaiter().GetResult(); // completed; rethrows the reload's failure
                if (value == null)
                {
                    return null;
                }
                return ReferenceEquals(value, current) ? oldFuture : Task.FromResult(value);
            }, (oldFuture, current), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }, static (currentFuture, newFuture) =>
        {
            V? value = AsyncValue.GetIfReady(newFuture);
            return (value != null) && ReferenceEquals(value, AsyncValue.GetIfReady(currentFuture));
        });
    }

    public Task<V> Get(K key)
        => GetInternal(key, (k, ct) => _loader.AsyncLoad(k, ct), recordStats: true);

    public Task<IReadOnlyDictionary<K, V>> GetAll(IEnumerable<K> keys)
        => GetAllInternal(keys, (missing, ct) => _loader.AsyncLoadAll(missing, ct));
}
