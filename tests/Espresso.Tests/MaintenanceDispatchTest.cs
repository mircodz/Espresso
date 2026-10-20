using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Regression tests for how maintenance is dispatched: exception isolation, rescheduling of incomplete
/// work, and not rooting a dropped cache from a pending scheduler timer.
/// </summary>
public class MaintenanceDispatchTest
{
    /// <summary>A key whose hash code throws once armed (simulates faulty user key code).</summary>
    private sealed class ArmableKey(int id)
    {
        public volatile bool Armed;
        public int Id { get; } = id;
        public override int GetHashCode() => Armed ? throw new InvalidOperationException("armed") : Id;
        public override bool Equals(object? obj) => obj is ArmableKey k && k.Id == Id;
    }

    private sealed class ManualExecutor : IExecutor
    {
        private readonly Queue<Action> _tasks = new();
        public void Execute(Action action) { lock (_tasks) { _tasks.Enqueue(action); } }
        public void RunAll()
        {
            while (true)
            {
                Action? a;
                lock (_tasks) { a = _tasks.Count > 0 ? _tasks.Dequeue() : null; }
                if (a == null) return;
                a();
            }
        }
    }

    private sealed class DroppingExecutor : IExecutor
    {
        public void Execute(Action command) { }
    }

    // Drains on the default thread-pool executor have no completion hook (the rescheduling under test
    // is specific to that executor), so these tests poll for the outcome. The bound only matters when
    // the pool is slow; a passing run exits as soon as the condition holds.
    private static readonly TimeSpan ThreadPoolDrainTimeout = TimeSpan.FromSeconds(30);

    private static T Field<T>(object o, string name)
        => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    [Fact]
    public void ExecutorMaintenanceTask_SwallowsMaintenanceFailure()
    {
        var executor = new ManualExecutor();
        var cache = Cache.NewBuilder<ArmableKey, string>().MaximumSize(1).Executor(executor).Build();
        var key = new ArmableKey(1);
        cache.Put(key, "a");
        key.Armed = true; // the queued AddTask hashes the key into the frequency sketch

        Assert.Null(Record.Exception(executor.RunAll));

        key.Armed = false;
        cache.Put(new ArmableKey(2), "b");
        Assert.Null(Record.Exception(executor.RunAll));
        cache.CleanUp();
        Assert.Equal(1, cache.EstimatedSize());
    }

    [Fact]
    public void AfterWriteInlineMaintenance_DoesNotSurfaceFailureToWriter()
    {
        // The executor drops every maintenance task, so the write buffer fills and a writer must run
        // the maintenance inline; a failure there must not propagate into the writer's Put.
        var cache = Cache.NewBuilder<ArmableKey, string>().MaximumSize(1).Executor(new DroppingExecutor()).Build();
        var poisoned = new ArmableKey(-1);
        cache.Put(poisoned, "p");
        poisoned.Armed = true;

        Exception? thrown = null;
        for (int i = 0; i < 100_000 && thrown == null; i++)
        {
            thrown = Record.Exception(() => cache.Put(new ArmableKey(i), "v"));
        }
        Assert.Null(thrown);
    }

    [Fact]
    public void CleanUp_SwallowsMaintenanceFailure()
    {
        var executor = new DroppingExecutor();
        var cache = Cache.NewBuilder<ArmableKey, string>().MaximumSize(1).Executor(executor).Build();
        var key = new ArmableKey(1);
        cache.Put(key, "a");
        key.Armed = true;
        Assert.Null(Record.Exception(cache.CleanUp));
    }

    [Fact]
    public void DefaultExecutorDrain_ReschedulesIncompleteExpiration()
    {
        long now = 0;
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .Ticker(() => Volatile.Read(ref now))
            .Build(); // default (thread-pool) executor
        const int count = 2_500; // more than the per-cycle expiration budget (1,000)
        for (int i = 0; i < count; i++)
        {
            cache.Put(i, "v");
        }
        cache.CleanUp();
        WaitForIdle(cache);

        Interlocked.Add(ref now, TimeSpan.FromSeconds(2).Ticks * 100L);
        cache.Put(-1, "fresh"); // schedules one ordinary drain on the thread pool

        var sw = Stopwatch.StartNew();
        while (cache.EstimatedSize() != 1 && sw.Elapsed < ThreadPoolDrainTimeout)
        {
            Thread.Sleep(5);
        }
        Assert.Equal(1, cache.EstimatedSize());
    }

    private sealed class DepthTrackingDirectExecutor : IExecutor
    {
        private int _depth;
        public int MaxDepth;
        public void Execute(Action command)
        {
            MaxDepth = Math.Max(MaxDepth, ++_depth);
            try { command(); }
            finally { _depth--; }
        }
    }

    [Fact]
    public void CallerRunsExecutor_IncompleteDrainIsNotResubmittedRecursively()
    {
        long now = 0;
        var executor = new DepthTrackingDirectExecutor();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .Ticker(() => now)
            .Executor(executor)
            .Build();
        for (int i = 0; i < 5_000; i++)
        {
            cache.Put(i, "v");
        }
        cache.CleanUp();
        now += TimeSpan.FromSeconds(2).Ticks * 100L;

        cache.Put(-1, "fresh"); // the inline drain leaves an expiration backlog (Required)
        Assert.Equal(1, executor.MaxDepth);
        for (int i = 0; i < 10 && cache.EstimatedSize() > 1; i++)
        {
            cache.CleanUp(); // like Java, a custom executor's backlog is finished by later activity
        }
        Assert.Equal(1, cache.EstimatedSize());
    }

    private static void WaitForIdle(object cache)
    {
        var sw = Stopwatch.StartNew();
        while (Field<int>(cache, "_drainStatus") != 0 && sw.Elapsed < ThreadPoolDrainTimeout)
        {
            Thread.Sleep(1);
        }
        Assert.Equal(0, Field<int>(cache, "_drainStatus"));
    }

    [Fact]
    public void PendingSchedulerTimer_DoesNotRootDroppedCache()
    {
        WeakReference weak = CreateAndDropCacheWithPendingTimer();
        for (int i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDropCacheWithPendingTimer()
    {
        var cache = Cache.NewBuilder<string, byte[]>()
            .ExpireAfterWrite(TimeSpan.FromHours(1))
            .Scheduler(Schedulers.System)
            .Executor(DirectExecutor.Instance)
            .Build();
        cache.Put("k", new byte[1024]);
        var pacer = Field<Pacer>(cache, "_pacer");
        Assert.True(pacer.IsScheduled);
        return new WeakReference(cache);
    }
}
