using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

public sealed class AsyncLoadingCacheTest
{
    private sealed class CountingLoader : IAsyncCacheLoader<int, string>
    {
        public int LoadCalls;
        public int BulkCalls;
        private readonly Func<int, string?> _fn;
        public CountingLoader(Func<int, string?> fn) => _fn = fn;

        public Task<string?> AsyncLoad(int key, CancellationToken ct)
        {
            Interlocked.Increment(ref LoadCalls);
            return Task.FromResult(_fn(key));
        }

        public Task<IReadOnlyDictionary<int, string>> AsyncLoadAll(
            IReadOnlyCollection<int> keys, CancellationToken ct)
        {
            Interlocked.Increment(ref BulkCalls);
            var result = new Dictionary<int, string>();
            foreach (int k in keys)
            {
                string? v = _fn(k);
                if (v != null) result[k] = v;
            }
            return Task.FromResult<IReadOnlyDictionary<int, string>>(result);
        }
    }

    private static IAsyncLoadingCache<int, string> NewLoading(Func<int, string?> fn, out CountingLoader loader)
    {
        loader = new CountingLoader(fn);
        return Cache.NewBuilder<int, string>()
            .MaximumSize(1000)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .BuildAsync(loader);
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
    public async Task Get_SameKeyTwice_LoadsOnce()
    {
        var cache = NewLoading(k => "v" + k, out var loader);

        Assert.Equal("v1", await cache.Get(1));
        Assert.Equal("v1", await cache.Get(1)); // cached

        Assert.Equal(1, loader.LoadCalls);
    }

    [Fact]
    public async Task GetAll_MissingKeys_BulkLoadedInOneCall()
    {
        var cache = NewLoading(k => "v" + k, out var loader);
        await cache.Get(2);
        int loadsAfterWarmup = loader.LoadCalls;

        IReadOnlyDictionary<int, string> all = await cache.GetAll(new[] { 1, 2, 3, 4 });

        Assert.Equal(4, all.Count);
        Assert.Equal("v1", all[1]);
        Assert.Equal("v3", all[3]);
        // The three missing keys (1,3,4) were bulk-loaded in a single call; key 2 was cached.
        Assert.Equal(1, loader.BulkCalls);
        Assert.Equal(loadsAfterWarmup, loader.LoadCalls); // GetAll used the bulk path, not per-key
    }

    [Fact]
    public async Task GetAll_CachedAndLoadedKeys_ReturnsAllValues()
    {
        var cache = NewLoading(k => "v" + k, out _);
        await cache.Get(1);

        var all = await cache.GetAll(new[] { 1, 2, 3 });

        Assert.Equal(new[] { "v1", "v2", "v3" }, new[] { all[1], all[2], all[3] });
    }

    [Fact]
    public async Task GetAll_LoaderReturnsNull_KeyOmittedAndNotCached()
    {
        // Loader returns null for key 2 → it is not in the result and not cached.
        var cache = NewLoading(k => k == 2 ? null : "v" + k, out _);

        var all = await cache.GetAll(new[] { 1, 2, 3 });

        Assert.False(all.ContainsKey(2));
        Assert.Equal(2, all.Count);
        await WaitUntil(() => cache.GetIfPresent(2) == null);
        Assert.Null(cache.GetIfPresent(2));
    }

    [Fact]
    public async Task GetAll_CoalescedGetOfMissingKey_ResolvesNull_NotThrow()
    {
        // A single Get can coalesce onto a GetAll proxy. If the bulk load omits that key, the awaiter
        // must observe null (not an internal exception).
        var gate = new TaskCompletionSource<IReadOnlyDictionary<int, string>>();
        var loader = new GatedBulkLoader(gate.Task);
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(1000)
            .Executor(DirectExecutor.Instance)
            .BuildAsync(loader);

        Task<IReadOnlyDictionary<int, string>> all = cache.GetAll(new[] { 1, 2 });
        Task<string> single = cache.Get(2); // coalesces onto the in-flight proxy for key 2

        // The bulk load returns only key 1; key 2 is omitted.
        gate.SetResult(new Dictionary<int, string> { [1] = "v1" });
        await all;

        Assert.Null(await single); // resolves to null, does not throw
    }

    private sealed class GatedBulkLoader : IAsyncCacheLoader<int, string>
    {
        private readonly Task<IReadOnlyDictionary<int, string>> _bulk;
        public GatedBulkLoader(Task<IReadOnlyDictionary<int, string>> bulk) => _bulk = bulk;
        public Task<string?> AsyncLoad(int key, CancellationToken ct) => Task.FromResult<string?>("x");
        public Task<IReadOnlyDictionary<int, string>> AsyncLoadAll(
            IReadOnlyCollection<int> keys, CancellationToken ct) => _bulk;
    }

    [Fact]
    public async Task GetAll_EmptyResult_RecordsLoadFailure()
    {
        // A loader that returns an empty (non-null) map for the misses → recorded as a failure.
        var loader = new EmptyBulkLoader();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(1000)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .BuildAsync(loader);

        var all = await cache.GetAll(new[] { 1, 2 });

        Assert.Empty(all);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Equal(0, cache.Stats().LoadSuccessCount);
    }

    private sealed class EmptyBulkLoader : IAsyncCacheLoader<int, string>
    {
        public Task<string?> AsyncLoad(int key, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyDictionary<int, string>> AsyncLoadAll(
            IReadOnlyCollection<int> keys, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }

    [Fact]
    public async Task GetAll_FailingBulkLoad_RemovesProxies()
    {
        var loader = new ThrowingBulkLoader();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(1000)
            .Executor(DirectExecutor.Instance)
            .BuildAsync(loader);

        await Assert.ThrowsAnyAsync<Exception>(async () => await cache.GetAll(new[] { 1, 2, 3 }));

        await WaitUntil(() => cache.GetIfPresent(1) == null);
        Assert.Null(cache.GetIfPresent(1));
        Assert.Null(cache.GetIfPresent(2));
    }

    private sealed class ThrowingBulkLoader : IAsyncCacheLoader<int, string>
    {
        public Task<string?> AsyncLoad(int key, CancellationToken ct) => Task.FromResult<string?>("x");
        public Task<IReadOnlyDictionary<int, string>> AsyncLoadAll(
            IReadOnlyCollection<int> keys, CancellationToken ct)
            => Task.FromException<IReadOnlyDictionary<int, string>>(new InvalidOperationException("bulk fail"));
    }

    [Fact]
    public async Task GetAll_DuplicateKeys_Deduplicated()
    {
        var cache = NewLoading(k => "v" + k, out _);

        var all = await cache.GetAll(new[] { 1, 1, 1 });

        Assert.Single(all);
        Assert.Equal("v1", all[1]);
    }

    private sealed class FakeTicker
    {
        private long _nanos;
        public long Read() => _nanos;
        public void Advance(TimeSpan by) => _nanos += by.Ticks * 100L;
    }

    [Fact]
    public async Task RefreshAfterWrite_ReloadsAsync_AfterThreshold()
    {
        var ticker = new FakeTicker();
        int version = 1;
        var loader = new CountingLoader(k => $"{k}.v{version}");
        var cache = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .BuildAsync(loader);

        Assert.Equal("1.v1", await cache.Get(1));

        // Within the window: no refresh.
        ticker.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("1.v1", await cache.GetIfPresent(1)!);

        // Past the threshold: an access triggers an async reload; the stale value is served on this
        // access, then the reloaded value replaces it.
        version = 2;
        ticker.Advance(TimeSpan.FromSeconds(31)); // 61s since write
        _ = cache.GetIfPresent(1); // triggers the refresh
        await WaitUntil(() => cache.GetIfPresent(1)!.IsCompletedSuccessfully
                              && cache.GetIfPresent(1)!.Result == "1.v2");
        Assert.Equal("1.v2", await cache.GetIfPresent(1)!);
    }

    [Fact]
    public async Task RefreshAfterWrite_CoincidentAccesses_CoalesceIntoSingleReload()
    {
        var ticker = new FakeTicker();
        var loader = new CountingLoader(k => "v");
        var cache = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .BuildAsync(loader);

        await cache.Get(1);
        int afterInitial = loader.LoadCalls;
        ticker.Advance(TimeSpan.FromSeconds(61));

        // Several accesses at the same instant should coalesce into one reload.
        _ = cache.GetIfPresent(1);
        _ = cache.GetIfPresent(1);
        _ = cache.GetIfPresent(1);
        await WaitUntil(() => loader.LoadCalls > afterInitial);
        Assert.Equal(afterInitial + 1, loader.LoadCalls);
    }

    private sealed class FixedExpiry : IExpiry<int, string>
    {
        private readonly long _nanos;
        public FixedExpiry(TimeSpan d) => _nanos = d.Ticks * 100L;
        public long ExpireAfterCreate(int key, string value, long now) => _nanos;
        public long ExpireAfterUpdate(int key, string value, long now, long currentDuration) => _nanos;
        public long ExpireAfterRead(int key, string value, long now, long currentDuration) => currentDuration;
    }

    [Fact]
    public async Task VariableExpiry_InFlightFutureDoesNotExpire_DurationStartsAtCompletion()
    {
        var ticker = new FakeTicker();
        var gate = new TaskCompletionSource<string?>();
        var loader = new GateLoader(gate.Task);
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfter(new FixedExpiry(TimeSpan.FromSeconds(30)))
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .BuildAsync(loader);

        Task<string> pending = cache.Get(1);

        // While in-flight, even a very long advance must not expire the pinned entry.
        ticker.Advance(TimeSpan.FromDays(365));
        Assert.False(pending.IsCompleted);
        Assert.NotNull(cache.GetIfPresent(1)); // future still present (in-flight)

        // Completion stamps the real 30s duration measured from "now" (365d). The completion Replace
        // runs asynchronously, so wait until it has been applied (LoadSuccess recorded after Replace).
        gate.SetResult("v1");
        Assert.Equal("v1", await pending);
        await WaitUntil(() => cache.Stats().LoadSuccessCount == 1);

        // 29s after completion: still alive. 31s: expired under eager maintenance.
        ticker.Advance(TimeSpan.FromSeconds(29));
        Assert.NotNull(cache.GetIfPresent(1));
        ticker.Advance(TimeSpan.FromSeconds(2));
        cache.CleanUp();
        Assert.Null(cache.GetIfPresent(1));
    }

    private sealed class GateLoader : IAsyncCacheLoader<int, string>
    {
        private readonly Task<string?> _gate;
        public GateLoader(Task<string?> gate) => _gate = gate;
        public Task<string?> AsyncLoad(int key, CancellationToken ct) => _gate;
        public Task<IReadOnlyDictionary<int, string>> AsyncLoadAll(
            IReadOnlyCollection<int> keys, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }

    // A loader whose reload can be switched to fault or return null, to test refresh-failure handling.
    private sealed class RefreshLoader : IAsyncCacheLoader<int, string>
    {
        private readonly Func<int, Task<string?>> _fn;
        public RefreshLoader(Func<int, Task<string?>> fn) => _fn = fn;
        public Task<string?> AsyncLoad(int key, CancellationToken ct) => _fn(key);
        public Task<IReadOnlyDictionary<int, string>> AsyncLoadAll(
            IReadOnlyCollection<int> keys, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }

    // Regression (#6): a best-effort async refresh reload that FAILS or resolves to NULL must preserve
    // the last good value — refresh must never remove the entry on a transient reload error.
    [Theory]
    [InlineData(false)] // reload faults
    [InlineData(true)]  // reload resolves to null
    public async Task RefreshAfterWrite_BadReload_PreservesOldValue(bool nullReload)
    {
        var ticker = new FakeTicker();
        bool badReload = false;
        var loader = new RefreshLoader(k => badReload
            ? (nullReload
                ? Task.FromResult<string?>(null)
                : Task.FromException<string?>(new InvalidOperationException("reload boom")))
            : Task.FromResult<string?>("v1"));
        var cache = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .BuildAsync(loader);

        Assert.Equal("v1", await cache.Get(1));

        badReload = true;
        ticker.Advance(TimeSpan.FromSeconds(61));
        _ = cache.GetIfPresent(1); // triggers a reload that faults / resolves to null
        await WaitUntil(() => cache.Stats().LoadFailureCount >= 1);

        // The entry must still be present with the old value (not removed).
        Task<string>? present = cache.GetIfPresent(1);
        Assert.NotNull(present);
        Assert.Equal("v1", await present!);
    }

    // Regression (#6): a reload that is genuinely in-flight and THEN faults (completing after it was
    // installed as the value) must revert the entry to the old value via the completion handler.
    [Fact]
    public async Task RefreshAfterWrite_InFlightReloadThenFails_PreservesOldValue()
    {
        var ticker = new FakeTicker();
        var gate = new TaskCompletionSource<string?>();
        bool gatedReload = false;
        var loader = new RefreshLoader(k => gatedReload ? gate.Task : Task.FromResult<string?>("v1"));
        var cache = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .BuildAsync(loader);

        Assert.Equal("v1", await cache.Get(1));

        gatedReload = true;
        ticker.Advance(TimeSpan.FromSeconds(61));
        _ = cache.GetIfPresent(1); // installs an in-flight reload future as the value

        // Now fail the in-flight reload; the completion must revert to the old value.
        gate.SetException(new InvalidOperationException("late boom"));
        await WaitUntil(() => cache.Stats().LoadFailureCount >= 1);

        Task<string>? present = cache.GetIfPresent(1);
        Assert.NotNull(present);
        Assert.Equal("v1", await present!);
    }
}
