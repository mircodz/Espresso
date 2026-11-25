using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Espresso;

/// <summary>
/// A cache whose values are automatically loaded by a <see cref="ICacheLoader{K,V}"/> and stored
/// until evicted or invalidated.
/// </summary>
/// <remarks>
/// The loader must not modify this cache. A detected recursive update may throw
/// <see cref="InvalidOperationException"/>.
/// </remarks>
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
    /// contains the entries that were cached or successfully loaded. If the loader overrides
    /// <see cref="ICacheLoader{K,V}.LoadAll"/>, the absent keys are loaded in one call and every entry
    /// it returns is cached, including keys that were not requested; otherwise each absent key is
    /// loaded with <see cref="ICacheLoader{K,V}.Load"/>, once per distinct key.
    /// </summary>
    IReadOnlyDictionary<K, V> GetAll(IEnumerable<K> keys);

    /// <summary>
    /// Loads a new value for <paramref name="key"/> asynchronously, on the cache's executor, while the
    /// current value (if any) keeps being served. The value is loaded with
    /// <see cref="ICacheLoader{K,V}.Load"/> whether or not the key is present; an expired entry is
    /// treated as absent and removed as <see cref="RemovalCause.Expired"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the load completes, its value replaces the cached one only if the entry was not modified
    /// in the meantime: a <see cref="ICache{K,V}.Put"/>, an invalidation or another load of the key
    /// while the refresh was in flight wins, and the refreshed value is then discarded and reported to
    /// the removal listener (<see cref="RemovalCause.Replaced"/> if the key is present,
    /// <see cref="RemovalCause.Explicit"/> if it is not). A load that returns <c>null</c> removes the
    /// entry (<see cref="RemovalCause.Explicit"/>). A load that throws leaves the entry unchanged.
    /// </para>
    /// <para>
    /// A refresh of a key that is already being refreshed (manually or by refresh-after-write) returns
    /// the in-flight refresh's task instead of loading again, unless that refresh reloads an entry that
    /// has since expired.
    /// </para>
    /// <para>
    /// Nothing is thrown for a failed load: the returned task faults instead. With an inline executor
    /// (such as <see cref="DirectExecutor"/>) the task is already complete when this method returns.
    /// </para>
    /// </remarks>
    /// <param name="key">the key whose value should be loaded</param>
    /// <returns>
    /// a task that completes with the loaded value, or <c>null</c> if the loader returned none, once
    /// the cache has applied or discarded it; it faults with the exception that the loader threw
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The cache has no loader, or the loader refreshes the key it is loading on the same thread
    /// (an inline executor).
    /// </exception>
    Task<V?> Refresh(K key);
}
