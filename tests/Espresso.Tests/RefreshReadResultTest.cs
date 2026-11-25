using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// A read that triggers a refresh returns the refreshed value when the reload completes inline (as in
/// Caffeine, where <c>afterRead</c> returns <c>Async.getIfReady</c> of the refresh): otherwise (still
/// in flight, failed, or resolved to null) it returns the value it read.
/// </summary>
public sealed class RefreshReadResultTest
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    /// <summary>A manual clock; scripted readings are returned (in order) before the current time.</summary>
    private sealed class ScriptedTicker
    {
        private long _nanos;
        private readonly Queue<long> _script = new();
        public long Read() => _script.Count > 0 ? _script.Dequeue() : Interlocked.Read(ref _nanos);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _nanos, by.Ticks * 100L);
        public void Script(TimeSpan at) => _script.Enqueue(at.Ticks * 100L);
    }

    private sealed class VersionedLoader : ICacheLoader<int, string>
    {
        public int Version = 1;
        public int Calls;
        public Func<int, string?>? Override;
        public string? Load(int key)
        {
            Interlocked.Increment(ref Calls);
            return Override != null ? Override(key) : $"{key}-v{Version}";
        }
    }

    private sealed class BulkVersionedLoader : ICacheLoader<int, string>
    {
        public int Version = 1;
        public string? Load(int key) => $"{key}-v{Version}";
        public IReadOnlyDictionary<int, string> LoadAll(IEnumerable<int> keys)
        {
            var result = new Dictionary<int, string>();
            foreach (int key in keys)
            {
                result[key] = $"{key}-v{Version}";
            }
            return result;
        }
    }

    private sealed class DeferredExecutor : IExecutor
    {
        public Action? Pending;
        public void Execute(Action command) => Pending = command;
    }

    private static ILoadingCache<int, string> Build<TLoader>(TLoader loader, ScriptedTicker ticker,
        IExecutor? executor = null, TimeSpan? expireAfterWrite = null)
        where TLoader : ICacheLoader<int, string>
    {
        var builder = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(executor ?? DirectExecutor.Instance)
            .RecordStats();
        if (expireAfterWrite is TimeSpan duration)
        {
            builder = builder.ExpireAfterWrite(duration);
        }
        return builder.Build(loader);
    }

    [Fact]
    public void GetIfPresent_RefreshCompletingInline_ReturnsTheRefreshedValue()
    {
        var ticker = new ScriptedTicker();
        var loader = new VersionedLoader();
        var cache = Build(loader, ticker);
        Assert.Equal("1-v1", cache.Get(1));

        loader.Version = 2;
        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("1-v2", cache.GetIfPresent(1));
        Assert.Equal(2, loader.Calls);
        Assert.Equal(1, cache.Stats().HitCount);
    }

    [Fact]
    public void LoadingGet_RefreshCompletingInline_ReturnsTheRefreshedValue()
    {
        var ticker = new ScriptedTicker();
        var loader = new VersionedLoader();
        var cache = Build(loader, ticker);
        Assert.Equal("1-v1", cache.Get(1));

        loader.Version = 2;
        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("1-v2", cache.Get(1));
        Assert.Equal(2, loader.Calls);
    }

    [Fact]
    public void GetWithMapping_RefreshCompletingInline_ReturnsTheRefreshedValue()
    {
        var ticker = new ScriptedTicker();
        var loader = new VersionedLoader();
        var cache = Build(loader, ticker);
        Assert.Equal("1-v1", cache.Get(1));

        loader.Version = 2;
        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("1-v2", cache.Get(1, _ => throw new InvalidOperationException("not a miss")));
    }

    // The fast path sees the entry expired (the first clock reading), the locked re-check sees it live
    // (the second): the contended hit path must also return the refreshed value.
    [Fact]
    public void GetWithMapping_ContendedHit_RefreshCompletingInline_ReturnsTheRefreshedValue()
    {
        var ticker = new ScriptedTicker();
        var loader = new VersionedLoader();
        var cache = Build(loader, ticker, expireAfterWrite: TimeSpan.FromMinutes(10));
        Assert.Equal("1-v1", cache.Get(1));

        loader.Version = 2;
        ticker.Advance(TimeSpan.FromMinutes(2));
        ticker.Script(TimeSpan.FromMinutes(11));
        int mappings = 0;
        Assert.Equal("1-v2", cache.Get(1, _ => { mappings++; return "computed"; }));
        Assert.Equal(0, mappings);
        Assert.Equal(2, loader.Calls);
        Assert.Equal(1, cache.Stats().HitCount);
    }

    [Fact]
    public void GetAllPresent_RefreshCompletingInline_ReturnsTheRefreshedValues()
    {
        var ticker = new ScriptedTicker();
        var loader = new VersionedLoader();
        var cache = Build(loader, ticker);
        Assert.Equal("1-v1", cache.Get(1));
        Assert.Equal("2-v1", cache.Get(2));

        loader.Version = 2;
        ticker.Advance(TimeSpan.FromSeconds(61));
        IReadOnlyDictionary<int, string> present = cache.GetAllPresent(new[] { 1, 2, 3 });
        Assert.Equal(new Dictionary<int, string> { [1] = "1-v2", [2] = "2-v2" }, present);
    }

    [Fact]
    public void GetAll_RefreshCompletingInline_ReturnsTheRefreshedValues()
    {
        var ticker = new ScriptedTicker();
        var loader = new VersionedLoader();
        var cache = Build(loader, ticker);
        Assert.Equal("1-v1", cache.Get(1));

        loader.Version = 2;
        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("1-v2", cache.GetAll(new[] { 1 })[1]);
    }

    [Fact]
    public void BulkGetAll_RefreshCompletingInline_ReturnsTheRefreshedValues()
    {
        var ticker = new ScriptedTicker();
        var loader = new BulkVersionedLoader();
        var cache = Build(loader, ticker);
        Assert.Equal("1-v1", cache.Get(1));

        loader.Version = 2;
        ticker.Advance(TimeSpan.FromSeconds(61));
        IReadOnlyDictionary<int, string> all = cache.GetAll(new[] { 1, 2 });
        Assert.Equal(new Dictionary<int, string> { [1] = "1-v2", [2] = "2-v2" }, all);
    }

    [Fact]
    public void Read_RefreshInFlight_ReturnsTheCurrentValue()
    {
        var ticker = new ScriptedTicker();
        var loader = new VersionedLoader();
        var executor = new DeferredExecutor();
        var cache = Build(loader, ticker, executor);
        cache.Put(1, "old");

        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("old", cache.GetIfPresent(1));
        Assert.NotNull(executor.Pending);

        executor.Pending!();
        Assert.Equal("1-v1", cache.GetIfPresent(1));
    }

    [Fact]
    public void Read_RefreshResolvingToNull_ReturnsTheValueItRead()
    {
        var ticker = new ScriptedTicker();
        var loader = new VersionedLoader { Override = _ => null };
        var cache = Build(loader, ticker);
        cache.Put(1, "old");

        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("old", cache.GetIfPresent(1));
        Assert.Null(cache.GetIfPresent(1)); // the null reload removed the entry
    }

    [Fact]
    public void Read_RefreshThrowing_ReturnsTheValueItRead()
    {
        var ticker = new ScriptedTicker();
        var loader = new VersionedLoader { Override = _ => throw new InvalidOperationException("reload failed") };
        var cache = Build(loader, ticker);
        cache.Put(1, "old");

        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("old", cache.GetIfPresent(1));
        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    // ----- async -----

    private sealed class AsyncLoader : IAsyncCacheLoader<int, string>
    {
        public Func<int, string, Task<string?>> Reload = (k, _) => Task.FromResult<string?>($"{k}-v2");

        public Task<string?> AsyncLoad(int key, CancellationToken cancellationToken)
            => Task.FromResult<string?>($"{key}-v1");

        public Task<string?> AsyncReload(int key, string oldValue, CancellationToken cancellationToken)
            => Reload(key, oldValue);
    }

    private static IAsyncLoadingCache<int, string> BuildAsync(AsyncLoader loader, ScriptedTicker ticker)
        => Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .BuildAsync(loader);

    [Fact]
    public async Task Async_RefreshCompletingInline_ReturnsTheRefreshedFuture()
    {
        var ticker = new ScriptedTicker();
        var cache = BuildAsync(new AsyncLoader(), ticker);
        Assert.Equal("1-v1", await cache.Get(1));
        Assert.Equal("2-v1", await cache.Get(2));

        ticker.Advance(TimeSpan.FromSeconds(61));
        var present = cache.GetIfPresent(1);
        Assert.NotNull(present);
        Assert.True(present!.IsCompletedSuccessfully);
        Assert.Equal("1-v2", await present);

        var loaded = cache.Get(2);
        Assert.True(loaded.IsCompletedSuccessfully);
        Assert.Equal("2-v2", await loaded);
    }

    [Fact]
    public async Task Async_RefreshInFlight_ReturnsTheCurrentFuture()
    {
        var ticker = new ScriptedTicker();
        var gate = new TaskCompletionSource<string?>();
        var cache = BuildAsync(new AsyncLoader { Reload = (_, _) => gate.Task }, ticker);
        var original = cache.Get(1);
        Assert.Equal("1-v1", await original);

        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Same(original, cache.GetIfPresent(1));

        gate.SetResult("1-v2");
        Assert.Equal("1-v2", await cache.GetIfPresent(1)!);
    }

    [Fact]
    public async Task Async_RefreshFailingInline_ReturnsTheCurrentFuture()
    {
        var ticker = new ScriptedTicker();
        var cache = BuildAsync(new AsyncLoader
        {
            Reload = (_, _) => Task.FromException<string?>(new InvalidOperationException("reload failed")),
        }, ticker);
        var original = cache.Get(1);
        Assert.Equal("1-v1", await original);

        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Same(original, cache.GetIfPresent(1));
    }
}
