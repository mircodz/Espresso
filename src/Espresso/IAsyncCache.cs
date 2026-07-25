using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Stats;

namespace Espresso;

/// <summary>
/// A cache whose values are loaded asynchronously and stored as futures. An entry whose future has
/// not yet completed is present in the cache but is treated as absent for size and expiration until
/// it completes; a future that completes with <c>null</c> or faults removes the entry automatically.
/// </summary>
/// <typeparam name="K">the key type</typeparam>
/// <typeparam name="V">the value type (reference types only)</typeparam>
public interface IAsyncCache<K, V> : IDisposable
    where K : notnull
    where V : class
{
    /// <summary>Returns the future for <paramref name="key"/>, or <c>null</c> if not cached.</summary>
    Task<V>? GetIfPresent(K key);

    /// <summary>
    /// Returns the future for <paramref name="key"/>, asynchronously computing it with
    /// <paramref name="mappingFunction"/> if absent. The function runs at most once per key; if the
    /// computation yields <c>null</c> or fails the entry is removed.
    /// </summary>
    Task<V> Get(K key, Func<K, V?> mappingFunction);

    /// <summary>
    /// Returns the future for <paramref name="key"/>, asynchronously computing it with
    /// <paramref name="mappingFunction"/> (which returns a task) if absent.
    /// </summary>
    Task<V> Get(K key, Func<K, CancellationToken, Task<V?>> mappingFunction);

    /// <summary>
    /// Returns a future of the values for <paramref name="keys"/>, computing any that are absent with
    /// <paramref name="mappingFunction"/> in a single call. Failed computations remove their entries.
    /// </summary>
    Task<IReadOnlyDictionary<K, V>> GetAll(
        IEnumerable<K> keys,
        Func<IReadOnlyCollection<K>, CancellationToken, Task<IReadOnlyDictionary<K, V>>> mappingFunction);

    /// <summary>Associates the future with <paramref name="key"/>, replacing any prior mapping.</summary>
    void Put(K key, Task<V> valueFuture);

    /// <summary>Discards any cached future for <paramref name="key"/>.</summary>
    void Invalidate(K key);

    /// <summary>Discards cached futures for the given keys.</summary>
    void InvalidateAll(IEnumerable<K> keys);

    /// <summary>Discards all entries.</summary>
    void InvalidateAll();

    /// <summary>The approximate number of entries (including in-flight futures).</summary>
    long EstimatedSize();

    /// <summary>A snapshot of the cache statistics.</summary>
    CacheStats Stats();

    /// <summary>Performs any pending maintenance.</summary>
    void CleanUp();
}
