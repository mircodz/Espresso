using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Stats;

namespace Espresso;

/// <summary>
/// A cache whose values are loaded asynchronously and stored as futures. An entry whose future has
/// not yet completed is present in the cache but is treated as absent for size and expiration until
/// it completes. A future that faults or completes with <c>null</c> is not cached: the entry is
/// removed automatically, the load is recorded as a failure, and no removal notification is delivered
/// for it. A computation that yields <c>null</c> completes the caller's future with <c>null</c> rather
/// than faulting it.
/// </summary>
/// <typeparam name="K">the key type</typeparam>
/// <typeparam name="V">the value type (reference types only)</typeparam>
public interface IAsyncCache<K, V> : IDisposable
    where K : notnull
    where V : class
{
    /// <summary>
    /// Returns the future for <paramref name="key"/>, or <c>null</c> if not cached. The future may
    /// complete with <c>null</c> if it is a load that produced no value (it is then removed).
    /// </summary>
    Task<V?>? GetIfPresent(K key);

    /// <summary>
    /// Returns the future for <paramref name="key"/>, asynchronously computing it with
    /// <paramref name="mappingFunction"/> on the executor if absent. The function runs at most once per
    /// key, and concurrent callers share its future. If the computation yields <c>null</c> the future
    /// completes with <c>null</c>; if it throws the future faults. Either way the entry is removed.
    /// </summary>
    Task<V?> Get(K key, Func<K, V?> mappingFunction);

    /// <summary>
    /// Returns the future for <paramref name="key"/>, asynchronously computing it with
    /// <paramref name="mappingFunction"/> (which returns a task) if absent. If the task completes with
    /// <c>null</c> the future completes with <c>null</c>; if it faults or is canceled the future does
    /// too. Either way the entry is removed.
    /// </summary>
    Task<V?> Get(K key, Func<K, CancellationToken, Task<V?>> mappingFunction);

    /// <summary>
    /// Returns a future of the values for <paramref name="keys"/>, computing any that are absent with
    /// <paramref name="mappingFunction"/> in a single call. Failed computations remove their entries.
    /// The result never contains a <c>null</c> value: a key whose future completes with <c>null</c>, or
    /// that the computation omits, is left out and not cached.
    /// </summary>
    Task<IReadOnlyDictionary<K, V>> GetAll(
        IEnumerable<K> keys,
        Func<IReadOnlyCollection<K>, CancellationToken, Task<IReadOnlyDictionary<K, V>>> mappingFunction);

    /// <summary>
    /// Associates the future with <paramref name="key"/>, replacing any prior mapping. A future that
    /// faults or completes with <c>null</c> is removed once it does, and recorded as a load failure.
    /// The parameter is declared <c>Task&lt;V&gt;</c> so that the common non-null future passes without a
    /// nullability warning (<see cref="Task{TResult}"/> is invariant); a <c>Task&lt;V?&gt;</c> is accepted
    /// at runtime and may be passed with the null-forgiving operator.
    /// </summary>
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
