using System;
using Xunit;

namespace Espresso.Tests;

public sealed class ExpirationTest
{
    private sealed class FakeTicker : ITicker
    {
        private long _nanos;
        public long Read() => _nanos;
        public void Advance(TimeSpan by) => _nanos += by.Ticks * 100L;
    }

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    [Fact]
    public void ExpireAfterWrite_EvictsAfterDuration()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .Build();

        cache.Put(1, "a");
        Assert.Equal("a", cache.GetIfPresent(1));

        ticker.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("a", cache.GetIfPresent(1)); // not yet expired

        ticker.Advance(TimeSpan.FromSeconds(31)); // total 61s > 60s
        Assert.Null(cache.GetIfPresent(1));       // logically expired -> absent
        cache.CleanUp();
        Assert.Equal(0, cache.EstimatedSize());   // physically evicted
    }

    [Fact]
    public void ExpireAfterWrite_NotResetByReads()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        for (int i = 0; i < 5; i++)
        {
            ticker.Advance(TimeSpan.FromSeconds(15));
            cache.GetIfPresent(1); // reads must NOT extend a write-based expiry
        }
        // 75s elapsed since write -> expired despite the reads.
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public void ExpireAfterWrite_ResetByUpdate()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        ticker.Advance(TimeSpan.FromSeconds(45));
        cache.Put(1, "b"); // re-write resets the clock
        ticker.Advance(TimeSpan.FromSeconds(45)); // 45s since the update
        Assert.Equal("b", cache.GetIfPresent(1));
    }

    [Fact]
    public void ExpireAfterAccess_ResetByReads()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterAccess(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        for (int i = 0; i < 5; i++)
        {
            ticker.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal("a", cache.GetIfPresent(1)); // each read extends the access expiry
        }
        // Now idle past the duration.
        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public void ExpireAfterAccess_EvictsIdleEntries()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterAccess(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        ticker.Advance(TimeSpan.FromSeconds(61));
        cache.CleanUp();
        Assert.Equal(0, cache.EstimatedSize());
    }

    [Fact]
    public void Expiration_NotifiesExpiredCause()
    {
        var ticker = new FakeTicker();
        RemovalCause? observed = null;
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .RemovalListener(new Listener((_, _, cause) => observed = cause))
            .Build();

        cache.Put(1, "a");
        ticker.Advance(TimeSpan.FromSeconds(61));
        cache.CleanUp();
        Assert.Equal(RemovalCause.Expired, observed);
    }

    [Fact]
    public void Expiration_WithMaximumSize_BothApply()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .MaximumSize(100)
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build();

        for (int i = 0; i < 50; i++)
        {
            cache.Put(i, "v" + i);
        }
        Assert.Equal("v10", cache.GetIfPresent(10));

        ticker.Advance(TimeSpan.FromSeconds(61));
        cache.CleanUp();
        Assert.Equal(0, cache.EstimatedSize()); // all expired despite being under the size bound
    }

    [Fact]
    public void ExpireAfterWrite_ReAddAfterExpiry()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(cache.GetIfPresent(1));

        cache.Put(1, "b"); // re-insert after expiry
        Assert.Equal("b", cache.GetIfPresent(1));
    }

    [Fact]
    public void Get_RecomputesExpiredEntry()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build();

        int calls = 0;
        Assert.Equal("v1", cache.Get(1, k => { calls++; return "v" + calls; }));
        ticker.Advance(TimeSpan.FromSeconds(61));
        // Expired -> the mapping function runs again.
        Assert.Equal("v2", cache.Get(1, k => { calls++; return "v" + calls; }));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Put_OverExpiredEntry_ReturnsNull_NotStale()
    {
        var ticker = new FakeTicker();
        RemovalCause? cause = null;
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .RemovalListener(new Listener((_, _, c) => cause = c))
            .Build();

        cache.Put(1, "old");
        ticker.Advance(TimeSpan.FromSeconds(61)); // expired
        string? returned = ((ICache<int, string>)cache).GetIfPresent(1); // sanity: absent
        Assert.Null(returned);

        // Put over the expired entry.
        cache.Put(1, "new");
        Assert.Equal("new", cache.GetIfPresent(1));
        Assert.NotEqual(RemovalCause.Replaced, cause); // expired, not replaced
    }

    [Fact]
    public void Remove_ExpiredEntry_ReturnsNull()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "old");
        ticker.Advance(TimeSpan.FromSeconds(61));
        cache.Invalidate(1); // just verify no throw and entry gone
        Assert.Null(cache.GetIfPresent(1));
    }

    // Regression: GetAllPresent must honor expiration like every other read path — an expired entry
    // must be a miss (not leaked to the caller, not counted as a hit).
    [Fact]
    public void GetAllPresent_SkipsExpiredEntries()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .Build();

        cache.Put(1, "a");
        cache.Put(2, "b");
        ticker.Advance(TimeSpan.FromSeconds(30));
        cache.Put(3, "c"); // fresher than 1 and 2

        // Age past 1 and 2's expiry (61s) but within 3's (31s since its write).
        ticker.Advance(TimeSpan.FromSeconds(31));

        var present = cache.GetAllPresent(new[] { 1, 2, 3 });
        Assert.False(present.ContainsKey(1), "expired entry 1 leaked");
        Assert.False(present.ContainsKey(2), "expired entry 2 leaked");
        Assert.Equal("c", present[3]);
        Assert.Single(present);

        // Only the live hit is recorded as a hit; the two expired keys are misses.
        Assert.Equal(1, cache.Stats().HitCount);
        Assert.Equal(2, cache.Stats().MissCount);
    }

    // GetAllPresent under expireAfterAccess must extend the entries it reads (not let them die
    // despite being accessed).
    [Fact]
    public void GetAllPresent_ExtendsAccessedEntries()
    {
        var ticker = new FakeTicker();
        var cache = Espresso.NewBuilder<int, string>()
            .ExpireAfterAccess(OneMinute)
            .Ticker(ticker)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        // Read via GetAllPresent every 40s; the access must keep the entry alive past 60s.
        for (int i = 0; i < 3; i++)
        {
            ticker.Advance(TimeSpan.FromSeconds(40));
            var present = cache.GetAllPresent(new[] { 1 });
            Assert.Equal("a", present[1]);
        }
    }

    private sealed class Listener : IRemovalListener<int, string>
    {
        private readonly Action<int, string?, RemovalCause> _onRemoval;
        public Listener(Action<int, string?, RemovalCause> onRemoval) => _onRemoval = onRemoval;
        public void OnRemoval(int key, string? value, RemovalCause cause) => _onRemoval(key, value, cause);
    }
}
