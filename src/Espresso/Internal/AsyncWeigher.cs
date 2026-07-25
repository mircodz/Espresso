using System.Threading.Tasks;

namespace Espresso.Internal;

/// <summary>
/// Wraps a user weigher for an async cache whose stored values are <see cref="Task{V}"/>. An in-flight
/// future weighs <c>0</c> (so it is pinned, not evicted, while loading); once the value is ready the
/// entry is re-weighed via a completion-triggered replace.
/// </summary>
internal sealed class AsyncWeigher<K, V> : IWeigher<K, Task<V>>
    where K : notnull
    where V : class
{
    private readonly IWeigher<K, V> _delegate;

    public AsyncWeigher(IWeigher<K, V> weigher) => _delegate = weigher;

    public int Weigh(K key, Task<V> future)
    {
        V? value = AsyncValue.GetIfReady(future);
        return value == null ? 0 : _delegate.Weigh(key, value);
    }
}
