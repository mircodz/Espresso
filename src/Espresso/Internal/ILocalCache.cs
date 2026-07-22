using System;
using System.Collections.Generic;
using Espresso.Stats;

namespace Espresso.Internal;

/// <summary>
/// The internal contract shared by <see cref="UnboundedLocalCache{K,V}"/> and
/// <see cref="BoundedLocalCache{K,V}"/>. The public <see cref="ICache{K,V}"/>/<see cref="ILoadingCache{K,V}"/>
/// surface delegates to an implementation of this interface.
/// </summary>
internal interface ILocalCache<K, V> : IDisposable
    where K : notnull
    where V : class
{
    // ----- reads -----

    /// <summary>Returns the value for the key, optionally recording statistics and policy access.</summary>
    V? GetIfPresent(K key, bool recordStats);

    /// <summary>Returns the value without recording statistics or updating the eviction policy.</summary>
    V? GetIfPresentQuietly(K key);

    IReadOnlyDictionary<K, V> GetAllPresent(IEnumerable<K> keys);

    // ----- compute / mutate -----

    /// <summary>
    /// Computes a value if absent, running the function atomically and at most once. Records a miss
    /// when the function runs; <paramref name="recordLoad"/> additionally records load success/failure
    /// timing (the async layer passes <c>false</c> so only its completion handler books the load).
    /// </summary>
    V? ComputeIfAbsent(K key, Func<K, V?> mappingFunction, bool recordStats, bool recordLoad = true);

    /// <summary>Unconditional put; returns the previous value or null.</summary>
    V? Put(K key, V value);

    /// <summary>Put only if absent; returns the existing value, or null if inserted.</summary>
    V? PutIfAbsent(K key, V value);

    /// <summary>Removes the key; returns the removed value or null.</summary>
    V? Remove(K key);

    /// <summary>Removes the key only if it currently maps to <paramref name="value"/>.</summary>
    bool Remove(K key, V value);

    /// <summary>
    /// Replaces the value only if the key currently maps to <paramref name="oldValue"/>. Returns
    /// whether the replacement was made. Used by the async completion path to refresh the weight and
    /// expiry timestamps once an in-flight future resolves (replacing the future with itself).
    /// </summary>
    bool Replace(K key, V oldValue, V newValue);

    void Clear();

    // ----- size / maintenance / stats -----

    long EstimatedSize { get; }
    void CleanUp();
    CacheStats StatsSnapshot();

    // ----- collaborators -----

    IStatsCounter StatsCounter { get; }
    IExecutor Executor { get; }
    ITicker Ticker { get; }
    bool IsRecordingStats { get; }
}
