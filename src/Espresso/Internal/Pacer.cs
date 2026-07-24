using System;

namespace Espresso.Internal;

/// <summary>
/// A pacing scheduler that prevents maintenance executions from happening too frequently. Only one
/// task may be scheduled at any time; the earliest pending task takes precedence, and the delay may be
/// raised when it falls below a tolerance threshold.
/// </summary>
internal sealed class Pacer
{
    /// <summary>The minimum scheduling delay (~1.07s), in nanoseconds.</summary>
    internal static readonly long Tolerance =
        Common.CeilingPowerOfTwo(TimeSpan.FromSeconds(1).Ticks * 100L);

    private readonly IScheduler _scheduler;

    internal long NextFireTime;
    private IScheduledFuture? _future;

    public Pacer(IScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _scheduler = scheduler;
    }

    /// <summary>Schedules the task, pacing the execution if it would occur too often.</summary>
    public void Schedule(IExecutor executor, Action command, long now, long delay)
    {
        long scheduleAt = now + delay;

        if (_future == null)
        {
            // Short-circuit an immediate scheduler causing an infinite loop during initialization.
            if (NextFireTime != 0L)
            {
                return;
            }
        }
        else
        {
            // Skip if a pending fire is still soon enough; otherwise cancel the future being replaced.
            if ((NextFireTime - now) > 0L && !_future.IsDone && MaySkip(scheduleAt))
            {
                return;
            }
            _future.Cancel();
        }

        long actualDelay = CalculateSchedule(now, delay, scheduleAt);
        _future = _scheduler.Schedule(executor, command, NanosToTimeSpan(actualDelay));
    }

    /// <summary>Attempts to cancel the scheduled task, if present.</summary>
    public void Cancel()
    {
        if (_future != null)
        {
            _future.Cancel();
            NextFireTime = 0L;
            _future = null;
        }
    }

    /// <summary>Whether a task is currently scheduled to run.</summary>
    public bool IsScheduled
    {
        get
        {
            IScheduledFuture? f = _future;
            return f != null && !f.IsDone;
        }
    }

    /// <summary>Whether the current fire time is sooner, or later but within the tolerance limit.</summary>
    internal bool MaySkip(long scheduleAt)
    {
        long delta = scheduleAt - NextFireTime;
        return delta >= -Tolerance;
    }

    /// <summary>Returns the delay and sets the next fire time, avoiding the 0L unscheduled sentinel.</summary>
    internal long CalculateSchedule(long now, long delay, long scheduleAt)
    {
        if (delay <= Tolerance)
        {
            // Use a minimum delay if close to now.
            NextFireTime = now + Tolerance;
            if (NextFireTime == 0L)
            {
                NextFireTime = 1L;
            }
            return Tolerance;
        }
        NextFireTime = (scheduleAt == 0L) ? 1L : scheduleAt;
        return delay;
    }

    private static TimeSpan NanosToTimeSpan(long nanos)
        => TimeSpan.FromTicks(nanos / 100L); // 100 ns == 1 tick
}
