using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Stats;

namespace Espresso.Internal;

/// <summary>
/// The loading behavior shared by the bounded and unbounded loading caches: bulk loading (Caffeine's
/// <c>LocalLoadingCache.getAll</c> and <c>LocalManualCache.bulkLoad</c>) and manual refresh (Caffeine's
/// <c>LocalLoadingCache.refresh</c> and <c>RefreshOperation</c>).
/// </summary>
internal static class LocalLoadingCache
{
    /// <summary>
    /// Returns whether the loader overrides <see cref="ICacheLoader{K,V}.LoadAll"/>. The default
    /// implementation loads key by key, so a cache uses its own sequential path instead of calling it.
    /// </summary>
    public static bool HasLoadAll<K, V>(ICacheLoader<K, V> loader)
        where K : notnull
        where V : class
    {
        Type contract = typeof(ICacheLoader<K, V>);
        try
        {
            InterfaceMapping map = loader.GetType().GetInterfaceMap(contract);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name == nameof(ICacheLoader<K, V>.LoadAll))
                {
                    return map.TargetMethods[i].DeclaringType != contract;
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // The mapping is unavailable (e.g. a dynamic proxy): use the sequential path.
        }
        return false;
    }

    /// <summary>
    /// Returns the values for the unique keys, loading the absent ones: in one
    /// <see cref="ICacheLoader{K,V}.LoadAll"/> call when <paramref name="bulk"/>, otherwise one key at
    /// a time. Every key is validated before anything is read or loaded.
    /// </summary>
    public static IReadOnlyDictionary<K, V> GetAll<K, V>(ILoadingCache<K, V> cache, ICacheLoader<K, V> loader,
        bool bulk, StatsCounter statsCounter, Ticker statsTicker, IEnumerable<K> keys)
        where K : notnull
        where V : class
    {
        ArgumentNullException.ThrowIfNull(keys);
        var unique = new List<K>();
        var seen = new HashSet<K>();
        foreach (K key in keys)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (seen.Add(key))
            {
                unique.Add(key);
            }
        }
        return bulk
            ? BulkLoad(cache, loader, statsCounter, statsTicker, unique)
            : LoadSequentially(cache, statsCounter, unique);
    }

    /// <summary>
    /// Loads each key in order. If a load throws, the keys not yet reached are recorded as misses
    /// before the failure propagates.
    /// </summary>
    private static Dictionary<K, V> LoadSequentially<K, V>(ILoadingCache<K, V> cache, StatsCounter statsCounter, List<K> keys)
        where K : notnull
        where V : class
    {
        var result = new Dictionary<K, V>(keys.Count);
        int count = 0;
        try
        {
            foreach (K key in keys)
            {
                count++;
                V? value = cache.Get(key);
                if (value != null)
                {
                    result[key] = value;
                }
            }
        }
        catch
        {
            statsCounter.RecordMisses(keys.Count - count);
            throw;
        }
        return result;
    }

    /// <summary>
    /// Reads the present keys, then loads the absent ones in one call. Every loaded entry is cached,
    /// including keys that were not requested; a requested key the loader omits stays absent.
    /// </summary>
    private static Dictionary<K, V> BulkLoad<K, V>(ILoadingCache<K, V> cache, ICacheLoader<K, V> loader,
        StatsCounter statsCounter, Ticker statsTicker, List<K> keys)
        where K : notnull
        where V : class
    {
        IReadOnlyDictionary<K, V> present = cache.GetAllPresent(keys);
        if (present.Count == keys.Count)
        {
            return new Dictionary<K, V>(present);
        }
        var keysToLoad = new List<K>(keys.Count - present.Count);
        foreach (K key in keys)
        {
            if (!present.ContainsKey(key))
            {
                keysToLoad.Add(key);
            }
        }

        long start = statsTicker.Read();
        bool success = false;
        var result = new Dictionary<K, V>(keys.Count);
        try
        {
            IReadOnlyDictionary<K, V> loaded = loader.LoadAll(keysToLoad.AsReadOnly())
                ?? throw new InvalidOperationException("The loader returned a null map");
            foreach (KeyValuePair<K, V> entry in loaded)
            {
                cache.Put(entry.Key, entry.Value);
            }
            // In the requested order; a requested key the loader omitted stays absent.
            foreach (K key in keys)
            {
                if (present.TryGetValue(key, out V? value) || loaded.TryGetValue(key, out value))
                {
                    result[key] = value;
                }
            }
            success = loaded.Count != 0;
        }
        finally
        {
            long loadTime = statsTicker.Read() - start;
            if (success)
            {
                statsCounter.RecordLoadSuccess(loadTime);
            }
            else
            {
                statsCounter.RecordLoadFailure(loadTime);
            }
        }
        return result;
    }

    /// <summary>
    /// Reloads the key's value as Caffeine's <c>RefreshOperation</c> does. The refresh is registered in
    /// the cache's refresh registry before the loader is called, so the loader runs outside every lock
    /// and a write that races it discards the reload. A refresh already registered for the key is
    /// joined (its task is returned) unless it reloads an entry that is now pending eviction, which a
    /// new refresh replaces. The old value is read quietly; the load runs on the cache's executor, and
    /// its result is committed only if the refresh still owns its registration and the entry still
    /// holds the old value. The returned task completes with the reloaded value (null if the loader
    /// returned none) once the reload was committed or discarded, or faults with the loader's failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The loader of a refresh that is still being submitted refreshes its own key on the same thread.
    /// </exception>
    public static Task<V?> Refresh<K, V>(IRefreshHost<K, V> host, ICacheLoader<K, V> loader, IExecutor executor,
        StatsCounter statsCounter, Ticker statsTicker, K key)
        where K : notnull
        where V : class
    {
        object keyReference = key;
        ConcurrentHashMap<object, object> refreshes = host.Refreshes();
        RefreshRegistration<V>? registration = null;
        V? oldValue = null;
        object registered = refreshes.Compute(keyReference, (_, existing) =>
        {
            registration = null;
            if ((existing != null)
                && ((existing is RefreshRegistration<V> { IsPending: true }) || !host.IsPendingEviction(key)))
            {
                return existing;
            }
            oldValue = host.GetIfPresentQuietly(key);
            registration = new RefreshRegistration<V>();
            return registration;
        })!;
        if (registration == null)
        {
            return ((RefreshRegistration<V>)registered).Coalesce();
        }

        var operation = new RefreshOperation<K, V>(
            host, loader, statsCounter, statsTicker, refreshes, key, keyReference, registration, oldValue);
        try
        {
            executor.Execute(operation.Run);
        }
        catch (Exception e)
        {
            // As a failed submission of Caffeine's asyncLoad/asyncReload, but reported through the task.
            operation.Reject(e);
            return registration.Task;
        }
        registration.Publish();
        if (oldValue == null)
        {
            // Reap a dead entry that this refresh loads anew, so a later refresh joins this one rather
            // than replacing it as a reload of a value that is pending eviction.
            host.EvictIfPendingEviction(key);
        }
        return registration.Task;
    }

    /// <summary>A submitted manual refresh: loads the value, commits it, and settles the registration.</summary>
    private sealed class RefreshOperation<K, V>(
        IRefreshHost<K, V> host, ICacheLoader<K, V> loader, StatsCounter statsCounter, Ticker statsTicker,
        ConcurrentHashMap<object, object> refreshes, K key, object keyReference,
        RefreshRegistration<V> registration, V? oldValue)
        where K : notnull
        where V : class
    {
        private const int Idle = 0;
        private const int Started = 1;
        private const int Rejected = 2;

        private int _state;

        /// <summary>Withdraws the registration if the executor refused the work before running it.</summary>
        public void Reject(Exception error)
        {
            if (Interlocked.CompareExchange(ref _state, Rejected, Idle) == Idle)
            {
                Withdraw();
                registration.TrySetException(error);
            }
        }

        /// <summary>Runs the load and the commit. Nothing escapes onto the executor's thread.</summary>
        public void Run()
        {
            if (Interlocked.CompareExchange(ref _state, Started, Idle) != Idle)
            {
                return;
            }
            try
            {
                long start = statsTicker.Read();
                V? newValue;
                try
                {
                    // ICacheLoader has no reload method, so a present value is reloaded with Load, as an
                    // automatic refresh does.
                    newValue = loader.Load(key);
                }
                catch (Exception e)
                {
                    Withdraw();
                    statsCounter.RecordLoadFailure(statsTicker.Read() - start);
                    registration.TrySetException(e);
                    return;
                }

                long loadTime = statsTicker.Read() - start;
                try
                {
                    host.CommitManualRefresh(key, keyReference, registration, oldValue, newValue);
                    if (newValue == null)
                    {
                        statsCounter.RecordLoadFailure(loadTime);
                    }
                    else
                    {
                        statsCounter.RecordLoadSuccess(loadTime);
                    }
                }
                catch
                {
                    // A user callback (weigher, expiry, key equality) failed the commit. As in Caffeine,
                    // the reload itself succeeded, so the task still completes with its value.
                    Withdraw();
                    statsCounter.RecordLoadFailure(loadTime);
                }
                registration.TrySetResult(newValue);
            }
            catch (Exception e)
            {
                // E.g. a throwing ticker: release the registration and report the failure.
                Withdraw();
                registration.TrySetException(e);
            }
        }

        private void Withdraw()
        {
            try
            {
                refreshes.Remove(keyReference, registration);
            }
            catch
            {
                // Hashing the key can throw; the registration is then left for a write to discard.
            }
        }
    }
}

