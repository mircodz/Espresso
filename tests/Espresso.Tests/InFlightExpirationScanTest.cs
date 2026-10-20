using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// An in-flight async load whose timestamps say it has expired must not stall the expiration scans:
/// the eviction re-check stamps the async sentinel into its timestamps before resurrecting it, so the
/// scan rotates it out of the way and expires the entries behind it in the same cycle (rather than
/// re-peeking the same head node until the per-cycle budget is spent and then rescheduling forever).
/// Reads of an in-flight load do not give it real (expirable) timestamps either.
/// </summary>
public class InFlightExpirationScanTest
{
    private const long Second = 1_000_000_000L;

    private sealed class Clock
    {
        public long Now;
        public long Reads;

        public long Read()
        {
            Interlocked.Increment(ref Reads);
            return Interlocked.Read(ref Now);
        }
    }

    private sealed class CountingExecutor : IExecutor
    {
        public int Submissions;

        public void Execute(Action action)
        {
            Interlocked.Increment(ref Submissions);
            action();
        }
    }

    private sealed class Setup
    {
        public required Clock Clock;
        public required IAsyncCache<int, string> Async;
        public required BoundedLocalCache<int, Task<string>> Store;
        public required TaskCompletionSource<string?> Load;
    }

    private const int A = 1, B = 2, C = 3;

