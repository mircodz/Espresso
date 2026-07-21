using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Espresso.Internal;

/// <summary>
/// An MPSC (multiple-producer, single-consumer) array queue which starts at
/// <c>initialCapacity</c> and grows to <c>maxCapacity</c> in linked chunks of the initial size. The
/// queue grows only when the current buffer is full; elements are not copied on resize — instead a
/// link to the new buffer is stored in the old buffer for the consumer to follow.
/// <para>
/// Adapted from JCTools' growable MPSC queue. Producer/consumer index fields are separated by
/// cache-line padding to avoid false sharing. Elements are reference types; the buffer arrays also
/// hold an internal jump sentinel and next-buffer links.
/// </para>
/// </summary>
internal sealed class MpscGrowableArrayQueue<E> where E : class
{
    /// <summary>Sentinel stored in a slot to tell the consumer to follow the link to the next buffer.</summary>
    private static readonly object Jump = new();

    // --- padding (false-sharing isolation) ---
#pragma warning disable CS0169, IDE0051
    private long _p00, _p01, _p02, _p03, _p04, _p05, _p06, _p07;
#pragma warning restore CS0169, IDE0051

    private long _producerIndex;      // volatile via Volatile / Interlocked
    private long _producerLimit;      // volatile
    private long _producerMask;
    private object?[] _producerBuffer;

#pragma warning disable CS0169, IDE0051
    private long _q00, _q01, _q02, _q03, _q04, _q05, _q06, _q07;
#pragma warning restore CS0169, IDE0051

    private long _consumerIndex;      // volatile via Volatile
    private long _consumerMask;
    private object?[] _consumerBuffer;

    private readonly long _maxQueueCapacity;

#pragma warning disable CS0169, IDE0051
    private long _r00, _r01, _r02, _r03, _r04, _r05, _r06, _r07;
#pragma warning restore CS0169, IDE0051

    public MpscGrowableArrayQueue(int initialCapacity, int maxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 4);
        if (Common.CeilingPowerOfTwo(maxCapacity) < Common.CeilingPowerOfTwo(initialCapacity))
        {
            throw new ArgumentException(
                "Initial capacity cannot exceed maximum capacity (both rounded up to a power of 2)");
        }

        int p2capacity = Common.CeilingPowerOfTwo(initialCapacity);
        // Leave the lower bit of the mask clear (it is used as a resize flag on the index).
        long mask = (p2capacity - 1L) << 1;
        // Need an extra element to point at the next array.
        object?[] buffer = new object?[p2capacity + 1];

        _consumerBuffer = buffer;
        _consumerMask = mask;
        _producerBuffer = buffer;
        _producerMask = mask;
        _maxQueueCapacity = ((long)Common.CeilingPowerOfTwo(maxCapacity)) << 1;

