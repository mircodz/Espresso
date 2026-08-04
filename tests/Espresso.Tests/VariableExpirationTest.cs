using System;
using Xunit;

namespace Espresso.Tests;

public sealed class VariableExpirationTest
{
    private sealed class FakeTicker
    {
        private long _nanos;
        public long Read() => _nanos;
        public void Advance(TimeSpan by) => _nanos += by.Ticks * 100L;
    }

    private sealed class ControllableExpiry : IExpiry<int, string>
    {
        public long CreateNanos = TimeSpan.FromMinutes(1).Ticks * 100L;
        public long? UpdateNanos;
        public long? ReadNanos;

        public long ExpireAfterCreate(int key, string value, long currentTime) => CreateNanos;
        public long ExpireAfterUpdate(int key, string value, long currentTime, long currentDuration)
            => UpdateNanos ?? currentDuration;
        public long ExpireAfterRead(int key, string value, long currentTime, long currentDuration)
            => ReadNanos ?? currentDuration;
    }

    private sealed class Listener : IRemovalListener<int, string>
    {
        private readonly Action<int, string?, RemovalCause> _onRemoval;
        public Listener(Action<int, string?, RemovalCause> onRemoval) => _onRemoval = onRemoval;
        public void OnRemoval(int key, string? value, RemovalCause cause) => _onRemoval(key, value, cause);
    }

    private static ICache<int, string> New(IExpiry<int, string> expiry, FakeTicker ticker)
        => Cache.NewBuilder<int, string>()
            .ExpireAfter(expiry)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .Build();

