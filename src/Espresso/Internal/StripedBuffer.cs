using System;
using System.Threading;

namespace Espresso.Internal;

/// <summary>
/// A base class providing the mechanics for dynamic striping of bounded buffers. Adapted from the
/// 64-bit <c>Striped64</c> approach used by atomic counters, modified to lazily grow an array of
/// buffers so that caches with little contention use minimal memory.
/// <para>
/// When uncontended, all updates go to a single buffer. On contention (a failed offer) the table
/// expands, doubling on further contention up to the nearest power of two ≥ the CPU count (times a
/// small factor). A single spinlock guards initialization, resizing, and slot creation; threads that
/// cannot take it simply try other slots.
/// </para>
/// </summary>
internal abstract class StripedBuffer<E> : IBuffer<E> where E : class
{
    /// <summary>Number of CPUs.</summary>
    private static readonly int NCpu = Environment.ProcessorCount;

    /// <summary>The bound on the table size.</summary>
    private static readonly int MaximumTableSize = 4 * Common.CeilingPowerOfTwo(NCpu);

    /// <summary>The maximum number of attempts when trying to expand the table.</summary>
    private const int Attempts = 3;

    /// <summary>Table of buffers. When non-null, size is a power of two.</summary>
    private volatile IBuffer<E>?[]? _table;

    /// <summary>Spinlock (locked via CAS) used when resizing and/or creating buffers.</summary>
    private int _tableBusy;

    private bool CasTableBusy() => Interlocked.CompareExchange(ref _tableBusy, 1, 0) == 0;

    /// <summary>Creates a new buffer populated with a single element after resizing.</summary>
    protected abstract IBuffer<E> Create(E e);

    public int Offer(E e)
    {
        long z = Mix64(Environment.CurrentManagedThreadId);
        int increment = ((int)(z >>> 32)) | 1;
        int h = (int)z;

        bool uncontended = true;
        IBuffer<E>?[]? buffers = _table;
        int mask;
        IBuffer<E>? buffer;
        int result;
        if (buffers == null
            || (mask = buffers.Length - 1) < 0
            || (buffer = buffers[h & mask]) == null
            || !(uncontended = (result = buffer.Offer(e)) != BufferResult.Failed))
        {
            return ExpandOrRetry(e, h, increment, uncontended);
        }
        return result;
    }

    /// <summary>
    /// Handles updates involving initialization, resizing, creating new buffers, and/or contention.
    /// </summary>
    private int ExpandOrRetry(E e, int h, int increment, bool wasUncontended)
    {
        int result = BufferResult.Failed;
        bool collide = false; // whether the last slot was nonempty
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            IBuffer<E>?[]? buffers = _table;
            int n;
            if (buffers != null && (n = buffers.Length) > 0)
            {
                IBuffer<E>? buffer = buffers[(n - 1) & h];
                if (buffer == null)
                {
                    if (_tableBusy == 0 && CasTableBusy())
                    {
                        bool created = false;
                        try
                        {
                            IBuffer<E>?[]? rs = _table;
                            int mask;
                            int j;
                            if (rs != null && (mask = rs.Length) > 0 && rs[j = (mask - 1) & h] == null)
                            {
                                rs[j] = Create(e);
                                created = true;
                            }
                        }
                        finally
                        {
                            Volatile.Write(ref _tableBusy, 0);
                        }
                        if (created)
                        {
                            result = BufferResult.Success;
                            break;
                        }
                        continue; // slot is now non-empty
                    }
                    collide = false;
                }
                else if (!wasUncontended) // CAS already known to fail
                {
                    wasUncontended = true; // continue after rehash
                }
                else if ((result = buffer.Offer(e)) != BufferResult.Failed)
                {
                    break;
                }
                else if (n >= MaximumTableSize || _table != buffers)
                {
                    collide = false; // at max size or stale
                }
                else if (!collide)
                {
                    collide = true;
                }
                else if (_tableBusy == 0 && CasTableBusy())
                {
                    try
                    {
                        if (_table == buffers) // expand unless stale
                        {
                            var expanded = new IBuffer<E>?[n << 1];
                            Array.Copy(buffers, expanded, n);
                            _table = expanded;
                        }
                    }
                    finally
                    {
                        Volatile.Write(ref _tableBusy, 0);
                    }
                    collide = false;
                    continue; // retry with expanded table
                }
                h += increment;
            }
            else if (_tableBusy == 0 && _table == buffers && CasTableBusy())
            {
                bool init = false;
                try
                {
                    if (_table == buffers)
                    {
                        var rs = new IBuffer<E>?[1];
                        rs[0] = Create(e);
                        _table = rs;
                        init = true;
                    }
                }
                finally
                {
                    Volatile.Write(ref _tableBusy, 0);
                }
                if (init)
                {
                    result = BufferResult.Success;
                    break;
                }
            }
        }
        return result;
    }

    public void DrainTo(Action<E> consumer)
    {
        IBuffer<E>?[]? buffers = _table;
        if (buffers == null)
        {
            return;
        }
        foreach (IBuffer<E>? buffer in buffers)
        {
            buffer?.DrainTo(consumer);
        }
    }

    public long Reads
    {
        get
        {
            IBuffer<E>?[]? buffers = _table;
            if (buffers == null)
            {
                return 0;
            }
            long reads = 0;
            foreach (IBuffer<E>? buffer in buffers)
            {
                if (buffer != null)
                {
                    reads += buffer.Reads;
                }
            }
            return reads;
        }
    }

    public long Writes
    {
        get
        {
            IBuffer<E>?[]? buffers = _table;
            if (buffers == null)
            {
                return 0;
            }
            long writes = 0;
            foreach (IBuffer<E>? buffer in buffers)
            {
                if (buffer != null)
                {
                    writes += buffer.Writes;
                }
            }
            return writes;
        }
    }

    public long Size => Writes - Reads;

    /// <summary>Computes Stafford variant 13 of the 64-bit mix function.</summary>
    private static long Mix64(long z)
    {
        z = (z ^ (z >>> 30)) * unchecked((long)0xbf58476d1ce4e5b9L);
        z = (z ^ (z >>> 27)) * unchecked((long)0x94d049bb133111ebL);
        return z ^ (z >>> 31);
    }
}
