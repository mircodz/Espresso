using Espresso.Stats;
using Xunit;

namespace Espresso.Tests;

public sealed class StatsTest
{
    [Fact]
    public void Empty_HasZeroCounts()
    {
        var stats = CacheStats.Empty;
        Assert.Equal(0, stats.RequestCount);
        Assert.Equal(0, stats.HitCount);
        Assert.Equal(0, stats.MissCount);
        Assert.Equal(1.0, stats.HitRate);  // 1.0 when no requests
        Assert.Equal(0.0, stats.MissRate);
    }

    [Fact]
    public void Rates_Computed()
    {
        var stats = new CacheStats(hitCount: 3, missCount: 1, 0, 0, 0, 0, 0);
        Assert.Equal(4, stats.RequestCount);
        Assert.Equal(0.75, stats.HitRate);
        Assert.Equal(0.25, stats.MissRate);
    }

    [Fact]
    public void AverageLoadPenalty()
    {
        var stats = new CacheStats(0, 0, loadSuccessCount: 2, loadFailureCount: 2, totalLoadTime: 400, 0, 0);
        Assert.Equal(4, stats.LoadCount);
        Assert.Equal(100.0, stats.AverageLoadPenalty);
        Assert.Equal(0.5, stats.LoadFailureRate);
    }

    [Fact]
    public void Plus_And_Minus()
    {
        var a = new CacheStats(10, 5, 3, 2, 100, 1, 7);
        var b = new CacheStats(4, 1, 1, 0, 40, 1, 3);
        var sum = a.Plus(b);
        Assert.Equal(14, sum.HitCount);
        Assert.Equal(6, sum.MissCount);
        Assert.Equal(140, sum.TotalLoadTime);

        var diff = a.Minus(b);
        Assert.Equal(6, diff.HitCount);
        Assert.Equal(4, diff.MissCount);
        Assert.Equal(4, diff.EvictionWeight);
    }

    [Fact]
    public void Minus_FloorsAtZero()
    {
        var a = new CacheStats(1, 0, 0, 0, 0, 0, 0);
        var b = new CacheStats(5, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, a.Minus(b).HitCount);
    }

    [Fact]
    public void ConcurrentStatsCounter_RecordsEverything()
    {
        var counter = StatsCounter.CreateEnabled();
        counter.RecordHits(3);
        counter.RecordMisses(2);
        counter.RecordLoadSuccess(50);
        counter.RecordLoadFailure(30);
        counter.RecordEviction(4, RemovalCause.Size);

        var stats = counter.Snapshot();
        Assert.Equal(3, stats.HitCount);
        Assert.Equal(2, stats.MissCount);
        Assert.Equal(1, stats.LoadSuccessCount);
        Assert.Equal(1, stats.LoadFailureCount);
        Assert.Equal(80, stats.TotalLoadTime);
        Assert.Equal(1, stats.EvictionCount);
        Assert.Equal(4, stats.EvictionWeight);
    }

    [Fact]
    public void DisabledStatsCounter_RecordsNothing()
    {
        var counter = StatsCounter.Disabled;
        counter.RecordHits(5);
        counter.RecordMisses(5);
        counter.RecordLoadSuccess(100);
        Assert.Same(CacheStats.Empty, counter.Snapshot());
    }

    [Fact]
    public void WasEvicted_ByCause()
    {
        Assert.False(RemovalCause.Explicit.WasEvicted());
        Assert.False(RemovalCause.Replaced.WasEvicted());
        Assert.True(RemovalCause.Collected.WasEvicted());
        Assert.True(RemovalCause.Expired.WasEvicted());
        Assert.True(RemovalCause.Size.WasEvicted());
    }
}
