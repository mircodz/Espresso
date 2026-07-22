using System;
using System.Collections.Generic;
using Espresso.Stats;

namespace Espresso;

/// <summary>
/// A semi-persistent mapping from keys to values. Entries are added manually via
/// <see cref="Get(K, Func{K, V})"/> or <see cref="Put"/> and remain until evicted or invalidated.
/// Implementations are thread-safe.
/// <para>
/// Disposing the cache releases any background resources (e.g. a pending maintenance timer from a
/// configured <see cref="IScheduler"/>). A cache with the default configuration holds no unmanaged
/// or timer resources, so disposal is optional there; it is only required when a scheduler is set.
/// </para>
/// </summary>
/// <typeparam name="K">the key type</typeparam>
/// <typeparam name="V">the value type (reference types only)</typeparam>
public interface ICache<K, V> : IDisposable
    where K : notnull
    where V : class
{
    /// <summary>Returns the value for <paramref name="key"/>, or <c>null</c> if not cached.</summary>
    V? GetIfPresent(K key);

    /// <summary>
    /// Returns the value for <paramref name="key"/>, computing it with <paramref name="mappingFunction"/>
    /// if absent. The function runs atomically and at most once per key; if it returns <c>null</c>
    /// nothing is cached and <c>null</c> is returned.
    /// </summary>
    V? Get(K key, Func<K, V?> mappingFunction);

    /// <summary>Returns the cached values for the given keys that are currently present.</summary>
    IReadOnlyDictionary<K, V> GetAllPresent(IEnumerable<K> keys);

    /// <summary>Associates <paramref name="value"/> with <paramref name="key"/>, replacing any prior value.</summary>
    void Put(K key, V value);

    /// <summary>Copies all mappings from <paramref name="map"/> into the cache.</summary>
    void PutAll(IReadOnlyDictionary<K, V> map);

    /// <summary>Discards any cached value for <paramref name="key"/>.</summary>
    void Invalidate(K key);

    /// <summary>Discards any cached values for the given keys.</summary>
    void InvalidateAll(IEnumerable<K> keys);

    /// <summary>Discards all entries.</summary>
    void InvalidateAll();

    /// <summary>Returns the approximate number of entries.</summary>
    long EstimatedSize();

    /// <summary>Returns a snapshot of this cache's cumulative statistics.</summary>
    CacheStats Stats();

    /// <summary>Performs any pending maintenance. Implementation-dependent; may be a no-op.</summary>
    void CleanUp();
}
