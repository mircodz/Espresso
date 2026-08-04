using System;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

public sealed class BoundedCacheTest : CacheTestBase
{
    [Fact]
    public void Put_ThenGet_ReturnsValue()
    {
        var cache = SizeCache(100);

        cache.Put(1, "a");

        Assert.Equal("a", cache.GetIfPresent(1));
        Assert.Null(cache.GetIfPresent(2));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(1000)]
    public void Put_BeyondMaximum_RespectsBoundAfterCleanUp(int max)
    {
        var cache = SizeCache(max);

        Fill(cache, 10 * max);
        cache.CleanUp();

        Assert.True(cache.EstimatedSize() <= max, $"size {cache.EstimatedSize()} exceeds {max}");
    }

    [Fact]
    public void Get_WithFactory_ComputesOnceAndCaches()
    {
        var cache = SizeCache(100);
        int calls = 0;

        Assert.Equal("v1", cache.Get(1, k => { calls++; return "v" + k; }));
        Assert.Equal("v1", cache.Get(1, _ => { calls++; return "other"; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Invalidate_RemovesEntry()
    {
        var cache = SizeCache(100);
        cache.Put(1, "a");

        cache.Invalidate(1);

        Assert.Null(cache.GetIfPresent(1));
        cache.CleanUp();
        Assert.Equal(0, cache.EstimatedSize());
    }

    [Fact]
    public void FrequentKeys_SurviveColdFlood()
    {
        const int max = 100;
        var cache = SizeCache(max);

        // Establish a hot set and raise its frequency.
        for (int round = 0; round < 20; round++)
        {
            for (int i = 0; i < 10; i++)
            {
                cache.Put(i, "hot" + i);
                cache.GetIfPresent(i);
            }
        }
        cache.CleanUp();

        // Flood with cold one-shot keys.
        for (int i = 1000; i < 1000 + 5 * max; i++)
        {
            cache.Put(i, "cold" + i);
        }
        cache.CleanUp();

        int survivors = 0;
        for (int i = 0; i < 10; i++)
        {
            if (cache.GetIfPresent(i) != null) survivors++;
        }
        Assert.True(survivors >= 7, $"expected most hot keys to survive, got {survivors}/10");
    }

    [Fact]
    public void WeightedCache_RespectsMaximumWeight()
    {
        const int maxWeight = 1000;
        var cache = WeightCache(maxWeight, (_, v) => v.Length);

        for (int i = 0; i < 500; i++)
        {
            cache.Put(i, new string('x', 10)); // weight 10 each
        }
        cache.CleanUp();

        // Total weight stays bounded: at most ~maxWeight/10 entries.
        Assert.True(cache.EstimatedSize() <= (maxWeight / 10) + 1,
            $"size {cache.EstimatedSize()} exceeds weight bound");
    }

    [Fact]
    public void ZeroWeightEntry_IsPinnedAgainstEviction()
    {
        var cache = WeightCache(100, (k, _) => k == 0 ? 0 : 10);

        cache.Put(0, "pinned");
        for (int i = 1; i < 200; i++)
        {
            cache.Put(i, "v" + i);
        }
        cache.CleanUp();

        Assert.Equal("pinned", cache.GetIfPresent(0));
    }

    [Fact]
    public void Eviction_IsRecordedInStats()
    {
        const int max = 50;
        var cache = SizeCache(max);

        Fill(cache, 10 * max);
        cache.CleanUp();

        Assert.True(cache.Stats().EvictionCount > 0);
    }

    [Fact]
    public void IntKeys_RoundTripThroughBoxedKeyPath()
    {
        var cache = SizeCache(1000);

        Fill(cache, 500);

        Assert.Equal("v250", cache.GetIfPresent(250));
    }

    [Fact]
    public void ConcurrentPutGet_StaysWithinBoundAndReadable()
    {
        const int max = 500;
        // Real thread-pool executor exercises the async drain/maintenance path under contention.
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(max)
            .RecordStats()
            .Build();

        const int threads = 8;
        const int perThread = 50_000;
        Parallel.For(0, threads, t =>
        {
            var rng = new Random(t);
            for (int i = 0; i < perThread; i++)
            {
                int key = rng.Next(2000);
                if ((i & 3) == 0)
                {
                    cache.Put(key, "v" + key);
                }
                else
                {
                    cache.GetIfPresent(key);
                }
            }
        });

        cache.CleanUp();
        Assert.True(cache.EstimatedSize() <= max, $"size {cache.EstimatedSize()} exceeds {max}");
        cache.Put(999999, "final");
        Assert.Equal("final", cache.GetIfPresent(999999));
    }
}
