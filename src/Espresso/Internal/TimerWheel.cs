using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;

namespace Espresso.Internal;

/// <summary>
/// The eviction callback invoked by the timer wheel when a scheduled entry is due. Implemented by
/// <see cref="BoundedLocalCache{K,V}"/>; abstracted so the wheel can be unit-tested in isolation.
/// </summary>
internal interface ITimerWheelCache<K, V>
    where K : notnull
    where V : class
{
    bool EvictEntry(Node<K, V> node, RemovalCause cause, long now);
}

/// <summary>
/// A hierarchical timer wheel to add, remove, and fire variable-expiration events in amortized O(1)
/// time. Expiration events are deferred until the timer is advanced as part of the cache's
/// maintenance cycle.
/// <para>
/// Timers are stored in buckets on a circular buffer; the wheels are structured in a hierarchy
/// (seconds, minutes, hours, days) so that events scheduled far in the future cascade down as the
/// wheels rotate.
/// </para>
/// </summary>
internal sealed class TimerWheel<K, V> : IEnumerable<Node<K, V>>
    where K : notnull
    where V : class
{
    private static readonly int[] Buckets = { 64, 64, 32, 4, 1 };

    private static readonly long[] Spans =
    {
        Common.CeilingPowerOfTwo(TimeSpan.FromSeconds(1).Ticks * 100L), // 1.07s in ns
        Common.CeilingPowerOfTwo(TimeSpan.FromMinutes(1).Ticks * 100L), // 1.14m
        Common.CeilingPowerOfTwo(TimeSpan.FromHours(1).Ticks * 100L),   // 1.22h
        Common.CeilingPowerOfTwo(TimeSpan.FromDays(1).Ticks * 100L),    // 1.63d
        Buckets[3] * Common.CeilingPowerOfTwo(TimeSpan.FromDays(1).Ticks * 100L), // 6.5d
        Buckets[3] * Common.CeilingPowerOfTwo(TimeSpan.FromDays(1).Ticks * 100L), // 6.5d
    };

    private static readonly int[] Shift =
    {
        BitOperations.TrailingZeroCount(Spans[0]),
        BitOperations.TrailingZeroCount(Spans[1]),
        BitOperations.TrailingZeroCount(Spans[2]),
        BitOperations.TrailingZeroCount(Spans[3]),
        BitOperations.TrailingZeroCount(Spans[4]),
    };

    private readonly Node<K, V>[][] _wheel;
    internal long Nanos;

    public TimerWheel()
    {
        _wheel = new Node<K, V>[Buckets.Length][];
        for (int i = 0; i < _wheel.Length; i++)
        {
            _wheel[i] = new Node<K, V>[Buckets[i]];
            for (int j = 0; j < _wheel[i].Length; j++)
            {
                _wheel[i][j] = new TimerWheelSentinel<K, V>();
            }
        }
    }

    /// <summary>
    /// Advances the timer and expires entries that are due, up to <paramref name="limit"/> evictions;
    /// returns the unused budget. When the limit is reached the timer rewinds so the next advance
    /// processes the remaining backlog.
    /// </summary>
    public int Advance(ITimerWheelCache<K, V> cache, long currentTimeNanos, int limit)
    {
        long previousTimeNanos = Nanos;
        Nanos = currentTimeNanos;

        long previousDelta = previousTimeNanos;
        long currentDelta = currentTimeNanos;
        if (previousTimeNanos < 0 && currentTimeNanos >= 0)
        {
            previousDelta += long.MinValue;
            currentDelta += long.MinValue;
        }

        try
        {
            for (int i = 0; i < Shift.Length; i++)
            {
                long delta = (currentDelta >>> Shift[i]) - (previousDelta >>> Shift[i]);
                if (delta <= 0L)
                {
                    break;
                }
                long previousTicks = previousTimeNanos >>> Shift[i];
                limit = Expire(cache, i, previousTicks, delta, limit);
                if (limit == 0)
                {
                    Nanos = previousTimeNanos; // rewind to process the backlog next time
                    break;
                }
            }
        }
        catch
        {
            Nanos = previousTimeNanos;
            throw;
        }
        return limit;
    }

    private int Expire(ITimerWheelCache<K, V> cache, int index, long previousTicks, long delta, int limit)
    {
        Node<K, V>[] timerWheel = _wheel[index];
        int mask = timerWheel.Length - 1;

        int steps = (int)Math.Min(1 + delta, timerWheel.Length);
        int start = (int)(previousTicks & mask);
        int end = start + steps;

        for (int i = start; i < end; i++)
        {
            Node<K, V> sentinel = timerWheel[i & mask];
            Node<K, V>? prev = sentinel.PreviousInVariableOrder;
            Node<K, V>? node = sentinel.NextInVariableOrder;
            sentinel.PreviousInVariableOrder = sentinel;
            sentinel.NextInVariableOrder = sentinel;

            while (!ReferenceEquals(node, sentinel))
            {
                Node<K, V> current = node!;
                Node<K, V>? next = current.NextInVariableOrder;
                current.PreviousInVariableOrder = null;
                current.NextInVariableOrder = null;

                try
                {
                    if ((current.VariableTime - Nanos) > 0)
                    {
                        Schedule(current);
                    }
                    else if (cache.EvictEntry(current, RemovalCause.Expired, Nanos))
                    {
                        if (--limit == 0)
                        {
                            // Leave the unprocessed remainder in the bucket for the next advance.
                            if (!ReferenceEquals(next, sentinel))
                            {
                                next!.PreviousInVariableOrder = sentinel.PreviousInVariableOrder;
                                sentinel.PreviousInVariableOrder!.NextInVariableOrder = next;
                                sentinel.PreviousInVariableOrder = prev;
                            }
                            return 0;
                        }
                    }
                    else
                    {
                        Schedule(current);
                    }
                    node = next;
                }
                catch
                {
                    current.PreviousInVariableOrder = sentinel.PreviousInVariableOrder;
                    current.NextInVariableOrder = next;
                    sentinel.PreviousInVariableOrder!.NextInVariableOrder = current;
                    sentinel.PreviousInVariableOrder = prev;
                    throw;
                }
            }
        }
        return limit;
    }

    /// <summary>Schedules a timer event for the node.</summary>
    public void Schedule(Node<K, V> node)
    {
        Node<K, V> sentinel = FindBucket(node.VariableTime);
        Link(sentinel, node);
    }

    /// <summary>Reschedules an active timer event for the node.</summary>
    public void Reschedule(Node<K, V> node)
    {
        if (node.NextInVariableOrder != null)
        {
            Unlink(node);
            Schedule(node);
        }
    }

    /// <summary>Removes a timer event for this entry if present.</summary>
    public void Deschedule(Node<K, V> node)
    {
        Unlink(node);
        node.NextInVariableOrder = null;
        node.PreviousInVariableOrder = null;
    }

    /// <summary>Determines the bucket that the timer event should be added to.</summary>
    private Node<K, V> FindBucket(long time)
    {
        long duration = Math.Max(0L, time - Nanos);
        if (duration == 0L)
        {
            time = Nanos;
        }

        int length = _wheel.Length - 1;
        for (int i = 0; i < length; i++)
        {
            if (duration < Spans[i + 1])
            {
                long ticks = time >>> Shift[i];
                int index = (int)(ticks & (_wheel[i].Length - 1));
                return _wheel[i][index];
            }
        }
        return _wheel[length][0];
    }

    /// <summary>Adds the entry at the tail of the bucket's list.</summary>
    private static void Link(Node<K, V> sentinel, Node<K, V> node)
    {
        node.PreviousInVariableOrder = sentinel.PreviousInVariableOrder;
        node.NextInVariableOrder = sentinel;

        sentinel.PreviousInVariableOrder!.NextInVariableOrder = node;
        sentinel.PreviousInVariableOrder = node;
    }

    /// <summary>Removes the entry from its bucket, if scheduled.</summary>
    private static void Unlink(Node<K, V> node)
    {
        Node<K, V>? next = node.NextInVariableOrder;
        if (next != null)
        {
            Node<K, V>? prev = node.PreviousInVariableOrder;
            next.PreviousInVariableOrder = prev;
            prev!.NextInVariableOrder = next;
        }
    }

    /// <summary>Returns the duration until the next bucket expires, or <see cref="long.MaxValue"/> if none.</summary>
    public long GetExpirationDelay()
    {
        for (int i = 0; i < Shift.Length; i++)
        {
            Node<K, V>[] timerWheel = _wheel[i];
            long ticks = Nanos >>> Shift[i];

            long spanMask = Spans[i] - 1;
            int mask = timerWheel.Length - 1;
            int start = (int)(ticks & mask);
            int end = start + timerWheel.Length;
            for (int j = start; j < end; j++)
            {
                Node<K, V> sentinel = timerWheel[j & mask];
                Node<K, V>? next = sentinel.NextInVariableOrder;
                if (ReferenceEquals(next, sentinel))
                {
                    continue;
                }
                long buckets = j - start;
                long delay = (buckets << Shift[i]) - (Nanos & spanMask);
                delay = (delay > 0) ? delay : Spans[i];

                for (int k = i + 1; k < Shift.Length; k++)
                {
                    long nextDelay = PeekAhead(k);
                    delay = Math.Min(delay, nextDelay);
                }

                return delay;
            }
        }
        return long.MaxValue;
    }

    private long PeekAhead(int index)
    {
        long ticks = Nanos >>> Shift[index];
        Node<K, V>[] timerWheel = _wheel[index];

        long spanMask = Spans[index] - 1;
        int mask = timerWheel.Length - 1;
        int probe = (int)((ticks + 1) & mask);
        Node<K, V> sentinel = timerWheel[probe];
        Node<K, V>? next = sentinel.NextInVariableOrder;
        return ReferenceEquals(next, sentinel) ? long.MaxValue : (Spans[index] - (Nanos & spanMask));
    }

    // ----- iterators (roughly ordered by expiration; used later by Policy introspection) -----

    public IEnumerator<Node<K, V>> GetEnumerator() => new AscendingEnumerator(this);

    public IEnumerator<Node<K, V>> GetDescendingEnumerator() => new DescendingEnumerator(this);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private abstract class Traverser : IEnumerator<Node<K, V>>
    {
        protected readonly TimerWheel<K, V> Wheel;
        private readonly long _expectedNanos;
        protected Node<K, V>? Current;
        private Node<K, V>? _next;

        protected Traverser(TimerWheel<K, V> wheel)
        {
            Wheel = wheel;
            _expectedNanos = wheel.Nanos;
        }

        Node<K, V> IEnumerator<Node<K, V>>.Current => Current!;
        object IEnumerator.Current => Current!;

        public bool MoveNext()
        {
            if (Wheel.Nanos != _expectedNanos)
            {
                throw new InvalidOperationException("timer wheel was advanced during iteration");
            }
            if (_next == null)
            {
                if (IsDone())
                {
                    return false;
                }
                _next = ComputeNext();
                if (_next == null)
                {
                    return false;
                }
            }
            Current = _next;
            _next = null;
            return true;
        }

        private Node<K, V>? ComputeNext()
        {
            Node<K, V> node = Current ?? Sentinel();
            while (true)
            {
                node = Traverse(node)!;
                if (!ReferenceEquals(node, Sentinel()))
                {
                    return node;
                }
                Node<K, V>? bucket = GoToNextBucket();
                if (bucket != null)
                {
                    node = bucket;
                    continue;
                }
                Node<K, V>? wheelNode = GoToNextWheel();
                if (wheelNode != null)
                {
                    node = wheelNode;
                    continue;
                }
                return null;
            }
        }

        protected abstract bool IsDone();
        protected abstract Node<K, V> Sentinel();
        protected abstract Node<K, V> Traverse(Node<K, V> node);
        protected abstract Node<K, V>? GoToNextBucket();
        protected abstract Node<K, V>? GoToNextWheel();

        public void Reset() => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class AscendingEnumerator : Traverser
    {
        private int _wheelIndex;
        private int _steps;

        public AscendingEnumerator(TimerWheel<K, V> wheel) : base(wheel) { }

        protected override bool IsDone() => _wheelIndex == Wheel._wheel.Length;
        protected override Node<K, V> Sentinel() => Wheel._wheel[_wheelIndex][BucketIndex()];
        protected override Node<K, V> Traverse(Node<K, V> node) => node.NextInVariableOrder!;
        protected override Node<K, V>? GoToNextBucket()
            => (++_steps < Wheel._wheel[_wheelIndex].Length) ? Wheel._wheel[_wheelIndex][BucketIndex()] : null;
        protected override Node<K, V>? GoToNextWheel()
        {
            if (++_wheelIndex == Wheel._wheel.Length)
            {
                return null;
            }
            _steps = 0;
            return Wheel._wheel[_wheelIndex][BucketIndex()];
        }

        private int BucketIndex()
        {
            int ticks = (int)(Wheel.Nanos >>> Shift[_wheelIndex]);
            int bucketMask = Wheel._wheel[_wheelIndex].Length - 1;
            int bucketOffset = (ticks & bucketMask) + 1;
            return (bucketOffset + _steps) & bucketMask;
        }
    }

    private sealed class DescendingEnumerator : Traverser
    {
        private int _wheelIndex;
        private int _steps;

        public DescendingEnumerator(TimerWheel<K, V> wheel) : base(wheel)
            => _wheelIndex = wheel._wheel.Length - 1;

        protected override bool IsDone() => _wheelIndex == -1;
        protected override Node<K, V> Sentinel() => Wheel._wheel[_wheelIndex][BucketIndex()];
        protected override Node<K, V> Traverse(Node<K, V> node) => node.PreviousInVariableOrder!;
        protected override Node<K, V>? GoToNextBucket()
            => (++_steps < Wheel._wheel[_wheelIndex].Length) ? Wheel._wheel[_wheelIndex][BucketIndex()] : null;
        protected override Node<K, V>? GoToNextWheel()
        {
            if (--_wheelIndex < 0)
            {
                return null;
            }
            _steps = 0;
            return Wheel._wheel[_wheelIndex][BucketIndex()];
        }

        private int BucketIndex()
        {
            int ticks = (int)(Wheel.Nanos >>> Shift[_wheelIndex]);
            int bucketMask = Wheel._wheel[_wheelIndex].Length - 1;
            int bucketOffset = ticks & bucketMask;
            return (bucketOffset - _steps) & bucketMask;
        }
    }
}
