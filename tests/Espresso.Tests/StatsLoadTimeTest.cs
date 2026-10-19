using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

/// <summary>Load-time statistics must be measured whenever stats are recorded (Caffeine's stats ticker).</summary>
public class StatsLoadTimeTest
{
    private static string SlowLoad(int key)
    {
        Thread.Sleep(30);
        return "v" + key;
    }

    [Fact]
    public void Unbounded_RecordStatsWithoutTimingFeature_RecordsLoadTime()
    {
        ICache<int, string> cache = Cache.NewBuilder<int, string>().RecordStats().Build();
        cache.Get(1, SlowLoad);
        Assert.True(cache.Stats().TotalLoadTime > 0);
    }

    [Fact]
    public void UnboundedLoading_Refresh_RecordsLoadTime()
    {
        ILoadingCache<int, string> cache = Cache.NewBuilder<int, string>().RecordStats().Build(SlowLoad);
        cache.Refresh(1);
        Assert.True(cache.Stats().TotalLoadTime > 0);
    }

    [Fact]
    public void Bounded_RecordStatsWithoutTimingFeature_RecordsLoadTime()
    {
        ICache<int, string> cache = Cache.NewBuilder<int, string>()
            .MaximumSize(100).Executor(DirectExecutor.Instance).RecordStats().Build();
        cache.Get(1, SlowLoad);
        Assert.True(cache.Stats().TotalLoadTime > 0);
    }

    [Fact]
    public void BoundedLoading_Refresh_RecordsLoadTime()
    {
        ILoadingCache<int, string> cache = Cache.NewBuilder<int, string>()
            .MaximumSize(100).Executor(DirectExecutor.Instance).RecordStats().Build(SlowLoad);
        cache.Refresh(1);
        Assert.True(cache.Stats().TotalLoadTime > 0);
    }

    [Fact]
    public async Task Async_RecordStatsWithoutTimingFeature_RecordsLoadTime()
    {
        IAsyncLoadingCache<int, string> cache = Cache.NewBuilder<int, string>()
            .Executor(DirectExecutor.Instance).RecordStats()
            .BuildAsync(async k =>
            {
                await Task.Delay(30);
                return (string?)("v" + k);
            });
        Assert.Equal("v1", await cache.Get(1));
        for (int i = 0; i < 100 && cache.Stats().LoadCount == 0; i++)
        {
            await Task.Delay(10); // the completion handler records the load
        }
        Assert.True(cache.Stats().TotalLoadTime > 0);
    }

    [Fact]
    public void ExplicitTicker_IsUsedForLoadTime()
    {
        long now = 0;
        ICache<int, string> cache = Cache.NewBuilder<int, string>()
            .MaximumSize(100).Executor(DirectExecutor.Instance).RecordStats()
            .Ticker(() => now)
            .Build();
        cache.Get(1, k =>
        {
            now += 5_000;
            return "v";
        });
        Assert.Equal(5_000, cache.Stats().TotalLoadTime);
    }
}