    [Fact]
    public void ExpiresAfterCreateDuration()
    {
        var ticker = new FakeTicker();
        var cache = New(new ControllableExpiry { CreateNanos = TimeSpan.FromMinutes(1).Ticks * 100L }, ticker);
        cache.Put(1, "a");
        Assert.Equal("a", cache.GetIfPresent(1));

        ticker.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal("a", cache.GetIfPresent(1));

        ticker.Advance(TimeSpan.FromSeconds(2)); // 61s > 60s
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public void ReadExtendsDuration()
    {
        var ticker = new FakeTicker();
        var expiry = new ControllableExpiry
        {
            CreateNanos = TimeSpan.FromMinutes(1).Ticks * 100L,
            ReadNanos = TimeSpan.FromMinutes(1).Ticks * 100L,
        };
        var cache = New(expiry, ticker);
        cache.Put(1, "a");

        // Read at 30s resets the duration to another 60s (expires at 90s instead of 60s).
        ticker.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("a", cache.GetIfPresent(1));

        // At 65s the original 60s lifetime would have expired; the read extended it. Use eager
        // maintenance (which does not extend) to confirm the entry survives.
        ticker.Advance(TimeSpan.FromSeconds(35));
        cache.CleanUp();
        Assert.Equal(1, cache.EstimatedSize());

        // Past the extended 90s deadline it expires under eager maintenance.
        ticker.Advance(TimeSpan.FromSeconds(30));
        cache.CleanUp();
        Assert.Equal(0, cache.EstimatedSize());
    }

    [Fact]
    public void UpdateChangesDuration()
    {
        var ticker = new FakeTicker();
        var expiry = new ControllableExpiry
        {
            CreateNanos = TimeSpan.FromMinutes(1).Ticks * 100L,
            UpdateNanos = TimeSpan.FromSeconds(10).Ticks * 100L,
        };
        var cache = New(expiry, ticker);
        cache.Put(1, "a");

        ticker.Advance(TimeSpan.FromSeconds(5));
        cache.Put(1, "b"); // update resets to a 10s lifetime

        ticker.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal("b", cache.GetIfPresent(1)); // 14s since create, 9s since update

        ticker.Advance(TimeSpan.FromSeconds(2)); // 11s since update > 10s
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public void MaxValueDurationNeverExpires()
    {
        var ticker = new FakeTicker();
        var cache = New(new ControllableExpiry { CreateNanos = long.MaxValue }, ticker);
        cache.Put(1, "a");

        ticker.Advance(TimeSpan.FromDays(365 * 100));
        Assert.Equal("a", cache.GetIfPresent(1));
    }

    [Fact]
    public void ExpiredEntryFiresRemovalWithExpiredCause()
    {
        var ticker = new FakeTicker();
        RemovalCause? cause = null;
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfter(new ControllableExpiry { CreateNanos = TimeSpan.FromSeconds(10).Ticks * 100L })
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .RemovalListener(new Listener((k, v, c) => cause = c))
            .Build();

        cache.Put(1, "a");
        ticker.Advance(TimeSpan.FromSeconds(11));
        cache.CleanUp(); // eager maintenance advances the timer wheel

        Assert.Null(cache.GetIfPresent(1));
        Assert.Equal(RemovalCause.Expired, cause);
    }

    [Fact]
    public void CombinedWithMaximumSize()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfter(new ControllableExpiry { CreateNanos = TimeSpan.FromMinutes(10).Ticks * 100L })
            .MaximumSize(10)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build();

        for (int i = 0; i < 100; i++)
        {
            cache.Put(i, "v" + i);
        }
        cache.CleanUp();
        Assert.True(cache.EstimatedSize() <= 10, $"size {cache.EstimatedSize()} should be bounded by 10");
    }

    [Fact]
    public void EagerMaintenanceEvictsWithoutAccess()
    {
        var ticker = new FakeTicker();
        var cache = New(new ControllableExpiry { CreateNanos = TimeSpan.FromSeconds(5).Ticks * 100L }, ticker);
        cache.Put(1, "a");
        cache.Put(2, "b");

        ticker.Advance(TimeSpan.FromSeconds(6));
        cache.CleanUp(); // no per-key access; the timer wheel must expire both

        Assert.Equal(0, cache.EstimatedSize());
    }

    [Fact]
    public void FuncExpiry_UniformDuration()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfter(new FuncExpiry<int, string>((_, _) => TimeSpan.FromSeconds(30)))
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        ticker.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal("a", cache.GetIfPresent(1));
        ticker.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public void CannotCombineExpireAfterWithFixedExpiry()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Cache.NewBuilder<int, string>()
                .ExpireAfterWrite(TimeSpan.FromMinutes(1))
                .ExpireAfter(new FuncExpiry<int, string>((_, _) => TimeSpan.FromSeconds(30))));
    }

    [Fact]
    public async System.Threading.Tasks.Task Scheduler_ProactivelyEvictsWithoutAccess()
    {
        // With a real scheduler, an expired entry is evicted by a background maintenance tick even
        // though nothing ever touches the cache after the write. Uses the system clock (not a fake
        // ticker) so the pacer's timer fires against wall-clock time.
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfter(new FuncExpiry<int, string>((_, _) => TimeSpan.FromMilliseconds(50)))
            .Scheduler(Schedulers.System)
            .Build();

        cache.Put(1, "a");
        Assert.Equal(1, cache.EstimatedSize());

        // Do NOT access the entry; the pacer must drive its removal on its own.
        for (int i = 0; i < 100 && cache.EstimatedSize() > 0; i++)
        {
            await System.Threading.Tasks.Task.Delay(50);
        }
        Assert.Equal(0, cache.EstimatedSize());
    }

    // Regression (Finding 1): if the user IExpiry throws on an update, the node must be left
    // untouched — the value/weight must NOT be mutated before the (throwing) expiry callback runs,
    // otherwise the entry keeps the new value with stale accounting. Espresso computes the expiry
    // time first, so the throw leaves the prior value intact.
    [Fact]
    public void ThrowingExpiryOnUpdate_LeavesEntryUnchanged()
    {
        var ticker = new FakeTicker();
        bool throwOnUpdate = false;
        var expiry = new ThrowingExpiry(() => throwOnUpdate);
        var cache = New(expiry, ticker);

        cache.Put(1, "original");
        Assert.Equal("original", cache.GetIfPresent(1));

        throwOnUpdate = true;
        Assert.Throws<InvalidOperationException>(() => cache.Put(1, "replacement"));

        // The failed update must not have committed the new value.
        Assert.Equal("original", cache.GetIfPresent(1));
    }

    private sealed class ThrowingExpiry : IExpiry<int, string>
    {
        private readonly Func<bool> _throwOnUpdate;
        public ThrowingExpiry(Func<bool> throwOnUpdate) => _throwOnUpdate = throwOnUpdate;
        public long ExpireAfterCreate(int key, string value, long now)
            => TimeSpan.FromMinutes(10).Ticks * 100L;
        public long ExpireAfterUpdate(int key, string value, long now, long currentDuration)
            => _throwOnUpdate() ? throw new InvalidOperationException("expiry boom") : currentDuration;
        public long ExpireAfterRead(int key, string value, long now, long currentDuration)
            => currentDuration;
    }
}
