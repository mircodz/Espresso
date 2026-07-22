using System.Collections.Generic;

namespace Espresso;

/// <summary>
/// A cache whose values are automatically loaded by a <see cref="ICacheLoader{K,V}"/> and stored
/// until evicted or invalidated.
/// </summary>
/// <typeparam name="K">the key type</typeparam>
/// <typeparam name="V">the value type (reference types only)</typeparam>
public interface ILoadingCache<K, V> : ICache<K, V>
    where K : notnull
    where V : class
{
    /// <summary>
    /// Returns the value for <paramref name="key"/>, loading it via the cache loader if absent. The
    /// load runs atomically and at most once per key; returns <c>null</c> if the loader returned
    /// <c>null</c>.
    /// </summary>
    V? Get(K key);

    /// <summary>
    /// Returns the values for <paramref name="keys"/>, loading any that are absent. The returned map
    /// contains the entries that were cached or successfully loaded.
    /// </summary>
    IReadOnlyDictionary<K, V> GetAll(IEnumerable<K> keys);

    /// <summary>
    /// Reloads the value for <paramref name="key"/> and, on success, replaces the cached value. If
    /// the loader returns <c>null</c> the entry is left unchanged.
    /// </summary>
    void Refresh(K key);
}
