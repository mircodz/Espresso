using System;
using System.Threading;

namespace Espresso;

/// <summary>
/// Schedules a task to run on an executor after a delay. Used by the cache to proactively run
/// maintenance so entries expire close to their deadline instead of only when the cache is next
/// touched.
/// </summary>
public interface IScheduler
{
    /// <summary>
    /// Schedules <paramref name="command"/> to be submitted to <paramref name="executor"/> after
    /// <paramref name="delay"/>. Returns a handle that can cancel the pending submission.
    /// </summary>
    /// <param name="executor">the executor that will run the task once the delay elapses.</param>
    /// <param name="command">the task to run.</param>
    /// <param name="delay">how long to wait before submitting the task.</param>
    IScheduledFuture Schedule(IExecutor executor, Action command, TimeSpan delay);
}

/// <summary>A cancellable handle for a task scheduled by an <see cref="IScheduler"/>.</summary>
public interface IScheduledFuture
{
    /// <summary>Attempts to cancel the pending task. A no-op if it has already run.</summary>
    void Cancel();

    /// <summary>Whether the task has completed (run or been cancelled).</summary>
    bool IsDone { get; }
}

/// <summary>Common <see cref="IScheduler"/> instances.</summary>
public static class Schedulers
{
    /// <summary>A scheduler that never schedules anything (disables proactive maintenance).</summary>
    public static IScheduler Disabled => DisabledScheduler.Instance;

    /// <summary>A scheduler backed by the system timer (<see cref="System.Threading.Timer"/>).</summary>
    public static IScheduler System => SystemScheduler.Instance;
}

/// <summary>A scheduler that ignores all requests; used when no proactive maintenance is wanted.</summary>
internal sealed class DisabledScheduler : IScheduler
{
    public static readonly DisabledScheduler Instance = new();
    private DisabledScheduler() { }

    public IScheduledFuture Schedule(IExecutor executor, Action command, TimeSpan delay)
        => CompletedFuture.Instance;
}

/// <summary>An already-completed, non-cancellable future for a disabled or immediate schedule.</summary>
internal sealed class CompletedFuture : IScheduledFuture
{
    public static readonly CompletedFuture Instance = new();
    private CompletedFuture() { }
    public void Cancel() { }
    public bool IsDone => true;
}

/// <summary>
/// A scheduler backed by a <see cref="System.Threading.Timer"/>. When the delay elapses the command is
/// handed to the cache's executor rather than run on the timer thread.
/// </summary>
internal sealed class SystemScheduler : IScheduler
{
    public static readonly SystemScheduler Instance = new();
    private SystemScheduler() { }

    public IScheduledFuture Schedule(IExecutor executor, Action command, TimeSpan delay)
    {
        var future = new TimerFuture();
        // Clamp to a non-negative delay; a due-or-past task fires promptly.
        long ms = (long)Math.Max(0d, delay.TotalMilliseconds);
        var timer = new Timer(_ =>
        {
            if (future.TryFire())
            {
                try { executor.Execute(command); }
                catch { /* a rejected executor drops the maintenance tick; the next access recovers */ }
            }
        }, null, ms, Timeout.Infinite);
        future.Bind(timer);
        return future;
    }

    private sealed class TimerFuture : IScheduledFuture
    {
        private volatile Timer? _timer;
        private int _state; // 0 = pending, 1 = fired/cancelled

        public void Bind(Timer timer)
        {
            _timer = timer;
            if (Volatile.Read(ref _state) != 0)
            {
                timer.Dispose(); // cancelled before binding completed
            }
        }

        public bool TryFire()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            {
                _timer?.Dispose();
                return true;
            }
            return false;
        }

        public void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            {
                _timer?.Dispose();
            }
        }

        public bool IsDone => Volatile.Read(ref _state) != 0;
    }
}
