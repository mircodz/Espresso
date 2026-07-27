using System;
using System.Threading;
using Xunit;

namespace Espresso.Tests;

public sealed class RefreshAfterWriteTest
{
    private sealed class FakeTicker : ITicker
    {
        private long _nanos;
        public long Read() => _nanos;
        public void Advance(TimeSpan by) => _nanos += by.Ticks * 100L;
    }

    private sealed class CountingLoader : ICacheLoader<int, string>
    {
        public int Calls;
        private readonly Func<int, string?> _fn;
        public CountingLoader(Func<int, string?> fn) => _fn = fn;
        public string? Load(int key) { Interlocked.Increment(ref Calls); return _fn(key); }
    }

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    [Fact]
    public void Refresh_ReloadsAfterThreshold_OnAccess()
    {
        var ticker = new FakeTicker();
        int version = 1;
        var loader = new CountingLoader(k => $"{k}-v{version}");
        var cache = Espresso.NewBuilder<int, string>()
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance) // background reload runs inline
            .Build(loader);

        Assert.Equal("1-v1", cache.Get(1));
        Assert.Equal(1, loader.Calls);

        // Within the window: no refresh.
        ticker.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("1-v1", cache.GetIfPresent(1));
        Assert.Equal(1, loader.Calls);

        // Past the window: the access triggers a reload; the new value replaces the old.
        version = 2;
        ticker.Advance(TimeSpan.FromSeconds(31)); // 61s since write
        cache.GetIfPresent(1); // triggers refresh (returns the stale value on this call)
        Assert.Equal(2, loader.Calls);
        Assert.Equal("1-v2", cache.GetIfPresent(1)); // now the refreshed value
    }

    [Fact]
    public void Refresh_ServesStaleValueDuringReload()
    {
        var ticker = new FakeTicker();
        var loader = new CountingLoader(k => "new");
        var cache = Espresso.NewBuilder<int, string>()
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build(loader);

        cache.Put(1, "old");
        ticker.Advance(TimeSpan.FromSeconds(61));
        // The triggering access still returns the old value.
        Assert.Equal("old", cache.GetIfPresent(1));
    }

    [Fact]
    public void Refresh_NotTriggeredWithinWindow()
    {
        var ticker = new FakeTicker();
        var loader = new CountingLoader(k => "reloaded");
        var cache = Espresso.NewBuilder<int, string>()
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build(loader);

        cache.Put(1, "a");
        for (int i = 0; i < 5; i++)
        {
            ticker.Advance(TimeSpan.FromSeconds(10));
            cache.GetIfPresent(1);
        }
        Assert.Equal(0, loader.Calls); // 50s < 60s, never refreshed
    }

    [Fact]
    public void Refresh_Debounced_SingleReloadPerWindow()
    {
        var ticker = new FakeTicker();
        var loader = new CountingLoader(k => "v");
        var cache = Espresso.NewBuilder<int, string>()
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build(loader);

        cache.Put(1, "a");
        ticker.Advance(TimeSpan.FromSeconds(61));
        // Multiple accesses at the same instant should coalesce into one reload.
        cache.GetIfPresent(1);
        cache.GetIfPresent(1);
        cache.GetIfPresent(1);
        Assert.Equal(1, loader.Calls);
    }

    [Fact]
    public void Refresh_FailedReload_RemovesEntry()
    {
        var ticker = new FakeTicker();
        var loader = new CountingLoader(k => null); // reload yields nothing
        var cache = Espresso.NewBuilder<int, string>()
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build(loader);

        cache.Put(1, "old");
        ticker.Advance(TimeSpan.FromSeconds(61));
        cache.GetIfPresent(1);
        // A null reload removes the entry (does not keep the stale value).
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public void Refresh_WithMaximumSize_Coexist()
    {
        var ticker = new FakeTicker();
        int version = 1;
        var loader = new CountingLoader(k => $"{k}.{version}");
        var cache = Espresso.NewBuilder<int, string>()
            .MaximumSize(100)
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build(loader);

        Assert.Equal("1.1", cache.Get(1));
        version = 2;
        ticker.Advance(TimeSpan.FromSeconds(61));
        cache.GetIfPresent(1);
        Assert.Equal("1.2", cache.GetIfPresent(1));
    }

    // Regression: refreshAfterWrite must fire on the LoadingCache.Get path, not only GetIfPresent.
    // Previously AfterRead did not call RefreshIfNeeded, so Get() (routed through ComputeIfAbsent)
    // never refreshed a hot present key.
    [Fact]
    public void Refresh_TriggeredVia_LoadingCacheGet()
    {
        var ticker = new FakeTicker();
        int version = 1;
        var loader = new CountingLoader(k => $"{k}-v{version}");
        var cache = Espresso.NewBuilder<int, string>()
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build(loader);

        Assert.Equal("1-v1", cache.Get(1));
        Assert.Equal(1, loader.Calls);

        version = 2;
        ticker.Advance(TimeSpan.FromSeconds(61));
        cache.Get(1); // triggers reload (runs inline via DirectExecutor)
        Assert.Equal(2, loader.Calls);
        Assert.Equal("1-v2", cache.Get(1)); // reloaded value now visible
    }

    // Regression: a reload loader that THROWS must preserve the existing (stale) value — refresh is
    // best-effort. Only a null reload removes the entry.
    [Fact]
    public void Refresh_ThrowingReload_PreservesOldValue()
    {
        var ticker = new FakeTicker();
        bool shouldThrow = false;
        var loader = new CountingLoader(k => shouldThrow ? throw new InvalidOperationException("boom") : "v1");
        var cache = Espresso.NewBuilder<int, string>()
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .Build(loader);

        Assert.Equal("v1", cache.Get(1));
        shouldThrow = true;
        ticker.Advance(TimeSpan.FromSeconds(61));
        cache.GetIfPresent(1); // triggers a reload that throws

        Assert.Equal("v1", cache.GetIfPresent(1)); // stale value preserved
        Assert.True(cache.Stats().LoadFailureCount >= 1);
    }

    // Regression: a weighted sync refresh that changes the entry weight must propagate the weight
    // delta to the policy (previously RefreshSync never enqueued an UpdateTask -> weightedSize drift,
    // which would break the weight bound). Behavioral check: the cache stays within its weight bound.
    [Fact]
    public void Refresh_WeightedReload_KeepsWeightBound()
    {
        var ticker = new FakeTicker();
        // Each key refreshes to a heavier value than it started with.
        int version = 1;
        var loader = new CountingLoader(k => version == 1 ? "x" : "xxxxxxxxxx"); // 1 -> 10
        var cache = Espresso.NewBuilder<int, string>()
            .MaximumWeight(40)
            .Weigher(new FuncWeigher<int, string>((_, v) => v.Length))
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build(loader);

        for (int i = 0; i < 10; i++) { cache.Get(i); } // weight 1 each
        version = 2;
        ticker.Advance(TimeSpan.FromSeconds(61));
        for (int i = 0; i < 10; i++) { cache.Get(i); cache.Get(i); } // trigger + observe reloads
        cache.CleanUp();

        // If the weight delta were lost, weightedSize would under-count and the bound would blow past
        // 40. Summing actual entry weights must respect the configured bound.
        long totalWeight = 0;
        for (int i = 0; i < 10; i++)
        {
            string? v = cache.GetIfPresent(i);
            if (v != null) totalWeight += v.Length;
        }
        Assert.True(totalWeight <= 40, $"live weight {totalWeight} exceeded bound 40 (weightedSize drift)");
    }

    // Regression: when an EXPIRED entry is refreshed in place on the loader path, the old value's
    // expiration must fire a removal notification with cause Expired (and record the eviction).
    [Fact]
    public void ExpiredEntry_ReplacedOnGet_FiresExpiredNotification()
    {
        var ticker = new FakeTicker();
        RemovalCause? cause = null;
        string? removedValue = null;
        int version = 1;
        var loader = new CountingLoader(k => $"v{version}");
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .RemovalListener(new CaptureListener((k, v, c) => { cause = c; removedValue = v; }))
            .Build(loader);

        Assert.Equal("v1", cache.Get(1));
        version = 2;
        ticker.Advance(TimeSpan.FromSeconds(61)); // entry now expired
        Assert.Equal("v2", cache.Get(1));         // expired entry replaced in place

        Assert.Equal(RemovalCause.Expired, cause);
        Assert.Equal("v1", removedValue);
    }

    private sealed class CaptureListener : IRemovalListener<int, string>
    {
        private readonly Action<int, string?, RemovalCause> _on;
        public CaptureListener(Action<int, string?, RemovalCause> on) => _on = on;
        public void OnRemoval(int key, string? value, RemovalCause cause) => _on(key, value, cause);
    }

    // Regression: on a refresh-only cache (no expireAfterWrite), a Put over an existing key must reset
    // the write time so the refresh window is measured from that write. Previously the put path gated
    // the write-time reset on expireAfterWrite only, leaving it stale and mis-timing the next refresh.
    [Fact]
    public void PutOverExisting_ResetsRefreshWindow()
    {
        var ticker = new FakeTicker();
        int version = 1;
        var loader = new CountingLoader(k => $"{k}-v{version}");
        var cache = Espresso.NewBuilder<int, string>()
            .RefreshAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build(loader);

        cache.Put(1, "manual");
        // Advance almost a full window, then overwrite: the write time must reset here.
        ticker.Advance(TimeSpan.FromSeconds(59));
        cache.Put(1, "manual2");

        // 30s after the second put (89s since the first) — within the window measured from the second
        // put, so no refresh fires and the manual value stands.
        version = 2;
        ticker.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("manual2", cache.GetIfPresent(1));
        Assert.Equal(0, loader.Calls);

        // Past the window from the second put: now a refresh triggers.
        ticker.Advance(TimeSpan.FromSeconds(31)); // 61s since the second put
        cache.GetIfPresent(1);
        Assert.Equal(1, loader.Calls);
    }
}
