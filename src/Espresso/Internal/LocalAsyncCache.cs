using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Stats;

namespace Espresso.Internal;

/// <summary>
/// An asynchronous cache backed by an <see cref="ILocalCache{K,V}"/> whose value type is the future
/// itself (<c>Task&lt;V&gt;</c>). Loading stores the incomplete future immediately and atomically; a
/// completion continuation then either removes the entry (on failure/null) or refreshes its weight and
/// expiry timestamps (on success).
/// </summary>
internal class LocalAsyncCache<K, V> : IAsyncCache<K, V>
    where K : notnull
    where V : class
{
    private protected readonly ILocalCache<K, Task<V>> Cache;

    internal LocalAsyncCache(ILocalCache<K, Task<V>> cache) => Cache = cache;

    public Task<V>? GetIfPresent(K key) => Cache.GetIfPresent(key, recordStats: true);

    public Task<V> Get(K key, Func<K, V?> mappingFunction)
    {
        ArgumentNullException.ThrowIfNull(mappingFunction);
        return Get(key, (k, _) => RunOnExecutor(() => mappingFunction(k)));
    }

    public Task<V> Get(K key, Func<K, CancellationToken, Task<V?>> mappingFunction)
    {
        ArgumentNullException.ThrowIfNull(mappingFunction);
        return GetInternal(key, mappingFunction, recordStats: true);
    }

    private protected Task<V> GetInternal(
        K key, Func<K, CancellationToken, Task<V?>> mappingFunction, bool recordStats)
    {
        Task<V>? present = Cache.GetIfPresent(key, recordStats: false);
        if (present != null)
        {
            if (recordStats)
            {
                Cache.StatsCounter.RecordHits(1);
            }
            return present;
        }

        long startTime = Cache.Ticker.Read();
        Task<V>? created = null;
        Task<V> future = Cache.ComputeIfAbsent(key, k =>
        {
            // The mapping function's task is stored immediately, still in-flight. Its result type is
            // Task<V?> (the loader may produce null); we adapt it to the stored Task<V> whose null/failed
            // completion triggers removal in HandleCompletion.
            Task<V> stored = Adapt(mappingFunction(k, CancellationToken.None));
            created = stored;
            return stored;
        }, recordStats, recordLoad: false)!;

        if (created != null && ReferenceEquals(created, future))
        {
            HandleCompletion(key, future, startTime);
        }
        return future;
    }

    /// <summary>Stores a future, replacing any prior mapping, and attaches the completion handler.</summary>
    public void Put(K key, Task<V> valueFuture)
    {
        ArgumentNullException.ThrowIfNull(valueFuture);
        long startTime = Cache.Ticker.Read();
        Task<V>? prior = Cache.Put(key, valueFuture);
        if (!ReferenceEquals(prior, valueFuture))
        {
            HandleCompletion(key, valueFuture, startTime);
        }
    }

    /// <summary>
    /// Attaches a continuation that finalizes an entry once its future resolves: a failed or
    /// null-completing future removes the entry (only if still mapped to it); a successful future is
    /// replaced with itself to refresh the weight and expiry timers now that the value is ready.
    /// </summary>
    private protected void HandleCompletion(K key, Task<V> future, long startTime)
    {
        future.ContinueWith(t =>
        {
            long loadTime = Cache.Ticker.Read() - startTime;
            V? value = t.IsCompletedSuccessfully ? t.Result : null;
            if (value == null)
            {
                Cache.StatsCounter.RecordLoadFailure(loadTime);
                Cache.Remove(key, future);
            }
            else
            {
                try
                {
                    // Replace the future with itself → re-runs the weigher and resets the expiry
                    // timestamps to now, so the real timers start from completion.
                    Cache.Replace(key, future, future);
                    Cache.StatsCounter.RecordLoadSuccess(loadTime);
                }
                catch
                {
                    Cache.StatsCounter.RecordLoadFailure(loadTime);
                    Cache.Remove(key, future);
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Attaches a continuation that finalizes a refresh once its in-flight future resolves. Unlike an
    /// initial load, a failed or null-completing refresh must PRESERVE the previous value (refresh is
    /// best-effort): the failing future is reverted to <paramref name="oldFuture"/> rather than
    /// removing the entry. A successful refresh is committed like a normal completion.
    /// </summary>
    private protected void HandleRefreshCompletion(K key, Task<V> refreshed, Task<V> oldFuture, long startTime)
    {
        refreshed.ContinueWith(t =>
        {
            long loadTime = Cache.Ticker.Read() - startTime;
            V? value = t.IsCompletedSuccessfully ? t.Result : null;
            if (value == null)
            {
                // Refresh failed (faulted, cancelled, or null result): keep the last good value by
                // reverting the entry from the failing future back to the previous one. Never remove.
                Cache.StatsCounter.RecordLoadFailure(loadTime);
                Cache.Replace(key, refreshed, oldFuture);
            }
            else
            {
                try
                {
                    // Replace the future with itself → re-runs the weigher and resets the expiry
                    // timers now that the value is ready.
                    Cache.Replace(key, refreshed, refreshed);
                    Cache.StatsCounter.RecordLoadSuccess(loadTime);
                }
                catch
                {
                    Cache.StatsCounter.RecordLoadFailure(loadTime);
                    Cache.Replace(key, refreshed, oldFuture);
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Adapts a <c>Task&lt;V?&gt;</c> loader result to the stored <c>Task&lt;V&gt;</c>; a null result faults.</summary>
    private protected static Task<V> Adapt(Task<V?> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tcs = new TaskCompletionSource<V>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                tcs.TrySetException(t.Exception!.InnerExceptions);
            }
            else if (t.IsCanceled)
            {
                tcs.TrySetCanceled();
            }
            else if (t.Result == null)
            {
                // A null load fails the stored future so HandleCompletion removes the entry.
                tcs.TrySetException(new NullValueException());
            }
            else
            {
                tcs.TrySetResult(t.Result);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>Runs a synchronous mapping on the cache executor, producing a Task.</summary>
    private protected Task<V?> RunOnExecutor(Func<V?> mapping)
    {
        var tcs = new TaskCompletionSource<V?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Cache.Executor.Execute(() =>
        {
            try { tcs.TrySetResult(mapping()); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }

    public Task<IReadOnlyDictionary<K, V>> GetAll(
        IEnumerable<K> keys,
        Func<IReadOnlyCollection<K>, CancellationToken, Task<IReadOnlyDictionary<K, V>>> mappingFunction)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(mappingFunction);
        return GetAllInternal(keys, mappingFunction);
    }

    private protected Task<IReadOnlyDictionary<K, V>> GetAllInternal(
        IEnumerable<K> keys,
        Func<IReadOnlyCollection<K>, CancellationToken, Task<IReadOnlyDictionary<K, V>>> mappingFunction)
    {
        // Snapshot the requested keys (deduplicated) and their current futures. Any key without a
        // future gets a proxy future inserted atomically so concurrent readers coalesce onto it.
        var futures = new Dictionary<K, Task<V>>();
        var proxies = new Dictionary<K, TaskCompletionSource<V>>();
        foreach (K key in keys)
        {
            if (futures.ContainsKey(key))
            {
                continue;
            }
            Task<V>? future = Cache.GetIfPresent(key, recordStats: false);
            if (future == null)
            {
                var proxy = new TaskCompletionSource<V>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<V>? prior = Cache.PutIfAbsent(key, proxy.Task);
                if (prior == null)
                {
                    future = proxy.Task;
                    proxies[key] = proxy;
                }
                else
                {
                    future = prior; // someone else is already loading it
                }
            }
            futures[key] = future;
        }

        Cache.StatsCounter.RecordMisses(proxies.Count);
        Cache.StatsCounter.RecordHits(futures.Count - proxies.Count);

        if (proxies.Count == 0)
        {
            return ComposeResult(futures);
        }

        long startTime = Cache.Ticker.Read();
        var missing = new List<K>(proxies.Keys);
        Task<IReadOnlyDictionary<K, V>> loaded;
        try
        {
            loaded = mappingFunction(missing, CancellationToken.None);
        }
        catch (Exception e)
        {
            CompleteProxiesOnFailure(proxies, startTime, e);
            return Task.FromException<IReadOnlyDictionary<K, V>>(e);
        }

        return loaded.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result != null)
            {
                CompleteProxiesOnSuccess(proxies, t.Result, startTime);
                return ComposeResult(futures);
            }

            // The bulk load failed: fail every proxy and propagate the error to the caller (a bulk
            // failure is not a partial success).
            Exception failure = t.Exception?.InnerExceptions is { Count: > 0 } inner
                ? inner[0]
                : new InvalidOperationException("bulk load returned null");
            CompleteProxiesOnFailure(proxies, startTime, failure);
            return Task.FromException<IReadOnlyDictionary<K, V>>(failure);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
        .Unwrap();
    }

    /// <summary>Fills the proxies from a successful bulk result: absent keys removed, present replaced.</summary>
    private void CompleteProxiesOnSuccess(
        Dictionary<K, TaskCompletionSource<V>> proxies, IReadOnlyDictionary<K, V> result, long startTime)
    {
        long loadTime = Cache.Ticker.Read() - startTime;
        // An empty result means no requested key was loaded — recorded as a failure (matches the
        // an empty result is booked as a load failure).
        if (result.Count == 0)
        {
            Cache.StatsCounter.RecordLoadFailure(loadTime);
        }
        else
        {
            Cache.StatsCounter.RecordLoadSuccess(loadTime);
        }

        foreach (KeyValuePair<K, TaskCompletionSource<V>> entry in proxies)
        {
            K key = entry.Key;
            TaskCompletionSource<V> proxy = entry.Value;
            if (result.TryGetValue(key, out V? value) && value != null)
            {
                proxy.TrySetResult(value);
                try { Cache.Replace(key, proxy.Task, proxy.Task); } // re-weigh + reset timers
                catch { Cache.Remove(key, proxy.Task); }
            }
            else
            {
                // No value for this requested key: drop the entry and resolve the proxy to null (not a
                // fault), so a caller that coalesced onto this proxy observes null, and the composed
                // GetAll result simply omits the key.
                Cache.Remove(key, proxy.Task);
                proxy.TrySetResult(null!);
            }
        }

        // Extra keys the loader returned that were not requested are cached as completed futures.
        foreach (KeyValuePair<K, V> entry in result)
        {
            if (!proxies.ContainsKey(entry.Key) && entry.Value != null)
            {
                try { Put(entry.Key, Task.FromResult(entry.Value)); }
                catch { /* a weigher/listener throw on an extra key must not fail the whole result */ }
            }
        }
    }

    /// <summary>Fails and removes every requested proxy when the bulk load fails.</summary>
    private void CompleteProxiesOnFailure(
        Dictionary<K, TaskCompletionSource<V>> proxies, long startTime, Exception failure)
    {
        long loadTime = Cache.Ticker.Read() - startTime;
        Cache.StatsCounter.RecordLoadFailure(loadTime);
        foreach (KeyValuePair<K, TaskCompletionSource<V>> entry in proxies)
        {
            Cache.Remove(entry.Key, entry.Value.Task);
            entry.Value.TrySetException(failure);
        }
    }

    /// <summary>Waits for all requested futures and returns the successfully-completed non-null values.</summary>
    private protected static Task<IReadOnlyDictionary<K, V>> ComposeResult(Dictionary<K, Task<V>> futures)
    {
        if (futures.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<K, V>>(new Dictionary<K, V>());
        }
        var array = new Task[futures.Count];
        int i = 0;
        foreach (Task<V> f in futures.Values)
        {
            array[i++] = f;
        }
        // WhenAll faults if any future faults; swallow via ContinueWith so partial results compose.
        return Task.WhenAll(array).ContinueWith(_ =>
        {
            var result = new Dictionary<K, V>(futures.Count);
            foreach (KeyValuePair<K, Task<V>> entry in futures)
            {
                if (entry.Value.IsCompletedSuccessfully && entry.Value.Result != null)
                {
                    result[entry.Key] = entry.Value.Result;
                }
            }
            return (IReadOnlyDictionary<K, V>)result;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Invalidate(K key) => Cache.Remove(key);

    public void InvalidateAll(IEnumerable<K> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (K key in keys)
        {
            Cache.Remove(key);
        }
    }

    public void InvalidateAll() => Cache.Clear();

    public long EstimatedSize() => Cache.EstimatedSize;

    public CacheStats Stats() => Cache.StatsSnapshot();

    public void CleanUp() => Cache.CleanUp();

    /// <summary>Releases background resources by disposing the underlying future-typed store.</summary>
    public void Dispose() => Cache.Dispose();

    /// <summary>Marks a null async load so the stored future faults and the entry is removed.</summary>
    internal sealed class NullValueException : Exception
    {
    }
}
