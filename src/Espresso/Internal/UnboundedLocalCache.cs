using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Espresso.Stats;

namespace Espresso.Internal;

/// <summary>
/// A cache with no bounding of the map — a lightweight wrapper over <see cref="ConcurrentHashMap{TKey,TValue}"/>.
/// It backs both the manual and loading surfaces; <see cref="_loader"/> is null for a manual cache.
/// </summary>
internal sealed class UnboundedLocalCache<K, V> : ILoadingCache<K, V>
    where K : notnull
    where V : class
{
    private readonly ConcurrentHashMap<K, V> _data;
    private readonly StatsCounter _statsCounter;
    private readonly IRemovalListener<K, V>? _removalListener;
    private readonly IExecutor _executor;
    private readonly Ticker _ticker;
    private readonly ICacheLoader<K, V>? _loader;

    internal UnboundedLocalCache(in CacheConfiguration<K, V> config, ICacheLoader<K, V>? loader)
    {
        _data = new ConcurrentHashMap<K, V>(config.InitialCapacity);
        _statsCounter = config.StatsCounter;
        _removalListener = config.RemovalListener;
        _executor = config.Executor;
        _ticker = config.StatsTicker; // used only to time loads
        _loader = loader;
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
        // recorded before loading and a throwing load is recorded as a failure (Caffeine's statsAware).
        bool[] loaded = { false };
        long startTime = 0;
        V? result = _data.ComputeIfAbsent(key, k =>
        {
            loaded[0] = true;
            _statsCounter.RecordMisses(1);
            startTime = _ticker.Read();
            try
            {
                return mappingFunction(k);
            }
            catch
            {
                _statsCounter.RecordLoadFailure(_ticker.Read() - startTime);
                throw;
            }
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
        var result = new Dictionary<K, V>();
        int hits = 0;
        int misses = 0;
        foreach (K key in keys)
        {
            ThrowIfKeyNull(key);
            V? value = _data.GetOrDefault(key);
            if (value == null)
            {
                misses++;
            }
            else
            {
                hits++;
                result[key] = value;
            }
        }
        _statsCounter.RecordHits(hits);
        _statsCounter.RecordMisses(misses);
        return result;
    }

    public void Put(K key, V value)
    {
        ThrowIfKeyNull(key);
        ArgumentNullException.ThrowIfNull(value);
        V? previous = _data.Put(key, value);
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
        V? removed = _data.Remove(key);
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
        ArgumentNullException.ThrowIfNull(keys);
        var result = new Dictionary<K, V>();
        foreach (K key in keys)
        {
            ThrowIfKeyNull(key);
            if (result.ContainsKey(key))
            {
                continue;
            }
            V? value = Get(key);
            if (value != null)
            {
                result[key] = value;
            }
        }
        return result;
    }

    public void Refresh(K key)
    {
        ThrowIfKeyNull(key);
        RequireLoader();
        long startTime = _ticker.Read();
        V? loaded;
        try
        {
            loaded = _loader!.Load(key);
        }
        catch
        {
            _statsCounter.RecordLoadFailure(_ticker.Read() - startTime);
            throw;
        }

        long elapsed = _ticker.Read() - startTime;
        if (loaded == null)
        {
            _statsCounter.RecordLoadFailure(elapsed);
            return; // leave the existing mapping unchanged
        }

        _statsCounter.RecordLoadSuccess(elapsed);
        V? previous = _data.Put(key, loaded);
        if (previous != null && !ReferenceEquals(previous, loaded))
        {
            NotifyRemoval(key, previous, RemovalCause.Replaced);
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
