using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class TimerWheelTest
{
    private static T StaticField<T>(string name)
        => (T)typeof(TimerWheel<long, string>)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static readonly long[] Spans = StaticField<long[]>("Spans");
    private static readonly int[] Shift = StaticField<int[]>("Shift");
    private static readonly int[] Buckets = StaticField<int[]>("Buckets");
    private const int ExpirationThreshold = 1_000;

    private static Node<long, string>[][] Wheel(TimerWheel<long, string> wheel)
        => (Node<long, string>[][])typeof(TimerWheel<long, string>)
            .GetField("_wheel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wheel)!;

    private static readonly long[] Clocks =
    {
        long.MinValue, -Spans[0] + 1, 0L, 0xfffffffc0000000L,
        long.MaxValue - Spans[0] + 1, long.MaxValue, 0x123456789abcdefL,
    };

    private static long Seconds(long s) => s * TimeSpan.FromSeconds(1).Ticks * 100L;
    private static long Minutes(long m) => m * TimeSpan.FromMinutes(1).Ticks * 100L;
    private static long Hours(long h) => h * TimeSpan.FromHours(1).Ticks * 100L;
    private static long Days(long d) => d * TimeSpan.FromDays(1).Ticks * 100L;

    private sealed class Timer : Node<long, string>
    {
        private long _variableTime;
        private Node<long, string>? _prev;
        private Node<long, string>? _next;

        public Timer(long variableTime) : base(0, "v") => _variableTime = variableTime;

        public override long VariableTime { get => _variableTime; set => _variableTime = value; }
        public override bool CasVariableTime(long expect, long update)
        {
            if (_variableTime == expect) { _variableTime = update; return true; }
            return false;
        }
        public override Node<long, string>? PreviousInVariableOrder { get => _prev; set => _prev = value; }
        public override Node<long, string>? NextInVariableOrder { get => _next; set => _next = value; }
    }

    private sealed class CountingCache : ITimerWheelCache<long, string>
    {
        private readonly Func<Node<long, string>, bool> _onEvict;
        public readonly List<Node<long, string>> Evicted = new();
        public CountingCache(Func<Node<long, string>, bool>? onEvict = null)
            => _onEvict = onEvict ?? (_ => true);
        public bool EvictEntry(Node<long, string> node, RemovalCause cause, long now)
        {
            Evicted.Add(node);
            return _onEvict(node);
        }
    }

    public static IEnumerable<object[]> ClockData() => Clocks.Select(c => new object[] { c });

    public static IEnumerable<object[]> ScheduleData()
    {
        foreach (long clock in Clocks)
        {
            yield return new object[] { clock, Seconds(10), 0 };
            yield return new object[] { clock, Minutes(3), 2 };
            yield return new object[] { clock, Minutes(10), 3 };
        }
    }

    [Theory]
    [MemberData(nameof(ScheduleData))]
    public void Schedule(long clock, long duration, int expired)
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = clock;
        foreach (int timeout in new[] { 25, 90, 240 })
        {
            wheel.Schedule(new Timer(clock + Seconds(timeout)));
        }
        wheel.Advance(cache, clock + duration, int.MaxValue);
        Assert.Equal(expired, cache.Evicted.Count);
        foreach (var node in cache.Evicted)
        {
            Assert.True(node.VariableTime - (clock + duration) <= 0);
        }
    }

    [Fact]
    public void FindBucket_LargeDuration_ReturnsLastWheel()
    {
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = 0L;
        var timer = new Timer(long.MaxValue / 2);
        wheel.Schedule(timer);
        var lastBucket = Wheel(wheel)[Wheel(wheel).Length - 1][0];
        // A duration exceeding all spans falls through to the last wheel's single bucket.
        Assert.Same(lastBucket, timer.NextInVariableOrder);
        Assert.Same(lastBucket, timer.PreviousInVariableOrder);
    }

    [Fact]
    public void Advance_DoesNotEvictFutureTimers()
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = 0L;
        wheel.Schedule(new Timer(10 * Spans[0]));
        wheel.Advance(cache, Spans[0], int.MaxValue);
        Assert.Empty(cache.Evicted);
    }

    [Theory]
    [MemberData(nameof(ClockData))]
    public void Advance(long clock)
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = clock;
        wheel.Schedule(new Timer(wheel.Nanos + Spans[0]));
        wheel.Advance(cache, clock + 13 * Spans[0], int.MaxValue);
        Assert.Single(cache.Evicted);
    }

    [Fact]
    public void Advance_Overflow()
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = -Days(365) / 2;
        wheel.Schedule(new Timer(wheel.Nanos + Spans[0]));
        wheel.Advance(cache, wheel.Nanos + Days(365), int.MaxValue);
        Assert.Single(cache.Evicted);
    }

    [Fact]
    public void Advance_AcrossZero()
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = -Spans[0];
        wheel.Schedule(new Timer(0));
        wheel.Advance(cache, Spans[0], int.MaxValue);
        Assert.Single(cache.Evicted);
    }

    [Fact]
    public void Advance_ToExactlyZero()
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = -Spans[0];
        wheel.Schedule(new Timer(0));
        wheel.Advance(cache, 0, int.MaxValue);
        Assert.Single(cache.Evicted);
    }

    [Fact]
    public void Advance_FromMinValueAcrossZero()
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = long.MinValue;
        wheel.Schedule(new Timer(long.MinValue + Spans[0]));
        wheel.Advance(cache, Spans[0], int.MaxValue);
        Assert.Single(cache.Evicted);
    }

    [Fact]
    public void Advance_LargeDelta()
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = 0;
        wheel.Schedule(new Timer(Spans[0]));
        wheel.Advance(cache, long.MaxValue, int.MaxValue);
        Assert.Single(cache.Evicted);
    }

    [Theory]
    [MemberData(nameof(ClockData))]
    public void Advance_Backwards(long clock)
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = clock;
        var rnd = new Random(1234);
        for (int i = 0; i < 1000; i++)
        {
            long duration = (long)(rnd.NextDouble() * Days(10));
            wheel.Schedule(new Timer(clock + duration));
        }
        for (int i = 0; i < Buckets.Length; i++)
        {
            wheel.Advance(cache, clock - 3 * Spans[i], int.MaxValue);
        }
        Assert.Empty(cache.Evicted);
    }

    [Theory]
    [MemberData(nameof(ClockData))]
    public void Advance_Reschedule(long clock)
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = clock;

        var t15 = new Timer(clock + Seconds(15));
        var t80 = new Timer(clock + Seconds(80));
        wheel.Schedule(t15);
        wheel.Schedule(t80);

        wheel.Advance(cache, clock + Seconds(45), int.MaxValue);
        Assert.Equal(new Node<long, string>[] { t15 }, cache.Evicted);
        Assert.Equal(1, Size(wheel));

        wheel.Advance(cache, clock + Seconds(70), int.MaxValue);
        Assert.Equal(1, Size(wheel));

        wheel.Advance(cache, clock + Seconds(90), int.MaxValue);
        Assert.Equal(new Node<long, string>[] { t15, t80 }, cache.Evicted);
        Assert.Equal(0, Size(wheel));
    }

    [Fact]
    public void Advance_Exception()
    {
        var wheel = new TimerWheel<long, string>();
        var cache = new CountingCache(_ => throw new ArgumentException());
        var timer = new Timer(wheel.Nanos + Spans[1]);
        wheel.Nanos = 0L;
        wheel.Schedule(timer);
        Assert.Throws<ArgumentException>(() => wheel.Advance(cache, long.MaxValue, int.MaxValue));
        Assert.Same(timer, Wheel(wheel)[1][1].NextInVariableOrder);
        Assert.Equal(0, wheel.Nanos);
    }

    [Fact]
    public void Advance_BoundedPerCycle()
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = 0L;
        int count = (2 * ExpirationThreshold) + 1;
        for (int i = 0; i < count; i++)
        {
            wheel.Schedule(new Timer(Spans[0]));
        }

        long advanceTo = 13 * Spans[0];
        Assert.Equal(0, wheel.Advance(cache, advanceTo, ExpirationThreshold));
        Assert.Equal(0L, wheel.Nanos);
        Assert.Equal(ExpirationThreshold, cache.Evicted.Count);

        Assert.Equal(0, wheel.Advance(cache, advanceTo, ExpirationThreshold));
        Assert.Equal(2 * ExpirationThreshold, cache.Evicted.Count);

        Assert.True(wheel.Advance(cache, advanceTo, ExpirationThreshold) > 0);
        Assert.Equal(advanceTo, wheel.Nanos);
        Assert.Equal(count, cache.Evicted.Count);
        CheckTimerWheel(wheel, advanceTo);
    }

    [Fact]
    public void Advance_BoundedPerCycle_AcrossBuckets()
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = 0L;
        int first = (3 * ExpirationThreshold) / 10;
        int second = ExpirationThreshold - first;
        int third = ExpirationThreshold / 2;
        int[] perBucket = { first, second, third };
        int count = first + second + third;
        for (int b = 0; b < perBucket.Length; b++)
        {
            for (int i = 0; i < perBucket[b]; i++)
            {
                wheel.Schedule(new Timer((b + 1) * Spans[0]));
            }
        }

        long advanceTo = 13 * Spans[0];
        Assert.Equal(0, wheel.Advance(cache, advanceTo, ExpirationThreshold));
        Assert.Equal(0L, wheel.Nanos);
        Assert.Equal(ExpirationThreshold, cache.Evicted.Count);

        Assert.True(wheel.Advance(cache, advanceTo, ExpirationThreshold) > 0);
        Assert.Equal(advanceTo, wheel.Nanos);
        Assert.Equal(count, cache.Evicted.Count);
        CheckTimerWheel(wheel, advanceTo);
    }

    [Theory]
    [MemberData(nameof(ClockData))]
    public void GetExpirationDelay_Empty(long clock)
    {
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = clock;
        Assert.Equal(long.MaxValue, wheel.GetExpirationDelay());
    }

    [Theory]
    [MemberData(nameof(ClockData))]
    public void GetExpirationDelay_FirstWheel(long clock)
    {
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = clock;
        wheel.Schedule(new Timer(clock + Seconds(1)));
        long result = wheel.GetExpirationDelay();
        Assert.True(result > 0L);
        Assert.True(result <= Spans[0]);
    }

    [Theory]
    [MemberData(nameof(ClockData))]
    public void GetExpirationDelay_LastWheel(long clock)
    {
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = clock;
        long delay = Days(14);
        wheel.Schedule(new Timer(clock + delay));
        long result = wheel.GetExpirationDelay();
        Assert.True(result > 0L);
        Assert.True(result <= delay);
    }

    [Fact]
    public void GetExpirationDelay_NotInLastWheel_LessThanMax()
    {
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = 0L;
        wheel.Schedule(new Timer(Spans[0]));
        Assert.True(wheel.GetExpirationDelay() < Spans[1]);
    }

    [Theory]
    [MemberData(nameof(ClockData))]
    public void Reschedule(long clock)
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = clock;

        var timer = new Timer(clock + Minutes(15));
        wheel.Schedule(timer);
        var startBucket = timer.NextInVariableOrder;

        timer.VariableTime = clock + Hours(2);
        wheel.Reschedule(timer);
        Assert.NotSame(startBucket, timer.NextInVariableOrder);

        wheel.Advance(cache, clock + Days(1), int.MaxValue);
        CheckEmpty(wheel);
    }

    [Theory]
    [MemberData(nameof(ClockData))]
    public void Deschedule(long clock)
    {
        var wheel = new TimerWheel<long, string>();
        var timer = new Timer(clock + 100);
        wheel.Nanos = clock;
        wheel.Schedule(timer);
        wheel.Deschedule(timer);
        Assert.Null(timer.NextInVariableOrder);
        Assert.Null(timer.PreviousInVariableOrder);
    }

    [Theory]
    [MemberData(nameof(ClockData))]
    public void Deschedule_NotScheduled(long clock)
    {
        var wheel = new TimerWheel<long, string>();
        var timer = new Timer(clock + 100);
        wheel.Nanos = clock;
        wheel.Deschedule(timer);
        Assert.Null(timer.NextInVariableOrder);
        Assert.Null(timer.PreviousInVariableOrder);
    }

    [Theory]
    [MemberData(nameof(ClockData))]
    public void Expire_Reschedule(long clock)
    {
        var wheel = new TimerWheel<long, string>();
        var cache = new CountingCache(node =>
        {
            node.VariableTime = wheel.Nanos + 100;
            return false;
        });
        wheel.Nanos = clock;
        wheel.Schedule(new Timer(clock + 100));
        wheel.Advance(cache, clock + Spans[0], int.MaxValue);

        Assert.Single(cache.Evicted);
        Assert.NotNull(cache.Evicted[0].NextInVariableOrder);
        Assert.NotNull(cache.Evicted[0].PreviousInVariableOrder);
    }

    public static IEnumerable<object[]> CascadingData()
    {
        var rnd = new Random(99);
        for (int i = 1; i < Spans.Length - 1; i++)
        {
            long span = Spans[i];
            long timeout = span + 1 + (long)(rnd.NextDouble() * (span - 1));
            long duration = span + 1 + (long)(rnd.NextDouble() * (timeout - span - 2));
            foreach (long clock in Clocks)
            {
                yield return new object[] { clock, duration, timeout, i };
            }
        }
    }

    [Theory]
    [MemberData(nameof(CascadingData))]
    public void Cascade(long clock, long duration, long timeout, int span)
    {
        var cache = new CountingCache();
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = clock;
        wheel.Schedule(new Timer(clock + timeout));
        wheel.Advance(cache, clock + duration, int.MaxValue);

        int count = 0;
        for (int i = 0; i <= span; i++)
        {
            for (int j = 0; j < Wheel(wheel)[i].Length; j++)
            {
                count += GetTimers(Wheel(wheel)[i][j]).Count;
            }
        }
        Assert.Equal(1, count);
    }

    [Theory]
    [MemberData(nameof(ClockData))]
    public void Iterator_Fixed(long clock)
    {
        var wheel = new TimerWheel<long, string>();
        wheel.Nanos = clock;
        var input = new List<long>();
        for (int i = 0; i < 21; i++)
        {
            long time = clock + Seconds(2L << i);
            wheel.Schedule(new Timer(time));
            input.Add(time);
        }

        var ascending = Drain(wheel, ascending: true);
        Assert.Equal(input, ascending);

        var descending = Drain(wheel, ascending: false);
        var reversed = new List<long>(input);
        reversed.Reverse();
        Assert.Equal(reversed, descending);
    }

    // ----- helpers -----

    private static List<long> Drain(TimerWheel<long, string> wheel, bool ascending)
    {
        var result = new List<long>();
        var e = ascending ? wheel.GetEnumerator() : wheel.GetDescendingEnumerator();
        while (e.MoveNext())
        {
            result.Add(e.Current.VariableTime);
        }
        return result;
    }

    private static int Size(TimerWheel<long, string> wheel)
    {
        int count = 0;
        foreach (var w in Wheel(wheel))
        {
            foreach (var sentinel in w)
            {
                count += GetTimers(sentinel).Count;
            }
        }
        return count;
    }

    private static List<long> GetTimers(Node<long, string> sentinel)
    {
        var timers = new List<long>();
        for (var node = sentinel.NextInVariableOrder; !ReferenceEquals(node, sentinel); node = node!.NextInVariableOrder)
        {
            timers.Add(node!.VariableTime);
        }
        return timers;
    }

    private static void CheckTimerWheel(TimerWheel<long, string> wheel, long duration)
    {
        for (int i = 0; i < Wheel(wheel).Length; i++)
        {
            for (int j = 0; j < Wheel(wheel)[i].Length; j++)
            {
                foreach (long timer in GetTimers(Wheel(wheel)[i][j]))
                {
                    Assert.False(timer - duration <= 0, $"wheel[{i}][{j}] has an unexpired-late timer");
                }
            }
        }
    }

    private static void CheckEmpty(TimerWheel<long, string> wheel)
    {
        foreach (var w in Wheel(wheel))
        {
            foreach (var sentinel in w)
            {
                Assert.Same(sentinel, sentinel.NextInVariableOrder);
                Assert.Same(sentinel, sentinel.PreviousInVariableOrder);
            }
        }
    }
}
