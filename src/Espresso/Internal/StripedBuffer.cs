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

    /// <summary>Initial stripe count: one per CPU (power-of-two) so producers spread out immediately.</summary>
    private static readonly int InitialTableSize = Common.CeilingPowerOfTwo(NCpu);

    /// <summary>The maximum number of attempts when trying to expand the table.</summary>
    private const int Attempts = 3;

    /// <summary>Table of buffers. When non-null, size is a power of two.</summary>
    private volatile IBuffer<E>?[]? _table;

    /// <summary>Spinlock (locked via CAS) used when resizing and/or creating buffers.</summary>
    private int _tableBusy;

    /// <summary>
    /// Per-thread hash used to pick a stripe, cached so the common (uncontended) offer avoids recomputing
    /// the thread-id mix on every read. Initialized lazily from the managed thread id and advanced by an
    /// xorshift only when a stripe collision forces a rehash — the same scheme the counter-cell striping
    /// in <see cref="ConcurrentHashMap{K,V}"/> uses. Zero means "not yet initialized".
    /// </summary>
    [ThreadStatic] private static int _probe;

    private bool CasTableBusy() => Interlocked.CompareExchange(ref _tableBusy, 1, 0) == 0;

    /// <summary>Creates a new buffer populated with a single element after resizing.</summary>
    protected abstract IBuffer<E> Create(E e);

    /// <summary>Returns this thread's cached stripe probe, initializing it on first use (never zero).</summary>
    private static int GetProbe()
    {
        int p = _probe;
        if (p == 0)
        {
            p = (int)Mix64(Environment.CurrentManagedThreadId);
            _probe = p == 0 ? 1 : p; // guard against a zero mix so the field stays "initialized"
            p = _probe;
        }
        return p;
    }

    /// <summary>Advances the probe via xorshift (Marsaglia) and writes it back to the thread-local slot.</summary>
    private static int AdvanceProbe(int probe)
    {
        probe ^= probe << 13;
        probe ^= probe >>> 17;
        probe ^= probe << 5;
        _probe = probe;
        return probe;
    }

    public int Offer(E e)
    {
        int h = GetProbe();

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
            return ExpandOrRetry(e, h, uncontended);
        }
        return result;
    }

    /// <summary>
    /// Handles updates involving initialization, resizing, creating new buffers, and/or contention.
    /// </summary>
    private int ExpandOrRetry(E e, int h, bool wasUncontended)
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
                h = AdvanceProbe(h);
            }
            else if (_tableBusy == 0 && _table == buffers && CasTableBusy())
            {
                bool init = false;
                try
                {
                    if (_table == buffers)
                    {
                        // Pre-size to one stripe per (power-of-two) CPU so concurrent producers land on
                        // distinct stripes from the start, instead of contending on a single stripe and
                        // growing reactively. Only the stripe this thread uses is populated eagerly; the
                        // rest are created on first use.
                        var rs = new IBuffer<E>?[InitialTableSize];
                        rs[h & (InitialTableSize - 1)] = Create(e);
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
