using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Stats;

namespace Espresso.Internal;

/// <summary>
/// A cache with no bounding of the map — a lightweight wrapper over <see cref="ConcurrentHashMap{TKey,TValue}"/>.
/// It backs both the manual and loading surfaces; <see cref="_loader"/> is null for a manual cache.
/// </summary>
internal sealed class UnboundedLocalCache<K, V> : ILoadingCache<K, V>, IRefreshHost<K, V>
    where K : notnull
    where V : class
{
    private readonly ConcurrentHashMap<K, V> _data;
    private readonly StatsCounter _statsCounter;
    private readonly IRemovalListener<K, V>? _removalListener;
    private readonly IExecutor _executor;
    private readonly Ticker _ticker;
    private readonly ICacheLoader<K, V>? _loader;
    private readonly bool _hasLoadAll;

    // In-flight manual refreshes (RefreshRegistration<V>), keyed by the boxed key; created by the first
    // refresh. Once it exists, every write discards the key's registration under the key's bin lock, so
    // a reload never overwrites the write (Caffeine's discardRefresh).
    private ConcurrentHashMap<object, object>? _refreshes;

    internal UnboundedLocalCache(in CacheConfiguration<K, V> config, ICacheLoader<K, V>? loader)
    {
        _data = new ConcurrentHashMap<K, V>(config.InitialCapacity);
        _statsCounter = config.StatsCounter;
        _removalListener = config.RemovalListener;
        _executor = config.Executor;
        _ticker = config.StatsTicker; // used only to time loads
        _loader = loader;
        _hasLoadAll = loader != null && LocalLoadingCache.HasLoadAll(loader);
    }

    // ----- Cache -----

    public V? GetIfPresent(K key)
    {
        ThrowIfKeyNull(key);
        V? value = _data.GetOrDefault(key);
        if (value == null)
        {
            _statsCounter.RecordMisses(1);
        }
        else
        {
            _statsCounter.RecordHits(1);
        }
        return value;
    }

    public V? Get(K key, Func<K, V?> mappingFunction)
    {
        ThrowIfKeyNull(key);
        ArgumentNullException.ThrowIfNull(mappingFunction);

        // A present value is a hit and never runs the function.
        V? current = _data.GetOrDefault(key);
        if (current != null)
        {
            _statsCounter.RecordHits(1);
            return current;
        }

        // Miss: compute under the bin lock so the function runs atomically, at most once. The miss is
        // recorded before loading and a throwing load is recorded as a failure.
        bool[] loaded = { false };
        long startTime = 0;
        V? result = _data.ComputeIfAbsent(key, k =>
        {
            loaded[0] = true;
            _statsCounter.RecordMisses(1);
            startTime = _ticker.Read();
            V? computed;
            try
            {
                computed = mappingFunction(k);
            }
            catch
            {
                _statsCounter.RecordLoadFailure(_ticker.Read() - startTime);
                throw;
            }
            Volatile.Read(ref _refreshes)?.Remove(k);
            return computed;
        });

        if (loaded[0])
        {
            long elapsed = _ticker.Read() - startTime;
            if (result != null)
            {
                _statsCounter.RecordLoadSuccess(elapsed);
            }
            else
            {
                _statsCounter.RecordLoadFailure(elapsed);
            }
        }
        else
        {
            // Another thread inserted between our read and the compute; count as a hit.
            _statsCounter.RecordHits(1);
        }
        return result;
    }

    public IReadOnlyDictionary<K, V> GetAllPresent(IEnumerable<K> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        // Validate and de-duplicate first: the statistics count each distinct key once.
        var unique = new List<K>();
        var seen = new HashSet<K>();
        foreach (K key in keys)
        {
            ThrowIfKeyNull(key);
            if (seen.Add(key))
            {
                unique.Add(key);
            }
        }
        var result = new Dictionary<K, V>(unique.Count);
        foreach (K key in unique)
        {
            V? value = _data.GetOrDefault(key);
            if (value != null)
            {
                result[key] = value;
            }
        }
        _statsCounter.RecordHits(result.Count);
        _statsCounter.RecordMisses(unique.Count - result.Count);
        return result;
    }

    public void Put(K key, V value)
    {
        ThrowIfKeyNull(key);
        ArgumentNullException.ThrowIfNull(value);
        V? previous;
        ConcurrentHashMap<object, object>? refreshes = Volatile.Read(ref _refreshes);
        if (refreshes == null)
        {
            previous = _data.Put(key, value);
        }
        else
        {
            V? prior = null;
            _data.Compute(key, (k, current) =>
            {
                refreshes.Remove(k);
                prior = current;
                return value;
            });
            previous = prior;
        }
        if (previous != null && !ReferenceEquals(previous, value))
        {
            NotifyRemoval(key, previous, RemovalCause.Replaced);
        }
    }

    public void PutAll(IReadOnlyDictionary<K, V> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        foreach (KeyValuePair<K, V> entry in map)
        {
            Put(entry.Key, entry.Value);
        }
    }

    public void Invalidate(K key)
    {
        ThrowIfKeyNull(key);
        V? removed;
        ConcurrentHashMap<object, object>? refreshes = Volatile.Read(ref _refreshes);
        if (refreshes == null)
        {
            removed = _data.Remove(key);
        }
        else
        {
            V? prior = null;
            _data.Compute(key, (k, current) =>
            {
                refreshes.Remove(k);
                prior = current;
                return null;
            });
            removed = prior;
        }
        if (removed != null)
        {
            NotifyRemoval(key, removed, RemovalCause.Explicit);
        }
    }

    public void InvalidateAll(IEnumerable<K> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (K key in keys)
        {
            Invalidate(key);
        }
    }

    public void InvalidateAll()
    {
        // Discard every in-flight refresh, including those loading an absent key, as Caffeine's clear.
        Volatile.Read(ref _refreshes)?.Clear();
        if (_removalListener == null)
        {
            _data.Clear();
            return;
        }
        // Notify per entry, mirroring explicit removal semantics.
        foreach (KeyValuePair<K, V> entry in Snapshot())
        {
            Invalidate(entry.Key);
        }
    }

    public long EstimatedSize() => _data.Count;

    public CacheStats Stats() => _statsCounter.Snapshot();

    public void CleanUp()
    {
        // Nothing is pending in an unbounded cache.
    }

    /// <summary>No-op: an unbounded cache holds no background resources.</summary>
    public void Dispose()
    {
    }

    // ----- LoadingCache -----

    public V? Get(K key)
    {
        ThrowIfKeyNull(key);
        RequireLoader();
        return Get(key, k => _loader!.Load(k));
    }

    public IReadOnlyDictionary<K, V> GetAll(IEnumerable<K> keys)
    {
        RequireLoader();
        return LocalLoadingCache.GetAll(this, _loader!, _hasLoadAll, _statsCounter, _ticker, keys);
    }

    public Task<V?> Refresh(K key)
    {
        ThrowIfKeyNull(key);
        RequireLoader();
        return LocalLoadingCache.Refresh(this, _loader!, _executor, _statsCounter, _ticker, key);
    }

    // ----- manual refresh (IRefreshHost) -----

    ConcurrentHashMap<object, object> IRefreshHost<K, V>.Refreshes()
    {
        ConcurrentHashMap<object, object>? refreshes = Volatile.Read(ref _refreshes);
        if (refreshes == null)
        {
            Interlocked.CompareExchange(ref _refreshes, new ConcurrentHashMap<object, object>(), null);
            refreshes = Volatile.Read(ref _refreshes)!;
        }
        return refreshes;
    }

    /// <summary>An unbounded cache neither expires nor collects entries.</summary>
    bool IRefreshHost<K, V>.IsPendingEviction(K key) => false;

    V? IRefreshHost<K, V>.GetIfPresentQuietly(K key) => _data.GetOrDefault(key);

    void IRefreshHost<K, V>.EvictIfPendingEviction(K key)
    {
    }

    /// <summary>
    /// Commits a manual refresh as Caffeine's <c>RefreshOperation.commit</c> does: installs the reload
    /// (a null reload removes the entry) if the refresh still owns its registration and the key still
    /// maps to the value the refresh read; otherwise keeps the mapping and reports the discarded reload.
    /// </summary>
    void IRefreshHost<K, V>.CommitManualRefresh(
        K key, object keyReference, RefreshRegistration<V> registration, V? oldValue, V? newValue)
    {
        ConcurrentHashMap<object, object> refreshes = Volatile.Read(ref _refreshes)!;
        V? replaced = null;
        bool discard = false;
        V? mapped = _data.Compute(key, (_, current) =>
        {
            bool owned = ReferenceEquals(refreshes.GetOrDefault(keyReference), registration);
            if (owned)
            {
                refreshes.Remove(keyReference, registration);
            }
            if (owned && ReferenceEquals(current, oldValue))
            {
                replaced = current;
                return newValue;
            }
            discard = !ReferenceEquals(current, newValue);
            return current;
        });

        if (replaced != null && !ReferenceEquals(replaced, newValue))
        {
            NotifyRemoval(key, replaced, (mapped == null) ? RemovalCause.Explicit : RemovalCause.Replaced);
        }
        if (discard && (newValue != null))
        {
            NotifyRemoval(key, newValue, (mapped == null) ? RemovalCause.Explicit : RemovalCause.Replaced);
        }
    }

    // ----- helpers -----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowIfKeyNull(K key, [CallerArgumentExpression(nameof(key))] string? param = null)
    {
        if (default(K) is null && key is null)
        {
            throw new ArgumentNullException(param);
        }
    }

    private void RequireLoader()
    {
        if (_loader == null)
        {
            throw new InvalidOperationException("this cache was built without a loader");
        }
    }

    private IEnumerable<KeyValuePair<K, V>> Snapshot()
    {
        var list = new List<KeyValuePair<K, V>>();
        var e = _data.GetEnumerator();
        while (e.MoveNext())
        {
            list.Add(e.Current);
        }
        return list;
    }

    private void NotifyRemoval(K? key, V? value, RemovalCause cause)
    {
        IRemovalListener<K, V>? listener = _removalListener;
        if (listener == null)
        {
            return;
        }
        NotifyRemovalViaExecutor(listener, key, value, cause);
    }

    private void NotifyRemovalViaExecutor(IRemovalListener<K, V> listener, K? key, V? value, RemovalCause cause)
    {
        void Run()
        {
            try { listener.OnRemoval(key, value, cause); }
            catch { /* a misbehaving listener must not disrupt the cache */ }
        }
        try
        {
            _executor.Execute(Run);
        }
        catch
        {
            Run(); // executor rejected the task; run inline so the notification is not lost
        }
    }
}