        Volatile.Write(ref _producerLimit, mask); // empty to start with
    }

    /// <summary>The maximum number of elements the queue can hold.</summary>
    public int Capacity => (int)(_maxQueueCapacity / 2);

    // ----- index helpers (release/acquire semantics) -----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long LvProducerIndex() => Volatile.Read(ref _producerIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long LvConsumerIndex() => Volatile.Read(ref _consumerIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long LvProducerLimit() => Volatile.Read(ref _producerLimit);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SoProducerIndex(long v) => Volatile.Write(ref _producerIndex, v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CasProducerIndex(long expect, long update)
        => Interlocked.CompareExchange(ref _producerIndex, update, expect) == expect;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SoConsumerIndex(long v) => Volatile.Write(ref _consumerIndex, v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CasProducerLimit(long expect, long update)
        => Interlocked.CompareExchange(ref _producerLimit, update, expect) == expect;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SoProducerLimit(long v) => Volatile.Write(ref _producerLimit, v);

    // ----- element access (release store / acquire load) -----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SoElement(object?[] buffer, long offset, object? e)
        => Volatile.Write(ref buffer[(int)offset], e);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object? LvElement(object?[] buffer, long offset)
        => Volatile.Read(ref buffer[(int)offset]);

    /// <summary>
    /// Index is stored as (index &lt;&lt; 1) because the lower bit flags a resize; the extra shift is
    /// compensated for by reducing the element shift.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ModifiedCalcElementOffset(long index, long mask) => (index & mask) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long NextArrayOffset(long mask) => ModifiedCalcElementOffset(mask + 2, long.MaxValue);

    // ----- capacity policy (from the growable subclass) -----

    private int GetNextBufferSize(object?[] buffer)
    {
        long maxSize = _maxQueueCapacity / 2;
        Common.RequireState(maxSize >= buffer.Length);
        int newSize = 2 * (buffer.Length - 1);
        return newSize + 1;
    }

    private long GetCurrentBufferCapacity(long mask)
        => (mask + 2 == _maxQueueCapacity) ? _maxQueueCapacity : mask;

    private long AvailableInQueue(long pIndex, long cIndex) => _maxQueueCapacity - (pIndex - cIndex);

    // ----- public queue operations -----

    /// <summary>Offers an element. Returns false only if the queue is full at max capacity.</summary>
    public bool Offer(E e)
    {
        if (e == null)
        {
            throw new ArgumentNullException(nameof(e));
        }

        long mask;
        long pIndex;
        object?[] buffer;

        while (true)
        {
            long producerLimit = LvProducerLimit();
            pIndex = LvProducerIndex();
            // Lower bit set indicates a resize in progress; spin until cleared.
            if ((pIndex & 1) == 1)
            {
                continue;
            }

            // mask/buffer may change on resize -> only use after a successful CAS.
            mask = _producerMask;
            buffer = _producerBuffer;

            if (producerLimit <= pIndex)
            {
                int result = OfferSlowPath(mask, pIndex, producerLimit);
                switch (result)
                {
                    case 0:
                        break;
                    case 1:
                        continue;
                    case 2:
                        return false;
                    case 3:
                        Resize(mask, buffer, pIndex, e);
                        return true;
                }
            }

            if (CasProducerIndex(pIndex, pIndex + 2))
            {
                break;
            }
        }

        // INDEX visible before ELEMENT, consistent with consumer expectation.
        long offset = ModifiedCalcElementOffset(pIndex, mask);
        SoElement(buffer, offset, e);
        return true;
    }

    private int OfferSlowPath(long mask, long pIndex, long producerLimit)
    {
        long cIndex = LvConsumerIndex();
        long bufferCapacity = GetCurrentBufferCapacity(mask);
        if (cIndex + bufferCapacity > pIndex)
        {
            if (!CasProducerLimit(producerLimit, cIndex + bufferCapacity))
            {
                return 1; // retry from top
            }
            return 0; // goto pIndex CAS
        }
        if (AvailableInQueue(pIndex, cIndex) <= 0)
        {
            return 2; // full, cannot grow
        }
        if (CasProducerIndex(pIndex, pIndex + 1)) // grab index for resize (set lower bit)
        {
            return 3; // resize
        }
        return 1; // failed resize attempt, retry
    }

    /// <summary>Removes and returns the head, or null if empty. Single-consumer only.</summary>
    public E? Poll()
    {
        object?[] buffer = _consumerBuffer;
        long index = _consumerIndex;
        long mask = _consumerMask;

        long offset = ModifiedCalcElementOffset(index, mask);
        object? e = LvElement(buffer, offset);
        if (e == null)
        {
            if (index != LvProducerIndex())
            {
                // Null is not a strong enough emptiness indicator; the producer index says otherwise,
                // so spin until the element becomes visible.
                do
                {
                    e = LvElement(buffer, offset);
                }
                while (e == null);
            }
            else
            {
                return null;
            }
        }
        if (ReferenceEquals(e, Jump))
        {
            object?[] nextBuffer = GetNextBuffer(buffer, mask);
            return NewBufferPoll(nextBuffer, index);
        }
        SoElement(buffer, offset, null);
        SoConsumerIndex(index + 2);
        return (E)e!;
    }

    /// <summary>Returns the head without removing it, or null if empty. Single-consumer only.</summary>
    public E? Peek()
    {
        object?[] buffer = _consumerBuffer;
        long index = _consumerIndex;
        long mask = _consumerMask;

        long offset = ModifiedCalcElementOffset(index, mask);
        object? e = LvElement(buffer, offset);
        if (e == null && index != LvProducerIndex())
        {
            while ((e = LvElement(buffer, offset)) == null)
            {
                // spin until visible
            }
        }
        if (ReferenceEquals(e, Jump))
        {
            return NewBufferPeek(GetNextBuffer(buffer, mask), index);
        }
        return (E?)e;
    }

    private object?[] GetNextBuffer(object?[] buffer, long mask)
    {
        long nextArrayOffset = NextArrayOffset(mask);
        var nextBuffer = (object?[]?)LvElement(buffer, nextArrayOffset);
        SoElement(buffer, nextArrayOffset, null);
        return nextBuffer ?? throw new InvalidOperationException("missing next buffer link");
    }

    private E NewBufferPoll(object?[] nextBuffer, long index)
    {
        long offsetInNew = NewBufferAndOffset(nextBuffer, index);
        object? n = LvElement(nextBuffer, offsetInNew)
            ?? throw new InvalidOperationException("new buffer must have at least one element");
        SoElement(nextBuffer, offsetInNew, null);
        SoConsumerIndex(index + 2);
        return (E)n;
    }

    private E NewBufferPeek(object?[] nextBuffer, long index)
    {
        long offsetInNew = NewBufferAndOffset(nextBuffer, index);
        object? n = LvElement(nextBuffer, offsetInNew)
            ?? throw new InvalidOperationException("new buffer must have at least one element");
        return (E)n;
    }

    private long NewBufferAndOffset(object?[] nextBuffer, long index)
    {
        _consumerBuffer = nextBuffer;
        _consumerMask = (nextBuffer.Length - 2L) << 1;
        return ModifiedCalcElementOffset(index, _consumerMask);
    }

    private void Resize(long oldMask, object?[] oldBuffer, long pIndex, E e)
    {
        int newBufferLength = GetNextBufferSize(oldBuffer);
        object?[] newBuffer;
        try
        {
            newBuffer = new object?[newBufferLength];
        }
        catch (OutOfMemoryException)
        {
            // The producer that entered Resize already advanced the index (setting the resize bit);
            // clear it so a recoverable allocation failure does not permanently livelock every
            // subsequent Offer (which spins while the resize bit is set).
            SoProducerIndex(pIndex);
            throw;
        }

        _producerBuffer = newBuffer;
        int newMask = (newBufferLength - 2) << 1;
        _producerMask = newMask;

        long offsetInOld = ModifiedCalcElementOffset(pIndex, oldMask);
        long offsetInNew = ModifiedCalcElementOffset(pIndex, newMask);

        SoElement(newBuffer, offsetInNew, e);                       // element in new array
        SoElement(oldBuffer, NextArrayOffset(oldMask), newBuffer);  // link old -> new

        long cIndex = LvConsumerIndex();
        long availableInQueue = AvailableInQueue(pIndex, cIndex);
        Common.RequireState(availableInQueue > 0);

        // Invalidate racing CASs; never set the limit beyond a buffer's bounds.
        SoProducerLimit(pIndex + Math.Min(newMask, availableInQueue));

        // Make the resize visible to other producers.
        SoProducerIndex(pIndex + 2);

        // Make the resize visible to the consumer (INDEX before ELEMENT).
        SoElement(oldBuffer, offsetInOld, Jump);
    }

    /// <summary>An estimate of the number of elements; over-estimates under concurrency.</summary>
    public int Count
    {
        get
        {
            long after = LvConsumerIndex();
            long size;
            while (true)
            {
                long before = after;
                long currentProducerIndex = LvProducerIndex();
                after = LvConsumerIndex();
                if (before == after)
                {
                    size = (currentProducerIndex - after) >> 1;
                    break;
                }
            }
            return (int)Math.Min(size, int.MaxValue);
        }
    }

    /// <summary>Conservative emptiness check (may report non-empty transiently under concurrency).</summary>
    public bool IsEmpty => LvConsumerIndex() == LvProducerIndex();
}
