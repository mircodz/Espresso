using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Espresso.Internal;

/// <summary>
/// A striped, non-blocking, bounded buffer.
/// <para>
/// A circular ring buffer stores the elements transferred by producers to the single consumer. A
/// monotonically increasing pair of read/write counters index into a power-of-two-sized array.
/// Producers race to CAS the write counter and then publish their element with a release store; they
/// never retry or block on a failed CAS or a full buffer. The read and write counters live on
/// separate cache lines to avoid false sharing.
/// </para>
/// </summary>
internal sealed class BoundedBuffer<E> : StripedBuffer<E> where E : class
{
    /// <summary>
    /// The maximum number of elements per stripe. A larger buffer fills less often under contention,
    /// so the read path reaches the drain-scheduling (and its Monitor.TryEnter on the eviction lock)
    /// far less frequently — the key to read scaling past a handful of threads.
    /// </summary>
    internal const int BufferSize = 128;
    internal const int Mask = BufferSize - 1;

    protected override IBuffer<E> Create(E e) => new RingBuffer(e);

    /// <summary>
    /// The read and write counters sit on separate 128-byte cache sectors (15 padding longs each side)
    /// to avoid false sharing between the producer's write counter and the consumer's read counter.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private sealed class RingBuffer : IBuffer<E>
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
