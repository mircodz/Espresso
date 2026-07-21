using System;

namespace Espresso.Internal;

/// <summary>
/// A multiple-producer / single-consumer buffer that rejects new elements if it is full or fails
/// spuriously due to contention. Unlike a queue or stack it makes no ordering guarantee. It is the
/// caller's responsibility to ensure a consumer has exclusive read access; there is no fail-fast
/// guard against incorrect consumer usage.
/// </summary>
internal interface IBuffer<E> where E : class
{
    /// <summary>Inserts the element if possible without violating capacity. May fail spuriously.</summary>
    /// <returns><see cref="BufferResult.Success"/>, <see cref="BufferResult.Failed"/>, or <see cref="BufferResult.Full"/>.</returns>
    int Offer(E e);

    /// <summary>Drains the buffer, sending each element to the consumer. Requires exclusive read access.</summary>
    void DrainTo(Action<E> consumer);

    /// <summary>The number of elements residing in the buffer.</summary>
    long Size => Writes - Reads;

    /// <summary>The number of elements that have been read from the buffer.</summary>
    long Reads { get; }

    /// <summary>The number of elements that have been written to the buffer.</summary>
    long Writes { get; }
}

/// <summary>Result codes and factory helpers for <see cref="IBuffer{E}"/>.</summary>
internal static class BufferResult
{
    public const int Full = 1;    // the buffer is full
    public const int Failed = -1; // the CAS failed
    public const int Success = 0; // added

    /// <summary>Returns a no-op buffer that accepts and discards everything.</summary>
    public static IBuffer<E> Disabled<E>() where E : class => DisabledBuffer<E>.Instance;
}

/// <summary>A buffer that accepts every element and stores nothing.</summary>
internal sealed class DisabledBuffer<E> : IBuffer<E> where E : class
{
    public static readonly DisabledBuffer<E> Instance = new();
    private DisabledBuffer() { }

    public int Offer(E e) => BufferResult.Success;
    public void DrainTo(Action<E> consumer) { }
    public long Size => 0;
    public long Reads => 0;
    public long Writes => 0;
}
