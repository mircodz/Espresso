using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

public sealed class AsyncCacheTest
{
    private sealed class FakeTicker
    {
        private long _nanos;
        public long Read() => _nanos;
        public void Advance(TimeSpan by) => _nanos += by.Ticks * 100L;
    }

    private static IAsyncCache<int, string> NewCache()
        => Cache.NewBuilder<int, string>()
            .MaximumSize(1000)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .BuildAsync();

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition()) return;
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Get_WithLoader_CompletesWithValue()
    {
        var cache = NewCache();

        Task<string> future = cache.Get(1, k => "v" + k);

        Assert.Equal("v1", await future);
    }

    [Fact]
    public void Get_ConcurrentSameKey_ReturnsSameFutureAndCoalescesLoad()
    {
        var cache = NewCache();
        var gate = new TaskCompletionSource<string>();
        int loaderCalls = 0;

        // Task-returning overload lets the test control completion timing.
        Task<string> f1 = cache.Get(1, (_, _) => { Interlocked.Increment(ref loaderCalls); return gate.Task!; });
        Task<string> f2 = cache.Get(1, (_, _) => { Interlocked.Increment(ref loaderCalls); return gate.Task!; });

        Assert.Same(f1, f2);
        Assert.Equal(1, loaderCalls);

        gate.SetResult("done");
        Assert.Equal("done", f1.GetAwaiter().GetResult());
    }

    [Fact]
    public void Get_WhileLoading_FutureIsVisibleAndCounted()
    {
        var cache = NewCache();
        var gate = new TaskCompletionSource<string>();
        Task<string> f = cache.Get(1, (_, _) => gate.Task!);

        // While loading, the entry is present and counted.
        Assert.NotNull(cache.GetIfPresent(1));
        Assert.Same(f, cache.GetIfPresent(1));
        Assert.Equal(1, cache.EstimatedSize());

        gate.SetResult("v");
        Assert.Equal("v", f.GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Get_FailedLoad_RemovesEntry()
    {
        var cache = NewCache();
        var gate = new TaskCompletionSource<string>();
        Task<string> f = cache.Get(1, (_, _) => gate.Task!);

        Assert.NotNull(cache.GetIfPresent(1)); // present while loading
        gate.SetException(new InvalidOperationException("boom"));

        // Wait for the stored future to finish faulting, then for the removal continuation to run.
        await Assert.ThrowsAnyAsync<Exception>(async () => await f);
        await WaitUntil(() => cache.GetIfPresent(1) == null);

        Assert.Null(cache.GetIfPresent(1)); // removed after failure
        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    [Fact]
    public async Task Get_NullLoad_RemovesEntry()
    {
        var cache = NewCache();

        Task<string> f = cache.Get(1, k => null);

        await Assert.ThrowsAnyAsync<Exception>(async () => await f);
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public async Task Put_CompletedFuture_IsStored()
    {
        var cache = NewCache();

        cache.Put(1, Task.FromResult("v"));

        Task<string>? f = cache.GetIfPresent(1);
        Assert.NotNull(f);
        Assert.Equal("v", await f!);
    }

    [Fact]
    public async Task Get_LongRunningLoad_ExpiryTimerStartsFromCompletionNotInsert()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();

        var gate = new TaskCompletionSource<string>();
        cache.Get(1, (_, _) => gate.Task!);

        // Loads for longer than the expiry duration. The in-flight entry must NOT expire.
        ticker.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(cache.GetIfPresent(1));

        // Completion happens at t=5m; the 1-minute timer starts NOW, not from insertion.
        gate.SetResult("v");
        Task<string> stored = cache.GetIfPresent(1)!;
        Assert.Equal("v", await stored);
        await WaitUntil(() => cache.Stats().LoadSuccessCount == 1);

        ticker.Advance(TimeSpan.FromSeconds(30)); // 30s after completion
        Assert.NotNull(cache.GetIfPresent(1));    // still valid

        ticker.Advance(TimeSpan.FromSeconds(31)); // 61s after completion
        cache.CleanUp();
        Assert.Null(cache.GetIfPresent(1));       // now expired (from completion time)
    }

    [Fact]
    public void Get_SlowLoad_PinnedAgainstSizeEviction()
    {
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();

        var gate = new TaskCompletionSource<string>();
        cache.Get(0, (_, _) => gate.Task!); // in-flight, weight 0 → pinned

        // Flood far past the size bound with completed entries.
        for (int i = 1; i < 500; i++)
        {
            cache.Put(i, Task.FromResult("v" + i));
        }
        cache.CleanUp();

        // The still-loading entry (weight 0) must survive size eviction.
        Assert.NotNull(cache.GetIfPresent(0));

        gate.SetResult("done");
    }

    [Fact]
    public async Task Stats_SuccessfulLoad_CountedExactlyOnce()
    {
        var cache = NewCache();
        Task<string> f = cache.Get(1, k => "v" + k);
        await f;
        await WaitUntil(() => cache.Stats().LoadSuccessCount == 1);

        var stats = cache.Stats();
        Assert.Equal(1, stats.LoadSuccessCount);   // not double-counted
        Assert.Equal(0, stats.LoadFailureCount);
        Assert.Equal(1, stats.MissCount);
    }

    [Fact]
    public async Task Stats_FailedLoad_CountedAsFailureOnly()
    {
        var cache = NewCache();
        var gate = new TaskCompletionSource<string>();
        Task<string> f = cache.Get(1, (_, _) => gate.Task!);
        gate.SetException(new InvalidOperationException("x"));

        await Assert.ThrowsAnyAsync<Exception>(async () => await f);
        await WaitUntil(() => cache.Stats().LoadFailureCount == 1);

        var stats = cache.Stats();
        Assert.Equal(0, stats.LoadSuccessCount);   // a failed load must NOT record a success
        Assert.Equal(1, stats.LoadFailureCount);
    }
}
