namespace Espresso;

using System;

/// <summary>
/// Listens for the removal of an entry from a cache, whether removed explicitly, replaced, or
/// evicted. The notification may be delivered on a background thread depending on the cache's
/// executor. The key and value may be <c>null</c> if they were garbage-collected (weak/soft
/// references).
/// </summary>
/// <typeparam name="K">the key type</typeparam>
/// <typeparam name="V">the value type</typeparam>
public interface IRemovalListener<in K, in V>
    where K : notnull
    where V : class
{
    /// <summary>Invoked when an entry has been removed.</summary>
    /// <param name="key">the removed key, or <c>null</c> if collected</param>
    /// <param name="value">the removed value, or <c>null</c> if collected</param>
    /// <param name="cause">why the entry was removed</param>
    void OnRemoval(K? key, V? value, RemovalCause cause);
}

/// <summary>Adapts a callback to <see cref="IRemovalListener{K,V}"/>.</summary>
internal sealed class FuncRemovalListener<K, V>(Action<K?, V?, RemovalCause> onRemoval) : IRemovalListener<K, V>
    where K : notnull
    where V : class
{
    public void OnRemoval(K? key, V? value, RemovalCause cause) => onRemoval(key, value, cause);
}
