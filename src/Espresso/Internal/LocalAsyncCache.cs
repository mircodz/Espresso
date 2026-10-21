using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
    private protected readonly BoundedLocalCache<K, Task<V>> Cache;

    internal LocalAsyncCache(BoundedLocalCache<K, Task<V>> cache) => Cache = cache;

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

        return Load(key, mappingFunction, recordStats);
    }

    /// <summary>
    /// The miss path of <see cref="GetInternal"/>. Under the map's bin lock only an in-flight
    /// placeholder is installed; the mapping function runs after the lock is released and its outcome
    /// completes the placeholder. So a mapping function's synchronous prefix may itself read or load
    /// other keys (including keys in the same bin) and never blocks writers, while concurrent callers
    /// for the key still coalesce onto the one placeholder and the function runs at most once.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<V> Load(K key, Func<K, CancellationToken, Task<V?>> mappingFunction, bool recordStats)
    {
        long startTime = Cache.StatsTicker.Read();
        var load = new PendingLoad(key, mappingFunction);
        Task<V> future;
        try
        {
            // The mapping function starts as soon as the placeholder is published (outside the bin lock),
            // ahead of the insertion's policy work, so its own work is never queued behind maintenance.
            future = Cache.ComputeIfAbsent(key, load.Install, recordStats, recordLoad: false, onInserted: load)!;
        }
        catch when (load.Started)
        {
            // The insertion failed after the load started: settle the placeholder, then propagate.
            Settle(key, load, startTime, recordStats);
            throw;
        }
        if (!load.Started)
        {
            return future; // another caller's load (or a concurrent write) won
        }

        Settle(key, load, startTime, recordStats);
        load.StartFailure?.Throw();
        return load.Task;
    }

    /// <summary>
    /// Completes a published placeholder from its load. A load that failed to start drops the
    /// placeholder (unless it was already replaced) and fails it for any caller that coalesced onto it;
    /// otherwise the load's outcome completes it and finalizes the entry.
    /// </summary>
    private void Settle(K key, PendingLoad load, long startTime, bool recordStats)
    {
        Task<V> pending = load.Task;
        if (load.StartFailure != null)
        {
            try
            {
                if (recordStats)
                {
                    Cache.StatsCounter.RecordLoadFailure(Cache.StatsTicker.Read() - startTime);
                }
                Cache.Remove(key, pending);
            }
            finally
            {
                load.TrySetException(load.StartFailure.SourceException);
                _ = pending.Exception; // observed here; coalesced callers observe their own copy
            }
            return;
        }

        // Link first, so that a source that has already completed completes the placeholder now and its
        // finalization below runs before the caller returns.
        Link(load.Source!, load);
        HandleCompletion(key, pending, startTime, deferred: true, computed: true);
    }

    /// <summary>
    /// The in-flight placeholder of a load. Once the cache publishes it, it starts the mapping function
    /// and keeps the returned task (or the failure to start) for the caller to link.
    /// </summary>
    private sealed class PendingLoad : TaskCompletionSource<V>, IInsertionCallback
    {
        private readonly K _key;
        private readonly Func<K, CancellationToken, Task<V?>> _mappingFunction;

        public PendingLoad(K key, Func<K, CancellationToken, Task<V?>> mappingFunction)
            : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            _key = key;
            _mappingFunction = mappingFunction;
            Install = _ => Task;
        }

        /// <summary>The insertion's mapping function: installs this placeholder.</summary>
        public Func<K, Task<V>?> Install { get; }

        public Task<V?>? Source { get; private set; }

        public ExceptionDispatchInfo? StartFailure { get; private set; }

        public bool Started => (Source != null) || (StartFailure != null);

        public void OnInserted()
        {
            try
            {
                Source = _mappingFunction(_key, CancellationToken.None)
                    ?? throw new ArgumentNullException("mappingFunction", "The mapping function returned a null task");
            }
            catch (Exception e)
            {
                StartFailure = ExceptionDispatchInfo.Capture(e);
            }
        }
    }

    /// <summary>Stores a future, replacing any prior mapping, and attaches the completion handler.</summary>
    public void Put(K key, Task<V> valueFuture)
    {
        ArgumentNullException.ThrowIfNull(valueFuture);
        long startTime = Cache.StatsTicker.Read();
        // Read before the store: a future observed in flight here may complete before the insertion
        // evaluates it, which the completion detects from the entry itself. A future that completed
        // without a value is not ready either: its completion removes the entry and records the failure.
        bool deferred = AsyncValue.GetIfReady(valueFuture) == null;
        Task<V>? prior = Cache.Put(key, valueFuture);
        if (!ReferenceEquals(prior, valueFuture))
        {
            HandleCompletion(key, valueFuture, startTime, deferred, computed: false);
        }
        else if (deferred)
        {
            HandleRewrite(key, valueFuture);
        }
    }

    /// <summary>
    /// Finalizes a future that was written over itself if it completed during the write. The write
    /// weighs the value before it locks the entry, so it may have stored the in-flight weight over the
    /// one that the insertion's completion finalized; a future still in flight is left to that
    /// completion.
    /// </summary>
    private void HandleRewrite(K key, Task<V> valueFuture)
    {
        if (AsyncValue.GetIfReady(valueFuture) == null)
        {
            return;
        }
        try
        {
            Cache.FinalizeAsyncCompletion(key, valueFuture);
        }
        catch
        {
            Cache.Remove(key, valueFuture);
        }
    }

    /// <summary>
    /// Attaches a continuation that finalizes an entry once its future resolves: a failed or
    /// null-completing future removes the entry (only if still mapped to it). A successful future whose
    /// insertion deferred its weight and expiration (it was not ready) is finalized now that the value
    /// is ready; an already-ready insertion was accounted for by the store. A load is recorded only if
    /// the caller computed the value or the accounting was deferred to the completion, so an explicit
    /// write of a ready future records nothing.
    /// </summary>
    private protected void HandleCompletion(K key, Task<V> future, long startTime, bool deferred, bool computed)
    {
        bool recordLoad = computed || deferred;
        if (!deferred)
        {
            // Ready when inserted (deferred is read from the same future), so nothing is left to finalize.
            if (recordLoad)
            {
                Cache.StatsCounter.RecordLoadSuccess(Cache.StatsTicker.Read() - startTime);
            }
            return;
        }
        future.ContinueWith(t =>
        {
            long loadTime = Cache.StatsTicker.Read() - startTime;
            V? value = t.IsCompletedSuccessfully ? t.Result : null;
            if (value == null)
            {
                // Record before removing, so an observer that sees the entry gone also sees the failure.
                if (recordLoad)
                {
                    Cache.StatsCounter.RecordLoadFailure(loadTime);
                }
                Cache.Remove(key, future);
            }
            else
            {
                try
                {
                    // Re-weigh the entry and start its expiry timers now that the value is ready.
                    Cache.FinalizeAsyncCompletion(key, future);
                    if (recordLoad)
                    {
                        Cache.StatsCounter.RecordLoadSuccess(loadTime);
                    }
                }
                catch
                {
                    if (recordLoad)
                    {
                        Cache.StatsCounter.RecordLoadFailure(loadTime);
                    }
                    Cache.Remove(key, future);
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Completes <paramref name="tcs"/> with the outcome of <paramref name="source"/>: a fault or a
    /// cancellation is propagated, and a null result faults it.
    /// </summary>
    private static void Link(Task<V?> source, TaskCompletionSource<V> tcs)
    {
        source.ContinueWith(static (t, state) =>
        {
            var tcs = (TaskCompletionSource<V>)state!;
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
        }, tcs, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
        // Validate and de-duplicate the requested keys before touching the store, so an invalid key or
        // a failing enumeration cannot leave proxies behind.
        var futures = new Dictionary<K, Task<V>>();
        var uniqueKeys = new List<K>();
        foreach (K key in keys)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(keys), "A requested key is null");
            }
            if (futures.TryAdd(key, null!))
            {
                uniqueKeys.Add(key);
            }
        }

        // Snapshot each key's current future. Any key without a future gets a proxy future inserted
        // atomically so concurrent readers coalesce onto it.
        var proxies = new Dictionary<K, TaskCompletionSource<V>>();
        try
        {
            foreach (K key in uniqueKeys)
            {
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
        }
        catch (Exception e)
        {
            // Roll back only this call's own proxies and fault them, so neither a later load nor a
            // reader that coalesced onto one of them waits forever.
            FailProxies(proxies, e);
            throw;
        }

        Cache.StatsCounter.RecordMisses(proxies.Count);
        Cache.StatsCounter.RecordHits(futures.Count - proxies.Count);

        if (proxies.Count == 0)
        {
            return ComposeResult(futures);
        }

        long startTime = Cache.StatsTicker.Read();
        var missing = new List<K>(proxies.Keys);
        Task<IReadOnlyDictionary<K, V>> loaded;
        try
        {
            loaded = mappingFunction(missing, CancellationToken.None)
                ?? throw new InvalidOperationException("The bulk mapping function returned a null task");
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
        long loadTime = Cache.StatsTicker.Read() - startTime;
        // An empty result means no requested key was loaded: booked as a load failure.
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
                try { Cache.FinalizeAsyncCompletion(key, proxy.Task); } // re-weigh + start timers
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
        long loadTime = Cache.StatsTicker.Read() - startTime;
        Cache.StatsCounter.RecordLoadFailure(loadTime);
        FailProxies(proxies, failure);
    }

    /// <summary>
    /// Removes each proxy only if it is still mapped (a concurrent write is preserved) and faults it.
    /// Every proxy is faulted even if removing another one throws.
    /// </summary>
    private void FailProxies(Dictionary<K, TaskCompletionSource<V>> proxies, Exception failure)
    {
        foreach (KeyValuePair<K, TaskCompletionSource<V>> entry in proxies)
        {
            try
            {
                Cache.Remove(entry.Key, entry.Value.Task);
            }
            catch
            {
                // The proxy is still faulted below, so it cannot strand a reader.
            }
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
