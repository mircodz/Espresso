using System;

namespace Espresso;

/// <summary>
/// Calculates the weight of a cache entry. The total weight of entries a cache may hold is bounded by
/// <c>Espresso&lt;K,V&gt;.MaximumWeight</c>. Weights are measured when entries are inserted or updated
/// and must be non-negative; a weight of zero pins the entry (it is skipped during size eviction).
/// </summary>
/// <typeparam name="K">the key type</typeparam>
/// <typeparam name="V">the value type</typeparam>
public interface IWeigher<in K, in V>
    where K : notnull
    where V : class
{
    /// <summary>Returns the weight of the entry; must be non-negative.</summary>
    int Weigh(K key, V value);
}

/// <summary>An <see cref="IWeigher{K,V}"/> that assigns every entry a weight of one.</summary>
public sealed class SingletonWeigher<K, V> : IWeigher<K, V>
    where K : notnull
    where V : class
{
    /// <summary>The shared singleton instance.</summary>
    public static readonly SingletonWeigher<K, V> Instance = new();

    private SingletonWeigher() { }

    /// <inheritdoc/>
    public int Weigh(K key, V value) => 1;
}

/// <summary>Adapts a delegate to <see cref="IWeigher{K,V}"/>.</summary>
internal sealed class FuncWeigher<K, V>(Func<K, V, int> weigh): IWeigher<K, V>
    where K : notnull
    where V : class
{
    /// <inheritdoc/>
    public int Weigh(K key, V value) => weigh(key, value);
}
