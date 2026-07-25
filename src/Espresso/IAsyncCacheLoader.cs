using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Espresso;

/// <summary>
/// Asynchronously computes or retrieves values for an <see cref="IAsyncLoadingCache{K,V}"/>. Most
/// implementations only need <see cref="AsyncLoad"/>.
/// </summary>
public interface IAsyncCacheLoader<K, V>
    where K : notnull
    where V : class
{
    /// <summary>
    /// Asynchronously computes or retrieves the value for <paramref name="key"/>. The returned task
    /// should complete with <c>null</c> (or fault) if there is no value; the entry is then not cached.
    /// </summary>
    Task<V?> AsyncLoad(K key, CancellationToken cancellationToken);

    /// <summary>
    /// Asynchronously computes or retrieves the values for <paramref name="keys"/>. The default
    /// implementation fans out to <see cref="AsyncLoad"/> per key.
    /// </summary>
    async Task<IReadOnlyDictionary<K, V>> AsyncLoadAll(
        IReadOnlyCollection<K> keys, CancellationToken cancellationToken)
    {
        var result = new Dictionary<K, V>(keys.Count);
        foreach (K key in keys)
        {
            V? value = await AsyncLoad(key, cancellationToken).ConfigureAwait(false);
            if (value != null)
            {
                result[key] = value;
            }
        }
        return result;
    }

    /// <summary>
    /// Asynchronously reloads the value for <paramref name="key"/> given its current value. The
    /// default implementation delegates to <see cref="AsyncLoad"/>.
    /// </summary>
    Task<V?> AsyncReload(K key, V oldValue, CancellationToken cancellationToken)
        => AsyncLoad(key, cancellationToken);
}

/// <summary>Adapts a delegate to <see cref="IAsyncCacheLoader{K,V}"/>.</summary>
internal sealed class FuncAsyncCacheLoader<K, V> : IAsyncCacheLoader<K, V>
    where K : notnull
    where V : class
{
    private readonly System.Func<K, CancellationToken, Task<V?>> _load;

    /// <summary>Creates an async loader that computes a value for a key with <paramref name="load"/>.</summary>
    public FuncAsyncCacheLoader(System.Func<K, CancellationToken, Task<V?>> load) => _load = load;

    /// <inheritdoc/>
    public Task<V?> AsyncLoad(K key, CancellationToken cancellationToken) => _load(key, cancellationToken);
}
