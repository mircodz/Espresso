using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// An inserted future that is already complete is accounted for once. The insertion evaluates its
/// expiry and weight, so the completion handler must not evaluate them again (which would charge the
/// creation as an update), and only a computed load or a write whose accounting was deferred to its
/// completion records a load statistic.
/// </summary>
public sealed class AsyncCompletionAccountingTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const long Minute = 60L * 1_000_000_000L;

    private sealed class Clock
    {
        public long Now;
        public Action? OnRead;

        public long Read()
        {
            Action? onRead = OnRead;
            OnRead = null;
            onRead?.Invoke();
            return Now;
        }
    }

    /// <summary>One hour after a create, one minute after an update; reads keep the current duration.</summary>
    private sealed class CountingExpiry : IExpiry<int, string>
    {
        public int Creates;
        public int Updates;

        public long ExpireAfterCreate(int key, string value, long currentTime)
        {
            Interlocked.Increment(ref Creates);
            return 60 * Minute;
        }

        public long ExpireAfterUpdate(int key, string value, long currentTime, long currentDuration)
        {
            Interlocked.Increment(ref Updates);
            return Minute;
        }

        public long ExpireAfterRead(int key, string value, long currentTime, long currentDuration)
            => currentDuration;
    }

    private static IAsyncCache<int, string> NewExpiringCache(Clock clock, CountingExpiry expiry)
        => Cache.NewBuilder<int, string>()
            .ExpireAfter(expiry)
            .Ticker(clock.Read)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();

    private static void AssertAliveAfterTwoMinutes(IAsyncCache<int, string> cache, Clock clock)
    {
        clock.Now += 2 * Minute;
        cache.CleanUp();
        Assert.NotNull(cache.GetIfPresent(1));
    }

    [Fact]
    public void Put_ReadyFuture_EvaluatesExpiryOnceAsCreate()
    {
        var clock = new Clock();
        var expiry = new CountingExpiry();
        var cache = NewExpiringCache(clock, expiry);

        cache.Put(1, Task.FromResult("v"));

        Assert.Equal(1, expiry.Creates);
        Assert.Equal(0, expiry.Updates);
        AssertAliveAfterTwoMinutes(cache, clock);
    }

    [Fact]
    public async Task Get_SynchronouslyCompletedLoad_EvaluatesExpiryOnceAsCreate()
    {
        var clock = new Clock();
        var expiry = new CountingExpiry();
        var cache = NewExpiringCache(clock, expiry);

        Assert.Equal("v", await cache.Get(1, (_, _) => Task.FromResult<string?>("v")).WaitAsync(Timeout));

        Assert.Equal(1, expiry.Creates);
        Assert.Equal(0, expiry.Updates);
        AssertAliveAfterTwoMinutes(cache, clock);
    }

    [Fact]
    public void Put_FutureCompletingDuringInsertion_IsNotChargedAsUpdate()
    {
        var clock = new Clock();
        var expiry = new CountingExpiry();
        var cache = NewExpiringCache(clock, expiry);
        var load = new TaskCompletionSource<string>();
        // The caller sees an in-flight future, but it completes before the insertion evaluates the
        // expiry (the insertion reads the ticker first), so the insertion charges the creation itself.
        clock.OnRead = () => load.SetResult("v");

        cache.Put(1, load.Task);

        Assert.True(load.Task.IsCompletedSuccessfully);
        Assert.Equal(1, expiry.Creates);
        Assert.Equal(0, expiry.Updates);
        AssertAliveAfterTwoMinutes(cache, clock);
    }

    [Fact]
    public async Task Put_InFlightFuture_EvaluatesExpiryAsCreateOnCompletion()
    {
        var clock = new Clock();
        var expiry = new CountingExpiry();
        var cache = NewExpiringCache(clock, expiry);
        var load = new TaskCompletionSource<string>();

        cache.Put(1, load.Task);
        load.SetResult("v");
        await WaitUntil(() => expiry.Creates == 1);

        Assert.Equal(0, expiry.Updates);
        AssertAliveAfterTwoMinutes(cache, clock);
    }

    [Fact]
    public void Put_ReadyFuture_WeighsOnce()
    {
        int weighs = 0;
        var cache = Cache.NewBuilder<int, string>()
            .MaximumWeight(100)
            .Weigher((_, v) =>
            {
                Interlocked.Increment(ref weighs);
                return v.Length;
            })
            .Executor(DirectExecutor.Instance)
            .BuildAsync();

        cache.Put(1, Task.FromResult("v"));

        Assert.Equal(1, weighs);
    }

    private static IAsyncCache<int, string> NewStatsCache()
        => Cache.NewBuilder<int, string>()
            .MaximumSize(100)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .BuildAsync();

    [Fact]
    public void Put_ReadyFuture_RecordsNoLoad()
    {
        var cache = NewStatsCache();

        cache.Put(1, Task.FromResult("v"));

        Assert.Equal(0, cache.Stats().LoadCount);
    }

    [Fact]
    public async Task Put_InFlightFuture_RecordsLoadOnCompletion()
    {
        var cache = NewStatsCache();
        var load = new TaskCompletionSource<string>();

        cache.Put(1, load.Task);
        load.SetResult("v");
        await WaitUntil(() => cache.Stats().LoadSuccessCount == 1);

        Assert.Equal(1, cache.Stats().LoadSuccessCount);
        Assert.Equal(0, cache.Stats().LoadFailureCount);
    }

    [Fact]
    public async Task Get_SynchronouslyCompletedLoad_RecordsLoad()
    {
        var cache = NewStatsCache();

        Assert.Equal("v", await cache.Get(1, (_, _) => Task.FromResult<string?>("v")).WaitAsync(Timeout));

        Assert.Equal(1, cache.Stats().LoadSuccessCount);
        Assert.Equal(1, cache.Stats().MissCount);
    }

    [Fact]
    public void Put_FutureCompletedWithoutAValue_RecordsALoadFailure()
    {
        var cache = NewStatsCache();

        cache.Put(1, Task.FromResult<string>(null!));

        Assert.Null(cache.GetIfPresent(1));
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Equal(0, cache.Stats().LoadSuccessCount);
    }

    [Fact]
    public void Put_SameFutureCompletingDuringTheWrite_KeepsTheCompletedWeight()
    {
        WarmUpPutPaths();
        var cache = NewWeightedCache((_, v) => v.Length);
        var store = StoreOf(cache);
        var load = new TaskCompletionSource<string>(); // continuations run inline on completion
        cache.Put(1, load.Task); // in flight: weighs nothing until its completion finalizes the weight
        var node = NodeOf(store, 1);

        RewriteWhileCompleting(cache, node, load, "vvvvv");
        cache.CleanUp();

        Assert.Same(load.Task, cache.GetIfPresent(1));
        Assert.Equal(5, node.Weight);
        Assert.Equal(5, node.PolicyWeight);
        Assert.Equal(5, Field<long>(store, "_weightedSize"));
    }

    [Fact]
    public void Put_SameFutureCompletingDuringTheWrite_CountsTowardsTheMaximumSize()
    {
        WarmUpPutPaths();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(100)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();
        var store = StoreOf(cache);
        var load = new TaskCompletionSource<string>();
        cache.Put(1, load.Task);
        var node = NodeOf(store, 1);

        RewriteWhileCompleting(cache, node, load, "v");
        cache.CleanUp();

        Assert.Same(load.Task, cache.GetIfPresent(1));
        Assert.Equal(1, node.Weight);
        Assert.Equal(1, node.PolicyWeight);
        Assert.Equal(1, Field<long>(store, "_weightedSize"));
    }

    [Fact]
    public void Put_SameFutureCompletingDuringTheWrite_RemovesTheEntryWhenWeighingFails()
    {
        WarmUpPutPaths();
        int weighs = 0;
        var cache = NewWeightedCache((_, v) => Interlocked.Increment(ref weighs) == 1
            ? v.Length
            : throw new InvalidOperationException("weigher failure"));
        var store = StoreOf(cache);
        var load = new TaskCompletionSource<string>();
        cache.Put(1, load.Task);
        var node = NodeOf(store, 1);

        RewriteWhileCompleting(cache, node, load, "vvvvv");
        cache.CleanUp();

        // The insertion's completion weighs the value, the write's finalization weighs it again, and
        // that failure removes the entry as a failed completion does.
        Assert.Equal(2, weighs);
        Assert.Null(cache.GetIfPresent(1));
        Assert.Equal(0, Field<long>(store, "_weightedSize"));
    }

    private static IAsyncCache<int, string> NewWeightedCache(Func<int, string, int> weigher)
        => Cache.NewBuilder<int, string>()
            .MaximumWeight(100)
            .Weigher(weigher)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();

    /// <summary>
    /// Writes an in-flight future over itself while the entry's lock is held, so the write weighs the
    /// future (nothing, while in flight) and then blocks on that lock; completes the future meanwhile, so
    /// the insertion's completion finalizes the weight inline (the lock is reentrant); then lets the
    /// write store its weight.
    /// </summary>
    private static void RewriteWhileCompleting(
        IAsyncCache<int, string> cache, Node<int, Task<string>> node, TaskCompletionSource<string> load, string value)
    {
        Thread writer;
        lock (node)
        {
            long contended = Monitor.LockContentionCount;
            writer = new Thread(() => cache.Put(1, load.Task)) { IsBackground = true };
            writer.Start();
            Assert.True(SpinWait.SpinUntil(
                () => ((writer.ThreadState & ThreadState.WaitSleepJoin) != 0)
                    && (Monitor.LockContentionCount > contended), Timeout),
                "the write did not block on the entry's lock");
            load.SetResult(value);
        }
        Assert.True(writer.Join(Timeout));
    }

    /// <summary>Compiles the write paths, so the racing write blocks only on the entry's lock.</summary>
    private static void WarmUpPutPaths()
    {
        var cache = NewWeightedCache((_, v) => v.Length);
        var load = new TaskCompletionSource<string>();
        cache.Put(1, load.Task);
        cache.Put(1, load.Task);
        load.SetResult("x");
        cache.Put(1, load.Task);
        cache.CleanUp();
    }

    private static BoundedLocalCache<int, Task<string>> StoreOf(IAsyncCache<int, string> cache)
        => (BoundedLocalCache<int, Task<string>>)typeof(LocalAsyncCache<int, string>)
            .GetField("Cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static Node<int, Task<string>> NodeOf(BoundedLocalCache<int, Task<string>> store, int key)
        => Field<ConcurrentHashMap<int, Node<int, Task<string>>>>(store, "_data").GetOrDefault(key)!;

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
        }
    }
}
