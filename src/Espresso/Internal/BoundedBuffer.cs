using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Espresso.Internal;

/// <summary>
/// A striped, non-blocking, bounded multiple-producer / single-consumer buffer.
/// <para>
/// Adapted from the 64-bit <c>Striped64</c> approach used by atomic counters, it lazily grows an array
/// of ring buffers so caches with little contention use minimal memory. When uncontended, all offers go
/// to a single ring; on contention (a failed offer) the table expands, doubling up to the nearest power
/// of two ≥ the CPU count (times a small factor). A single spinlock guards initialization, resizing, and
/// slot creation; threads that cannot take it simply try other slots.
/// </para>
/// <para>
/// This is a single concrete <c>sealed</c> type (rather than an <c>IBuffer</c>-based striping base with a
/// separate ring implementation) so the stripe table is typed as the sealed <see cref="RingBuffer"/>: the
/// JIT devirtualizes and inlines <see cref="RingBuffer.Offer"/> on the read hot path instead of dispatching
/// through an interface array element on every access.
/// </para>
/// </summary>
internal sealed class BoundedBuffer<E> where E : class
{
    /// <summary>
    /// The maximum number of elements per stripe. A larger buffer fills less often under contention,
    /// so the read path reaches the drain-scheduling (and its Monitor.TryEnter on the eviction lock)
    /// far less frequently — the key to read scaling past a handful of threads.
    /// </summary>
    internal const int BufferSize = 128;
    internal const int Mask = BufferSize - 1;

    private static readonly int NCpu = Environment.ProcessorCount;

    /// <summary>The bound on the table size.</summary>
    private static readonly int MaximumTableSize = 4 * Common.CeilingPowerOfTwo(NCpu);

    /// <summary>Initial stripe count: one per CPU (power-of-two) so producers spread out immediately.</summary>
    private static readonly int InitialTableSize = Common.CeilingPowerOfTwo(NCpu);

    /// <summary>The maximum number of attempts when trying to expand the table.</summary>
    private const int Attempts = 3;

    /// <summary>Table of ring buffers. When non-null, size is a power of two.</summary>
    private volatile RingBuffer?[]? _table;

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
        RingBuffer?[]? buffers = _table;
        int mask;
        RingBuffer? buffer;
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
            RingBuffer?[]? buffers = _table;
            int n;
            if (buffers != null && (n = buffers.Length) > 0)
            {
                RingBuffer? buffer = buffers[(n - 1) & h];
                if (buffer == null)
                {
                    if (_tableBusy == 0 && CasTableBusy())
                    {
                        bool created = false;
                        try
                        {
                            RingBuffer?[]? rs = _table;
                            int mask;
                            int j;
                            if (rs != null && (mask = rs.Length) > 0 && rs[j = (mask - 1) & h] == null)
                            {
                                rs[j] = new RingBuffer(e);
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
                            var expanded = new RingBuffer?[n << 1];
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
                        var rs = new RingBuffer?[InitialTableSize];
                        rs[h & (InitialTableSize - 1)] = new RingBuffer(e);
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
        RingBuffer?[]? buffers = _table;
        if (buffers == null)
        {
            return;
        }
        foreach (RingBuffer? buffer in buffers)
        {
            buffer?.DrainTo(consumer);
        }
    }

    public long Reads
    {
        get
        {
            RingBuffer?[]? buffers = _table;
            if (buffers == null)
            {
                return 0;
            }
            long reads = 0;
            foreach (RingBuffer? buffer in buffers)
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
            RingBuffer?[]? buffers = _table;
            if (buffers == null)
            {
                return 0;
            }
            long writes = 0;
            foreach (RingBuffer? buffer in buffers)
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

    /// <summary>
    /// A single ring buffer stripe. The read and write counters sit on separate 128-byte cache sectors
    /// (15 padding longs each side) to avoid false sharing between the producer's write counter and the
    /// consumer's read counter. Sealed so the striping table can hold it by concrete type and the JIT
    /// devirtualizes and inlines <see cref="Offer"/> on the read hot path.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private sealed class RingBuffer
    {
        private readonly E?[] _buffer;
#pragma warning disable CS0169, IDE0051 // padding
        private long _p00, _p01, _p02, _p03, _p04, _p05, _p06, _p07;
        private long _p08, _p09, _p10, _p11, _p12, _p13, _p14;
#pragma warning restore CS0169, IDE0051
        private long _readCounter;
#pragma warning disable CS0169, IDE0051 // padding
        private long _q00, _q01, _q02, _q03, _q04, _q05, _q06, _q07;
        private long _q08, _q09, _q10, _q11, _q12, _q13, _q14;
#pragma warning restore CS0169, IDE0051
        private long _writeCounter;

        public RingBuffer(E e)
        {
            _buffer = new E?[BufferSize];
            Volatile.Write(ref _buffer[0], e);
            _writeCounter = 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Offer(E e)
        {
            long head = Volatile.Read(ref _readCounter);
            // Plain read of the write counter: the CAS below is the linearization point and re-validates
            // it, so a stale value only ever yields a (lossy-safe) Failed, never a corrupt slot.
            long tail = _writeCounter;
            long size = tail - head;
            if (size >= BufferSize)
            {
                return BufferResult.Full;
            }
            if (Interlocked.CompareExchange(ref _writeCounter, tail + 1, tail) == tail)
            {
                int index = (int)(tail & Mask);
                Volatile.Write(ref _buffer[index], e);
                return BufferResult.Success;
            }
            return BufferResult.Failed;
        }

        public void DrainTo(Action<E> consumer)
        {
            long head = Volatile.Read(ref _readCounter);
            long tail = Volatile.Read(ref _writeCounter);
            if (tail - head == 0)
            {
                return;
            }
            do
            {
                int index = (int)(head & Mask);
                E? e = Volatile.Read(ref _buffer[index]);
                if (e == null)
                {
                    break; // not published yet
                }
                Volatile.Write(ref _buffer[index], null);
                consumer(e);
                head++;
            }
            while (head != tail);
            Volatile.Write(ref _readCounter, head);
        }

        public long Size => Volatile.Read(ref _writeCounter) - Volatile.Read(ref _readCounter);
        public long Reads => Volatile.Read(ref _readCounter);
        public long Writes => Volatile.Read(ref _writeCounter);
    }
}