/// <summary>
/// A refresh's entry in a cache's refresh registry (Caffeine's <c>refreshes()</c> future). Its task
/// completes with the reloaded value once the reload was committed or discarded, or faults with the
/// reload's failure, so a refresh that coalesces with it returns that same task. The registration is
/// pending until its reload has been submitted (Caffeine's <c>PendingRefresh</c>): with an inline
/// executor the whole reload runs while it is pending, so a refresh of the same key on the owner's
/// thread is a loader refreshing its own key.
/// </summary>
internal sealed class RefreshRegistration<V> : TaskCompletionSource<V?>
    where V : class
{
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private volatile bool _published;

    /// <summary>
    /// Creates a pending registration owned by the current thread. Continuations run asynchronously,
    /// so a caller's continuation never runs inside the cache operation that settled the refresh.
    /// </summary>
    public RefreshRegistration()
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
    }

    /// <summary>Whether the reload has not been submitted yet.</summary>
    public bool IsPending => !_published;

    /// <summary>Marks the reload as submitted.</summary>
    public void Publish() => _published = true;

    /// <summary>
    /// Returns the task of the refresh being joined. A pending registration's owner joining it would be
    /// a loader refreshing its own key, which is rejected as in Caffeine's <c>PendingRefresh.resolve</c>.
    /// </summary>
    public Task<V?> Coalesce()
    {
        if (!_published && (_owner == Environment.CurrentManagedThreadId))
        {
            throw new InvalidOperationException("Recursive refresh");
        }
        return Task;
    }
}

