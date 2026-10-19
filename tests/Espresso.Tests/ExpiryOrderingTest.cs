using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Reads check expiry before loading the value (and writers publish the value before the timestamps),
/// so a read never returns a value that already expired and was reported as such. Every locked caller
/// exempts an in-flight async load from expiry explicitly. Quiet reads hide expired entries, and a read
/// that finds an expired entry schedules its cleanup.
/// </summary>
public class ExpiryOrderingTest
{
    private const long Second = 1_000_000_000L;

    private sealed class Clock
    {
        public long Now;
        public long Read() => Interlocked.Read(ref Now);
    }

    private sealed class ManualExecutor : IExecutor
    {
        private readonly Queue<Action> _tasks = new();
        public int Pending => _tasks.Count;
        public void Execute(Action action) => _tasks.Enqueue(action);
        public void RunAll()
        {
            while (_tasks.Count != 0) _tasks.Dequeue()();
        }
    }

    /// <summary>A ticker that parks one designated thread on its first read until released.</summary>
    private sealed class ParkingTicker
    {
        public long Now;
        public int ParkThread = -1;
        public readonly ManualResetEventSlim Parked = new();
        public readonly ManualResetEventSlim Resume = new();

        public long Read()
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref ParkThread))
            {
                Volatile.Write(ref ParkThread, -1);
                Parked.Set();
                if (!Resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            return Volatile.Read(ref Now);
        }
    }

    // ----- C19: a read never returns a value that expired (and was notified) before the read finished -----

    [Fact]
    public async Task GetIfPresent_NeverReturnsExpiredValueAfterRewrite()
    {
        var ticker = new ParkingTicker();
        var expired = new List<string?>();
        var cache = Cache.NewBuilder<int, string>().ExpireAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(ticker.Read).Executor(DirectExecutor.Instance)
            .RemovalListener((_, v, c) => { if (c == RemovalCause.Expired) lock (expired) expired.Add(v); })
            .Build();
        cache.Put(1, "old");
        ticker.Now = 61 * Second;

        Task<string?> reader = Task.Run(() =>
        {
            Volatile.Write(ref ticker.ParkThread, Environment.CurrentManagedThreadId);
            return cache.GetIfPresent(1);
        });
        try
        {
            Assert.True(ticker.Parked.Wait(TimeSpan.FromSeconds(10)));
            cache.Put(1, "new"); // replaces the expired value, reporting it as EXPIRED
            lock (expired) Assert.Contains("old", expired);
        }
        finally
        {
            ticker.Resume.Set();
        }
        Assert.NotEqual("old", await reader.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task PutIfAbsent_NeverReturnsExpiredValueAfterRewrite()
    {
        var ticker = new ParkingTicker();
        var cache = Cache.NewBuilder<int, string>().ExpireAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(ticker.Read).Executor(DirectExecutor.Instance)
            .Build();
        var store = (BoundedLocalCache<int, string>)cache;
        cache.Put(1, "old");
        ticker.Now = 61 * Second;

        Task<string?> reader = Task.Run(() =>
        {
            Volatile.Write(ref ticker.ParkThread, Environment.CurrentManagedThreadId);
            return store.PutIfAbsent(1, "absent");
        });
        try
        {
            Assert.True(ticker.Parked.Wait(TimeSpan.FromSeconds(10)));
            cache.Put(1, "new");
        }
        finally
        {
            ticker.Resume.Set();
        }
        Assert.NotEqual("old", await reader.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ----- C26: quiet reads hide expired entries; reads of an expired entry schedule cleanup -----

    [Fact]
    public void GetIfPresentQuietly_HidesExpiredEntry()
    {
        var clock = new Clock();
        var cache = Cache.NewBuilder<int, string>().ExpireAfterWrite(TimeSpan.FromSeconds(10))
            .Ticker(clock.Read).Executor(DirectExecutor.Instance).RecordStats().Build();
        var store = (BoundedLocalCache<int, string>)cache;
        cache.Put(1, "v");
        Assert.Equal("v", store.GetIfPresentQuietly(1));

        clock.Now = 20 * Second;

        Assert.Null(store.GetIfPresentQuietly(1));
        Assert.Equal(1, cache.EstimatedSize()); // no side effects: nothing was cleaned up
        Assert.Equal(0, cache.Stats().RequestCount);
    }

    [Fact]
    public void GetIfPresent_OfExpiredEntry_SchedulesCleanup()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        var cache = Cache.NewBuilder<int, string>().ExpireAfterWrite(TimeSpan.FromSeconds(10))
            .Ticker(clock.Read).Executor(executor).Build();
        cache.Put(1, "v");
        executor.RunAll();
        clock.Now = 20 * Second;

        Assert.Null(cache.GetIfPresent(1));

        Assert.True(executor.Pending > 0, "an expired read must schedule maintenance");
        executor.RunAll();
        Assert.Equal(0, cache.EstimatedSize());
    }

    // ----- C27: every caller exempts an in-flight async load whose timestamps say it has expired -----

    private sealed class InFlight
    {
        public required IAsyncCache<int, string> Async;
        public required BoundedLocalCache<int, Task<string>> Store;
        public required TaskCompletionSource<string> Load;
        public required List<RemovalCause> Removals;
    }

    /// <summary>
    /// An async cache holding an in-flight load whose timestamps are past their deadline (the clock has
    /// advanced beyond the async sentinel), so only the in-flight exemption keeps it from expiring.
    /// </summary>
    private static InFlight InFlightPastItsDeadline(Action<EspressoBuilder<int, string>> configure)
    {
        var clock = new Clock();
        var removals = new List<RemovalCause>();
        var builder = Cache.NewBuilder<int, string>()
            .Ticker(clock.Read).Executor(DirectExecutor.Instance).RecordStats()
            .RemovalListener((_, _, c) => { lock (removals) removals.Add(c); });
        configure(builder);
        IAsyncCache<int, string> async = builder.BuildAsync();
        var store = (BoundedLocalCache<int, Task<string>>)typeof(LocalAsyncCache<int, string>)
            .GetField("Cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(async)!;
        var load = new TaskCompletionSource<string>();
        async.Put(1, load.Task);
        clock.Now = BoundedLocalCache<int, Task<string>>.AsyncExpiry + (120 * Second);
        return new InFlight { Async = async, Store = store, Load = load, Removals = removals };
    }

    public static TheoryData<string> Policies => new() { "write", "access", "variable" };

    private static Action<EspressoBuilder<int, string>> Policy(string name) => name switch
    {
        "write" => b => b.ExpireAfterWrite(TimeSpan.FromSeconds(10)),
        "access" => b => b.ExpireAfterAccess(TimeSpan.FromSeconds(10)),
        _ => b => b.ExpireAfter(new FixedExpiry(10 * Second)),
    };

    private sealed class FixedExpiry(long duration) : IExpiry<int, string>
    {
        public long ExpireAfterCreate(int key, string value, long currentTime) => duration;
        public long ExpireAfterUpdate(int key, string value, long currentTime, long currentDuration) => duration;
        public long ExpireAfterRead(int key, string value, long currentTime, long currentDuration) => currentDuration;
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void InFlight_GetIfPresent_IsNotExpired(string policy)
    {
        InFlight c = InFlightPastItsDeadline(Policy(policy));
        Assert.Same(c.Load.Task, c.Store.GetIfPresent(1, recordStats: false));
        Assert.Same(c.Load.Task, c.Store.GetIfPresentQuietly(1));
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void InFlight_GetAllPresent_IsNotExpired(string policy)
    {
        InFlight c = InFlightPastItsDeadline(Policy(policy));
        Assert.Same(c.Load.Task, c.Store.GetAllPresent(new[] { 1 })[1]);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void InFlight_PutIfAbsent_IsNotExpired(string policy)
    {
        InFlight c = InFlightPastItsDeadline(Policy(policy));
        Assert.Same(c.Load.Task, c.Store.PutIfAbsent(1, Task.FromResult("other")));
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void InFlight_ComputeIfAbsent_IsNotExpired(string policy)
    {
        InFlight c = InFlightPastItsDeadline(Policy(policy));
        Assert.Same(c.Load.Task, c.Store.ComputeIfAbsent(1, _ => throw new InvalidOperationException(), recordStats: false));
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void InFlight_LockedComputeIfAbsent_IsNotExpired(string policy)
    {
        InFlight c = InFlightPastItsDeadline(Policy(policy));
        MethodInfo doCompute = typeof(BoundedLocalCache<int, Task<string>>)
            .GetMethod("DoComputeIfAbsent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Func<int, Task<string>?> mapping = _ => throw new InvalidOperationException();
        object? result = doCompute.Invoke(c.Store, new object[] { 1, mapping, false, true, 0L });
        Assert.Same(c.Load.Task, result);
        Assert.Equal(0, c.Async.Stats().EvictionCount);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void InFlight_Put_ReplacesRatherThanExpires(string policy)
    {
        InFlight c = InFlightPastItsDeadline(Policy(policy));
        Assert.Same(c.Load.Task, c.Store.Put(1, Task.FromResult("other")));
        Assert.Equal(0, c.Async.Stats().EvictionCount);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void InFlight_Remove_IsExplicit(string policy)
    {
        InFlight c = InFlightPastItsDeadline(Policy(policy));
        Assert.Same(c.Load.Task, c.Store.Remove(1));
        c.Load.SetResult("v"); // the async listener reports a removed future once it completes
        Assert.Equal(new[] { RemovalCause.Explicit }, c.Removals);
        Assert.Equal(0, c.Async.Stats().EvictionCount);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void InFlight_ConditionalRemove_IsExplicit(string policy)
    {
        InFlight c = InFlightPastItsDeadline(Policy(policy));
        Assert.True(c.Store.Remove(1, c.Load.Task));
        c.Load.SetResult("v");
        Assert.Equal(new[] { RemovalCause.Explicit }, c.Removals);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void InFlight_Replace_Succeeds(string policy)
    {
        InFlight c = InFlightPastItsDeadline(Policy(policy));
        Task<string> other = Task.FromResult("other");
        Assert.True(c.Store.Replace(1, c.Load.Task, other));
        Assert.Same(other, c.Store.GetIfPresentQuietly(1));
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public void InFlight_Maintenance_DoesNotEvict(string policy)
    {
        InFlight c = InFlightPastItsDeadline(Policy(policy));
        c.Store.CleanUp();
        Assert.Same(c.Load.Task, c.Store.GetIfPresentQuietly(1));
        Assert.Equal(0, c.Async.Stats().EvictionCount);
    }
}
