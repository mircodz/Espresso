using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

public sealed class AsyncCacheTest
{
    private sealed class FakeTicker : ITicker
    {
        private long _nanos;
        public long Read() => _nanos;
        public void Advance(TimeSpan by) => _nanos += by.Ticks * 100L;
    }

    private static IAsyncCache<int, string> NewCache()
        => Espresso.NewBuilder<int, string>()
            .MaximumSize(1000)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .BuildAsync();

    [Fact]
    public async Task Get_CompletesWithValue()
    {
        var cache = NewCache();
        Task<string> future = cache.Get(1, k => "v" + k);
        Assert.Equal("v1", await future);
    }

    [Fact]
    public void Get_ConcurrentSameKey_ReturnsSameFuture_CoalescesLoad()
    {
        var cache = NewCache();
        var gate = new TaskCompletionSource<string>();
        int loaderCalls = 0;

        // Use the Task-returning overload so we control completion timing.
        Task<string> f1 = cache.Get(1, (k, _) => { Interlocked.Increment(ref loaderCalls); return gate.Task!; });
        Task<string> f2 = cache.Get(1, (k, _) => { Interlocked.Increment(ref loaderCalls); return gate.Task!; });

        // Same in-flight future returned; the second Get did not start a new load.
        Assert.Same(f1, f2);
        Assert.Equal(1, loaderCalls);

        gate.SetResult("done");
        Assert.Equal("done", f1.GetAwaiter().GetResult());
    }

    [Fact]
    public void InFlightFuture_IsVisible_AndCounted()
    {
        var cache = NewCache();
        var gate = new TaskCompletionSource<string>();
        Task<string> f = cache.Get(1, (k, _) => gate.Task!);

        // While loading, the entry is present and counted.
        Assert.NotNull(cache.GetIfPresent(1));
        Assert.Same(f, cache.GetIfPresent(1));
        Assert.Equal(1, cache.EstimatedSize());

        gate.SetResult("v");
        Assert.Equal("v", f.GetAwaiter().GetResult());
    }

    [Fact]
    public async Task FailedLoad_RemovesEntry()
    {
        var cache = NewCache();
        var gate = new TaskCompletionSource<string>();
        Task<string> f = cache.Get(1, (k, _) => gate.Task!);

        Assert.NotNull(cache.GetIfPresent(1)); // present while loading
        gate.SetException(new InvalidOperationException("boom"));

        // Wait for the stored future to finish faulting, then for the removal continuation to run.
        await Assert.ThrowsAnyAsync<Exception>(async () => await f);
        await WaitUntil(() => cache.GetIfPresent(1) == null);

        Assert.Null(cache.GetIfPresent(1)); // removed after failure
        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    [Fact]
    public async Task NullLoad_RemovesEntry()
    {
        var cache = NewCache();
        // A synchronous mapping returning null.
        Task<string> f = cache.Get(1, k => null);
        await Assert.ThrowsAnyAsync<Exception>(async () => await f);
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public async Task Put_StoresCompletedFuture()
    {
        var cache = NewCache();
        cache.Put(1, Task.FromResult("v"));
        Task<string>? f = cache.GetIfPresent(1);
        Assert.NotNull(f);
        Assert.Equal("v", await f!);
    }

    [Fact]
    public async Task CompletionResetsExpiryTimer_FromCompletionNotInsert()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();

        var gate = new TaskCompletionSource<string>();
        cache.Get(1, (k, _) => gate.Task!);

        // Loads for a long time — longer than the expiry duration. The in-flight entry must NOT expire.
        ticker.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(cache.GetIfPresent(1));

        // Completion happens at t=5m; the 1-minute timer starts NOW, not from insertion.
        gate.SetResult("v");
        // Wait for the completion continuation (Replace resets the timers) to run.
        Task<string> stored = cache.GetIfPresent(1)!;
        Assert.Equal("v", await stored);
        await WaitUntil(() => cache.Stats().LoadSuccessCount == 1);

        ticker.Advance(TimeSpan.FromSeconds(30)); // 30s after completion
        Assert.NotNull(cache.GetIfPresent(1));    // still valid

        ticker.Advance(TimeSpan.FromSeconds(31)); // 61s after completion
        cache.CleanUp();
        Assert.Null(cache.GetIfPresent(1));       // now expired (from completion time)
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition()) return;
            await Task.Delay(5);
        }
    }

    [Fact]
    public void SlowLoad_PinnedAgainstSizeEviction()
    {
        var cache = Espresso.NewBuilder<int, string>()
            .MaximumSize(10)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();

        var gate = new TaskCompletionSource<string>();
        cache.Get(0, (k, _) => gate.Task!); // in-flight, weight 0 → pinned

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

        var s = cache.Stats();
        Assert.Equal(1, s.LoadSuccessCount);   // not double-counted
        Assert.Equal(0, s.LoadFailureCount);
        Assert.Equal(1, s.MissCount);
    }

    [Fact]
    public async Task Stats_FailedLoad_CountedAsFailureOnly()
    {
        var cache = NewCache();
        var gate = new TaskCompletionSource<string>();
        Task<string> f = cache.Get(1, (k, _) => gate.Task!);
        gate.SetException(new InvalidOperationException("x"));
        await Assert.ThrowsAnyAsync<Exception>(async () => await f);
        await WaitUntil(() => cache.Stats().LoadFailureCount == 1);

        var s = cache.Stats();
        Assert.Equal(0, s.LoadSuccessCount);   // a failed load must NOT record a success
        Assert.Equal(1, s.LoadFailureCount);
    }
}