/// <summary>The cache operations a manual refresh needs (Caffeine's <c>LocalCache</c> view of it).</summary>
internal interface IRefreshHost<K, V>
    where K : notnull
    where V : class
{
    /// <summary>The refresh registry, created on first use, keyed by the boxed key.</summary>
    ConcurrentHashMap<object, object> Refreshes();

    /// <summary>Whether the key's entry is present but expired or collected, awaiting its removal.</summary>
    bool IsPendingEviction(K key);

    /// <summary>The live value, without recording statistics or a use; an expired entry reads as absent.</summary>
    V? GetIfPresentQuietly(K key);

    /// <summary>
    /// Removes the key's entry if it is pending eviction, notifying its removal, without discarding the
    /// key's refresh registration.
    /// </summary>
    void EvictIfPendingEviction(K key);

    /// <summary>
    /// Under the entry's locks: installs <paramref name="newValue"/> (removes the entry when null) if
    /// <paramref name="registration"/> still owns the key's registration and the entry still holds
    /// <paramref name="oldValue"/> (absent when null); otherwise keeps the entry and reports the discarded
    /// reload as <see cref="RemovalCause.Replaced"/>, or <see cref="RemovalCause.Explicit"/> if the entry
    /// is gone, unless it is the instance the entry holds. An owned registration is released. Throws if
    /// a user callback failed.
    /// </summary>
    void CommitManualRefresh(K key, object keyReference, RefreshRegistration<V> registration, V? oldValue, V? newValue);
}
