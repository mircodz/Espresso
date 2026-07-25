using System.Threading.Tasks;

namespace Espresso.Internal;

/// <summary>
/// Wraps a user removal listener for an async cache. The stored value is a <see cref="Task{V}"/>; the
/// listener fires with the unwrapped value only if the future completed successfully with a non-null
/// value. Delivery is scheduled on the executor with an inline fallback if the executor rejects it.
/// </summary>
internal sealed class AsyncRemovalListener<K, V> : IRemovalListener<K, Task<V>>
    where K : notnull
    where V : class
{
    private readonly IRemovalListener<K, V> _delegate;
    private readonly IExecutor _executor;

    public AsyncRemovalListener(IRemovalListener<K, V> listener, IExecutor executor)
    {
        _delegate = listener;
        _executor = executor;
    }

    public void OnRemoval(K? key, Task<V>? future, RemovalCause cause)
    {
        if (future == null)
        {
            return;
        }
        // Deliver once the future resolves, forwarding only a successful non-null value.
        future.ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || t.Result == null)
            {
                return;
            }
            V value = t.Result;
            void Run()
            {
                try { _delegate.OnRemoval(key, value, cause); }
                catch { /* a misbehaving listener must not disrupt the cache */ }
            }
            try { _executor.Execute(Run); }
            catch { Run(); }
        }, TaskContinuationOptions.ExecuteSynchronously);
    }
}

/// <summary>
/// Wraps a user eviction listener for an async cache. Fires synchronously with the unwrapped value,
/// but only if the future has already completed successfully with a non-null value (an in-flight
/// future is never eligible for eviction).
/// </summary>
internal sealed class AsyncEvictionListener<K, V> : IRemovalListener<K, Task<V>>
    where K : notnull
    where V : class
{
    private readonly IRemovalListener<K, V> _delegate;

    public AsyncEvictionListener(IRemovalListener<K, V> listener) => _delegate = listener;

    public void OnRemoval(K? key, Task<V>? future, RemovalCause cause)
    {
        V? value = AsyncValue.GetIfReady(future);
        if (value != null)
        {
            _delegate.OnRemoval(key, value, cause);
        }
    }
}
