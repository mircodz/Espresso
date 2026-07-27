using System;
using System.Collections.Generic;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class PacerTest
{
    private static readonly long Tolerance = Pacer.Tolerance;
    // A delay routed through TimeSpan (100ns tick granularity) loses sub-tick nanos on the way back.
    private static long AtTick(long nanos) => nanos / 100L * 100L;
    private const long Now = 1_000_000_000L;
    private static readonly long OneMinute = TimeSpan.FromMinutes(1).Ticks * 100L;

    private sealed class RecordingScheduler : IScheduler
    {
        public readonly List<long> Delays = new();
        public readonly List<ControllableFuture> Futures = new();
        private readonly bool _completed;
        public RecordingScheduler(bool completed = false) => _completed = completed;

        public IScheduledFuture Schedule(IExecutor executor, Action command, TimeSpan delay)
        {
            Delays.Add(delay.Ticks * 100L);
            var f = new ControllableFuture { IsDone = _completed };
            Futures.Add(f);
            return f;
        }
    }

    private sealed class ControllableFuture : IScheduledFuture
    {
        public bool Cancelled;
        public bool IsDone { get; set; }
        public void Cancel() { Cancelled = true; IsDone = true; }
    }

    private static readonly Action Command = () => { };

    [Fact]
    public void Schedule_Initialize()
    {
        var scheduler = new RecordingScheduler();
        var pacer = new Pacer(scheduler);
        long delay = OneMinute;

        pacer.Schedule(DirectExecutor.Instance, Command, Now, delay);

        Assert.Single(scheduler.Delays);
        Assert.Equal(delay, scheduler.Delays[0]);
        Assert.Equal(Now + delay, pacer.NextFireTime);
    }

    [Fact]
    public void Schedule_Uninitialized_SkipsWhenNextFireTimeSet()
    {
        // future == null but NextFireTime != 0 → guards an immediate-scheduler infinite loop.
        var scheduler = new RecordingScheduler();
        var pacer = new Pacer(scheduler) { NextFireTime = Now };
        pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute);
        Assert.Empty(scheduler.Delays);
    }

    [Fact]
    public void Schedule_CompletedFuture_Reschedules()
    {
        var scheduler = new RecordingScheduler(completed: true);
        var pacer = new Pacer(scheduler);
        pacer.Schedule(DirectExecutor.Instance, Command, Now, 0L);
        pacer.Schedule(DirectExecutor.Instance, Command, Now, 0L);
        Assert.Equal(2, scheduler.Delays.Count);
        Assert.All(scheduler.Delays, d => Assert.Equal(AtTick(Tolerance), d));
    }

    [Fact]
    public void Schedule_OverduePendingFuture_Cancels()
    {
        var scheduler = new RecordingScheduler();
        var pacer = new Pacer(scheduler);
        pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute);
        var first = scheduler.Futures[0];

        // NextFireTime is now overdue relative to a new "now"; the pending future is cancelled+replaced.
        pacer.NextFireTime = Now - OneMinute;
        pacer.Schedule(DirectExecutor.Instance, Command, Now, 0L);

        Assert.True(first.Cancelled);
        Assert.Equal(2, scheduler.Delays.Count);
    }

    [Fact]
    public void Schedule_BeforeNextFireTime_Skips()
    {
        var scheduler = new RecordingScheduler();
        var pacer = new Pacer(scheduler);
        pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute); // arms a future
        long expected = pacer.NextFireTime;

        // A new schedule sooner than the pending fire (within tolerance) is skipped.
        pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute - Tolerance);
        Assert.Single(scheduler.Delays);
        Assert.Equal(expected, pacer.NextFireTime);
    }

    [Fact]
    public void Schedule_BeforeNextFireTime_MinimumDelay()
    {
        var scheduler = new RecordingScheduler();
        var pacer = new Pacer(scheduler);
        pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute);
        scheduler.Futures[0].IsDone = false;

        // A far-earlier schedule beyond the skip tolerance re-arms with the minimum (tolerance) delay.
        pacer.NextFireTime = Now + OneMinute;
        pacer.Schedule(DirectExecutor.Instance, Command, Now, Tolerance / 2);
        Assert.Equal(AtTick(Tolerance), scheduler.Delays[^1]);
        Assert.Equal(Now + Tolerance, pacer.NextFireTime);
    }

    [Fact]
    public void Cancel_ResetsState()
    {
        var scheduler = new RecordingScheduler();
        var pacer = new Pacer(scheduler);
        pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute);
        var f = scheduler.Futures[0];

        pacer.Cancel();
        Assert.True(f.Cancelled);
        Assert.Equal(0L, pacer.NextFireTime);
        Assert.False(pacer.IsScheduled);
    }

    [Fact]
    public void CalculateSchedule_AvoidsZeroSentinel()
    {
        var pacer = new Pacer(new RecordingScheduler());
        // scheduleAt == 0 with a delay above tolerance must bump NextFireTime off the 0L sentinel.
        long delay = pacer.CalculateSchedule(now: -Tolerance - 1, delay: Tolerance + 1, scheduleAt: 0L);
        Assert.Equal(Tolerance + 1, delay);
        Assert.Equal(1L, pacer.NextFireTime);
    }
}
