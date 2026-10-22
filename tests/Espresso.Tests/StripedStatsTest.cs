using System.Linq;
using System.Threading.Tasks;
using Espresso.Stats;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Hits and misses are counted in striped cells; every increment must be counted exactly once, whether
/// threads have their own cells or share them.
/// </summary>
public sealed class StripedStatsTest
{
    private const int PerThread = 100_000;
    private static readonly System.TimeSpan Timeout = System.TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(64)] // more threads than cells, so cells are shared
    public async Task ConcurrentIncrements_AreAllCounted(int threads)
    {
        var counter = new StripedCounter();

        await Task.WhenAll(Enumerable.Range(0, threads)
            .Select(_ => DedicatedThread.Run(() =>
            {
                for (int i = 0; i < PerThread; i++)
                {
                    counter.Add(1);
                }
            }))
            .ToArray()).WaitAsync(Timeout);

        Assert.Equal((long)threads * PerThread, counter.Sum());
    }

    [Fact]
    public async Task ConcurrentReads_CountEveryHitAndMiss()
    {
        const int threads = 8;
        var cache = Cache.NewBuilder<int, string>().MaximumSize(100).RecordStats().Build();
        cache.Put(1, "v");

        await Task.WhenAll(Enumerable.Range(0, threads)
            .Select(_ => DedicatedThread.Run(() =>
            {
                for (int i = 0; i < PerThread; i++)
                {
                    cache.GetIfPresent(1);
                    cache.GetIfPresent(2);
                }
            }))
            .ToArray()).WaitAsync(Timeout);

        CacheStats stats = cache.Stats();
        Assert.Equal((long)threads * PerThread, stats.HitCount);
        Assert.Equal((long)threads * PerThread, stats.MissCount);
    }

    [Fact]
    public void DisabledCounter_RecordsNothing()
    {
        StatsCounter.Disabled.RecordHits(5);
        StatsCounter.Disabled.RecordMisses(5);
        Assert.Same(CacheStats.Empty, StatsCounter.Disabled.Snapshot());
    }
}
