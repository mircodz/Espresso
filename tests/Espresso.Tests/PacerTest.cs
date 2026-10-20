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

    private sealed class InlineScheduler : IScheduler
    {
        public int Calls;

        public IScheduledFuture Schedule(IExecutor executor, Action command, TimeSpan delay)
        {
            Calls++;
            executor.Execute(command);
            return new ControllableFuture { IsDone = true };
        }
    }

    [Fact]
    public void Schedule_InlineCompletedReplacement_GuardsReentry()
    {
        var scheduler = new InlineScheduler();
        var pacer = new Pacer(scheduler);
        Action command = () => pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute);

        pacer.Schedule(DirectExecutor.Instance, command, Now, OneMinute);
        pacer.Schedule(DirectExecutor.Instance, command, Now, OneMinute);

        Assert.Equal(2, scheduler.Calls);
        Assert.False(pacer.IsScheduled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Schedule_InlineCancelThenReschedule_PreservesReentryGuard(bool replacement)
    {
        var scheduler = new InlineScheduler();
        var pacer = new Pacer(scheduler);
        if (replacement)
        {
            pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute);
        }
        int depth = 0;
        int maximumDepth = 0;
        Action command = null!;
        command = () =>
        {
            depth++;
            maximumDepth = Math.Max(maximumDepth, depth);
            try
            {
                // Bound any recursive inline scheduling; Schedule catches this exception.
                if (depth > 8) throw new InvalidOperationException("recursive inline scheduling");
                pacer.Cancel();
                pacer.Schedule(DirectExecutor.Instance, command, Now, OneMinute);
            }
            finally
            {
                depth--;
            }
        };

        pacer.Schedule(DirectExecutor.Instance, command, Now, OneMinute);

        Assert.Equal(replacement ? 2 : 1, scheduler.Calls);
        Assert.Equal(1, maximumDepth);
        Assert.Equal(0, depth);
        Assert.False(pacer.IsScheduled);
        Assert.Equal(0L, pacer.NextFireTime);
        pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute);
        Assert.Equal(replacement ? 3 : 2, scheduler.Calls);
    }

    private sealed class RejectingScheduler : IScheduler
    {
        public int Calls;
        public bool Reject;
        public bool ReturnNull;

        public IScheduledFuture Schedule(IExecutor executor, Action command, TimeSpan delay)
        {
            Calls++;
            if (Reject)
            {
                Reject = false;
                if (ReturnNull) return null!;
                throw new InvalidOperationException("scheduler rejected maintenance");
            }
            return new ControllableFuture();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Schedule_RejectedOrNullFuture_LeavesStateRetryable(bool replacement, bool returnNull)
    {
        var scheduler = new RejectingScheduler { ReturnNull = returnNull };
        var pacer = new Pacer(scheduler);
        if (replacement)
        {
            pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute);
        }
        scheduler.Reject = true;

        pacer.Schedule(DirectExecutor.Instance, Command, Now, 0L);

        Assert.False(pacer.IsScheduled);
        Assert.Equal(0L, pacer.NextFireTime);
        pacer.Schedule(DirectExecutor.Instance, Command, Now, 0L);
        Assert.True(pacer.IsScheduled);
        Assert.Equal(replacement ? 3 : 2, scheduler.Calls);
        pacer.Cancel();
    }

    [Fact]
    public void Cancel_NullFuture_PreservesInitializingState()
    {
        var pacer = new Pacer(new RecordingScheduler()) { NextFireTime = Now };

        pacer.Cancel();

        Assert.Equal(Now, pacer.NextFireTime);
        Assert.False(pacer.IsScheduled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancel_AfterRejectedOrNullFuture_AllowsRetry(bool returnNull)
    {
        var scheduler = new RejectingScheduler { Reject = true, ReturnNull = returnNull };
        var pacer = new Pacer(scheduler);
        pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute);

        pacer.Cancel();

        Assert.Equal(0L, pacer.NextFireTime);
        Assert.False(pacer.IsScheduled);
        pacer.Schedule(DirectExecutor.Instance, Command, Now, OneMinute);
        Assert.Equal(2, scheduler.Calls);
        Assert.True(pacer.IsScheduled);
        pacer.Cancel();
    }

    [Theory]
    [InlineData(0xFFFFFFFEL * TimeSpan.TicksPerMillisecond)]
    [InlineData(0xFFFFFFFFL * TimeSpan.TicksPerMillisecond)]
    [InlineData(60L * TimeSpan.TicksPerDay)]
    [InlineData(long.MaxValue)]
    public void SystemScheduler_LargeDelay_CanBeScheduledAndCancelled(long ticks)
    {
        IScheduledFuture future = Schedulers.System.Schedule(
            DirectExecutor.Instance, Command, TimeSpan.FromTicks(ticks));
        try
        {
            Assert.False(future.IsDone);
        }
        finally
        {
            future.Cancel();
        }
        Assert.True(future.IsDone);
    }

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
        var future = scheduler.Futures[0];

        pacer.Cancel();
        Assert.True(future.Cancelled);
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
