using System.Collections.Generic;

namespace Espresso;

/// <summary>
/// Computes or retrieves values, based on a key, for use in populating an <see cref="ILoadingCache{K,V}"/>.
/// Most implementations only need <see cref="Load"/>; <see cref="LoadAll"/> may be overridden when
/// bulk retrieval is significantly more efficient than many individual lookups.
/// <para>
/// <b>Warning:</b> loading must not attempt to update any mappings of the cache directly.
/// </para>
/// </summary>
/// <typeparam name="K">the key type</typeparam>
/// <typeparam name="V">the value type</typeparam>
public interface ICacheLoader<K, V>
    where K : notnull
    where V : class
{
    /// <summary>
    /// Computes or retrieves the value for <paramref name="key"/>, or <c>null</c> if not found (the
    /// mapping is then left unestablished).
    /// </summary>
    V? Load(K key);

    /// <summary>
    /// Computes or retrieves the values for <paramref name="keys"/>. Entries the returned map does
    /// not contain are simply not cached. The default implementation defers to per-key
    /// <see cref="Load"/>; override when bulk loading is more efficient.
    /// </summary>
    IReadOnlyDictionary<K, V> LoadAll(IEnumerable<K> keys)
    {
        var result = new Dictionary<K, V>();
        foreach (K key in keys)
        {
            V? value = Load(key);
            if (value != null)
            {
                result[key] = value;
            }
        }
        return result;
    }
}

/// <summary>Adapts a delegate to <see cref="ICacheLoader{K,V}"/>.</summary>
internal sealed class FuncCacheLoader<K, V> : ICacheLoader<K, V>
    where K : notnull
    where V : class
{
    private readonly System.Func<K, V?> _load;

    /// <summary>Creates a loader that computes a value for a key with <paramref name="load"/>.</summary>
    public FuncCacheLoader(System.Func<K, V?> load) => _load = load;

    /// <inheritdoc/>
    public V? Load(K key) => _load(key);
}