    /// <summary>
    /// The reviewer's two shapes. Write: <c>b</c>, <c>a</c> written at 0s, <c>a</c> rewritten with an
    /// in-flight future at 2s, <c>c</c> written at 3s. Access: <c>b</c> written and <c>a</c> loaded
    /// asynchronously at 0s, <c>a</c> read at 2s, <c>c</c> written at 3s. In both, at 20s every entry's
    /// timestamps say expired, and the in-flight <c>a</c> sits ahead of <c>c</c> in the scanned deque.
    /// </summary>
    private static Setup Create(string shape, IExecutor? executor)
    {
        var clock = new Clock();
        var builder = Cache.NewBuilder<int, string>().Ticker(clock.Read).RecordStats();
        if (executor != null)
        {
            builder.Executor(executor);
        }
        if (shape == "write")
        {
            builder.ExpireAfterWrite(TimeSpan.FromSeconds(10));
        }
        else
        {
            builder.ExpireAfterAccess(TimeSpan.FromSeconds(10));
        }
        IAsyncCache<int, string> async = builder.BuildAsync();
        var store = (BoundedLocalCache<int, Task<string>>)typeof(LocalAsyncCache<int, string>)
            .GetField("Cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(async)!;
        var load = new TaskCompletionSource<string?>();

        async.Put(B, Task.FromResult("b"));
        if (shape == "write")
        {
            async.Put(A, Task.FromResult("a"));
            clock.Now = 2 * Second;
            async.Put(A, load.Task!);
        }
        else
        {
            _ = async.Get(A, (_, _) => load.Task);
            clock.Now = 2 * Second;
            Assert.NotNull(async.GetIfPresent(A));
        }
        clock.Now = 3 * Second;
        async.Put(C, Task.FromResult("c"));
        clock.Now = 20 * Second;
        return new Setup { Clock = clock, Async = async, Store = store, Load = load };
    }

    private static Node<int, Task<string>>? NodeOf(BoundedLocalCache<int, Task<string>> store, int key)
    {
        var data = (ConcurrentHashMap<int, Node<int, Task<string>>>)typeof(BoundedLocalCache<int, Task<string>>)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        return data.GetOrDefault(key);
    }

    private static int DrainStatus(BoundedLocalCache<int, Task<string>> store)
        => (int)typeof(BoundedLocalCache<int, Task<string>>)
            .GetField("_drainStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    public static TheoryData<string> Shapes => new() { "write", "access" };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void OneCleanUp_ExpiresEntriesBehindAnInFlightLoad(string shape)
    {
        var executor = new CountingExecutor();
        Setup s = Create(shape, executor);
        int submissionsBefore = executor.Submissions;

        s.Store.CleanUp();

        Assert.Null(NodeOf(s.Store, B));
        Assert.Null(NodeOf(s.Store, C)); // was starved behind the in-flight head
        Assert.NotNull(NodeOf(s.Store, A));
        Assert.Equal(2, s.Async.Stats().EvictionCount);
        Assert.Equal(0, DrainStatus(s.Store)); // Idle: the budget was not exhausted, nothing re-armed
        Assert.True(executor.Submissions - submissionsBefore <= 1,
            $"maintenance was resubmitted {executor.Submissions - submissionsBefore} times");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task ThreadPoolMaintenance_DoesNotSpinWhileTheLoadIsPending(string shape)
    {
        // The default thread-pool executor reschedules an incomplete drain immediately, so a stalled
        // scan turns into a busy loop; every maintenance cycle reads the ticker, which counts them.
        Setup s = Create(shape, executor: null);

        s.Store.CleanUp();
        long readsAfterCleanUp = Interlocked.Read(ref s.Clock.Reads);
        await Task.Delay(300);
        long backgroundReads = Interlocked.Read(ref s.Clock.Reads) - readsAfterCleanUp;

        try
        {
            Assert.True(backgroundReads < 20, $"maintenance kept running in the background ({backgroundReads} ticker reads)");
            Assert.Null(NodeOf(s.Store, C));
            Assert.NotNull(NodeOf(s.Store, A));
        }
        finally
        {
            s.Load.TrySetResult("a"); // stop any background loop before the next test
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task CompletedLoad_ThenExpiresByItsRealTimers(string shape)
    {
        var executor = new CountingExecutor();
        Setup s = Create(shape, executor);
        s.Store.CleanUp();

        s.Load.SetResult("a");
        Node<int, Task<string>> a = NodeOf(s.Store, A)!;
        var sw = Stopwatch.StartNew();
        while (Timestamp(a, shape) > 20 * Second && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(1);
        }
        Assert.Equal(20 * Second, Timestamp(a, shape)); // completion restarted the real timer at 20s

        s.Clock.Now = 29 * Second;
        s.Store.CleanUp();
        Assert.Equal("a", await s.Async.GetIfPresent(A)!);

        s.Clock.Now = 40 * Second;
        s.Store.CleanUp();
        Assert.Null(NodeOf(s.Store, A));
        Assert.Equal(3, s.Async.Stats().EvictionCount);
    }

    private sealed class FixedExpiry : IExpiry<int, string>
    {
        public long ExpireAfterCreate(int key, string value, long currentTime) => 10 * Second;
        public long ExpireAfterUpdate(int key, string value, long currentTime, long currentDuration) => 10 * Second;
        public long ExpireAfterRead(int key, string value, long currentTime, long currentDuration) => currentDuration;
    }

    public static TheoryData<string> Policies => new() { "write", "access", "variable" };

    [Theory]
    [MemberData(nameof(Policies))]
    public void ResurrectedInFlightLoad_GetsTheAsyncSentinelInItsTimestamps(string policy)
    {
        // Advance past the in-flight load's own sentinel so its timestamps say expired; the eviction
        // re-check must push them out again (for the timer wheel, this is the variable time).
        var clock = new Clock();
        var builder = Cache.NewBuilder<int, string>().Ticker(clock.Read).Executor(DirectExecutor.Instance);
        _ = policy switch
        {
            "write" => builder.ExpireAfterWrite(TimeSpan.FromSeconds(10)),
            "access" => builder.ExpireAfterAccess(TimeSpan.FromSeconds(10)),
            _ => builder.ExpireAfter(new FixedExpiry()),
        };
        IAsyncCache<int, string> async = builder.BuildAsync();
        var store = (BoundedLocalCache<int, Task<string>>)typeof(LocalAsyncCache<int, string>)
            .GetField("Cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(async)!;
        var load = new TaskCompletionSource<string>();
        async.Put(A, load.Task);
        long now = BoundedLocalCache<int, Task<string>>.AsyncExpiry + (120 * Second);
        clock.Now = now;

        store.CleanUp();

        Node<int, Task<string>> a = NodeOf(store, A)!;
        Assert.NotNull(a);
        long expected = now + BoundedLocalCache<int, Task<string>>.AsyncExpiry;
        long actual = policy switch { "write" => a.WriteTime, "access" => a.AccessTime, _ => a.VariableTime };
        Assert.Equal(expected, actual);
        load.SetResult("a");
    }

    [Fact]
    public void Reads_DoNotGiveAnInFlightLoadRealTimestamps()
    {
        var clock = new Clock();
        IAsyncCache<int, string> async = Cache.NewBuilder<int, string>().Ticker(clock.Read)
            .Executor(DirectExecutor.Instance).ExpireAfterAccess(TimeSpan.FromSeconds(10)).BuildAsync();
        var store = (BoundedLocalCache<int, Task<string>>)typeof(LocalAsyncCache<int, string>)
            .GetField("Cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(async)!;
        var load = new TaskCompletionSource<string?>();
        _ = async.Get(A, (_, _) => load.Task);
        Node<int, Task<string>> a = NodeOf(store, A)!;
        long sentinel = a.AccessTime;
        Assert.Equal(BoundedLocalCache<int, Task<string>>.AsyncExpiry, sentinel);

        clock.Now = 2 * Second;
        Assert.NotNull(store.GetIfPresent(A, recordStats: false));
        Assert.Equal(sentinel, a.AccessTime);
        Assert.Single(store.GetAllPresent(new[] { A }));
        Assert.Equal(sentinel, a.AccessTime);
        load.SetResult("a");
    }

    private static long Timestamp(Node<int, Task<string>> node, string shape)
        => shape == "write" ? node.WriteTime : node.AccessTime;
}
