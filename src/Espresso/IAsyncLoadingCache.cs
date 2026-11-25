using System.Collections.Generic;
using System.Threading.Tasks;

namespace Espresso;

/// <summary>
/// An <see cref="IAsyncCache{K,V}"/> whose values are loaded by an <see cref="IAsyncCacheLoader{K,V}"/>.
/// </summary>
/// <typeparam name="K">the key type</typeparam>
/// <typeparam name="V">the value type (reference types only)</typeparam>
public interface IAsyncLoadingCache<K, V> : IAsyncCache<K, V>
    where K : notnull
    where V : class
{
    /// <summary>
    /// Returns the future for <paramref name="key"/>, loading it via the loader if absent. If the load
    /// produces <c>null</c> the future completes with <c>null</c> and the entry is removed.
    /// </summary>
    Task<V?> Get(K key);

    /// <summary>
    /// Returns a future of the values for <paramref name="keys"/>, bulk-loading any absent. A key whose
    /// load produces no value is left out of the result.
    /// </summary>
    Task<IReadOnlyDictionary<K, V>> GetAll(IEnumerable<K> keys);
}
