using System;
using Xunit;

namespace Espresso.Tests;

public sealed class BoundedCacheTest
{
    // A direct-executor bounded cache makes maintenance observable via CleanUp().
    private static ICache<int, string> NewSizeCache(long maximumSize)
        => Espresso.NewBuilder<int, string>()
            .MaximumSize(maximumSize)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .Build();

    [Fact]
    public void Put_Get_Basic()
    {
        var cache = NewSizeCache(100);
        cache.Put(1, "a");
        Assert.Equal("a", cache.GetIfPresent(1));
        Assert.Null(cache.GetIfPresent(2));
    }

    [Fact]
    public void RespectsMaximumSize_AfterCleanUp()
    {
        const int max = 100;
        var cache = NewSizeCache(max);
        for (int i = 0; i < 10 * max; i++)
        {
            cache.Put(i, "v" + i);
        }
        cache.CleanUp();
        Assert.True(cache.EstimatedSize() <= max,
            $"size {cache.EstimatedSize()} should be <= {max}");
    }

    [Fact]
    public void Get_ComputesAndCaches()
    {
        var cache = NewSizeCache(100);
        int calls = 0;
        Assert.Equal("v1", cache.Get(1, k => { calls++; return "v" + k; }));
        Assert.Equal("v1", cache.Get(1, k => { calls++; return "other"; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Invalidate_Removes()
    {
        var cache = NewSizeCache(100);
        cache.Put(1, "a");
        cache.Invalidate(1);
        Assert.Null(cache.GetIfPresent(1));
        cache.CleanUp();
        Assert.Equal(0, cache.EstimatedSize());
    }

    [Fact]
    public void FrequentKeys_SurviveEviction()
    {
        const int max = 100;
        var cache = NewSizeCache(max);

        // Establish a hot set and make it frequent.
        for (int round = 0; round < 20; round++)
        {
            for (int i = 0; i < 10; i++)
            {
                cache.Put(i, "hot" + i);
                cache.GetIfPresent(i); // raise frequency
            }
        }
        cache.CleanUp();

        // Flood with cold one-shot keys.
        for (int i = 1000; i < 1000 + 5 * max; i++)
        {
            cache.Put(i, "cold" + i);
        }
        cache.CleanUp();

        // Most of the hot set should survive TinyLFU admission.
        int survivors = 0;
        for (int i = 0; i < 10; i++)
        {
            if (cache.GetIfPresent(i) != null) survivors++;
        }
        Assert.True(survivors >= 7, $"expected most hot keys to survive, got {survivors}/10");
    }

    [Fact]
    public void WeightedEviction_RespectsMaximumWeight()
    {
        const int maxWeight = 1000;
        var cache = Espresso.NewBuilder<int, string>()
            .MaximumWeight(maxWeight)
            .Weigher(new FuncWeigher<int, string>((_, v) => v.Length))
            .Executor(DirectExecutor.Instance)
            .Build();

        for (int i = 0; i < 500; i++)
        {
            cache.Put(i, new string('x', 10)); // weight 10 each
        }
        cache.CleanUp();

        // Total weight must stay within bound: at most ~maxWeight/10 entries.
        Assert.True(cache.EstimatedSize() <= (maxWeight / 10) + 1,
            $"size {cache.EstimatedSize()} exceeds weight bound");
    }

    [Fact]
    public void ZeroWeight_PinsEntry()
    {
        var cache = Espresso.NewBuilder<int, string>()
            .MaximumWeight(100)
            .Weigher(new FuncWeigher<int, string>((k, _) => k == 0 ? 0 : 10))
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(0, "pinned");
        for (int i = 1; i < 200; i++)
        {
            cache.Put(i, "v" + i);
        }
        cache.CleanUp();

        // The zero-weight entry is skipped during eviction and must remain.
        Assert.Equal("pinned", cache.GetIfPresent(0));
    }

    [Fact]
    public void Stats_RecordsEvictions()
    {
        const int max = 50;
        var cache = NewSizeCache(max);
        for (int i = 0; i < 10 * max; i++)
        {
            cache.Put(i, "v" + i);
        }
        cache.CleanUp();
        Assert.True(cache.Stats().EvictionCount > 0);
    }

    [Fact]
    public void IntKeys_WorkWithoutIssue()
    {
        // Exercises the box-per-entry key path in the node.
        var cache = NewSizeCache(1000);
        for (int i = 0; i < 500; i++)
        {
            cache.Put(i, "v" + i);
        }
        Assert.Equal("v250", cache.GetIfPresent(250));
    }

    [Fact]
    public void ConcurrentPutGet_StaysWithinBound_NoCorruption()
    {
        const int max = 500;
        // Real thread-pool executor: exercises the async drain/maintenance path under contention.
        var cache = Espresso.NewBuilder<int, string>()
            .MaximumSize(max)
            .RecordStats()
            .Build();

        const int threads = 8;
        const int perThread = 50_000;
        System.Threading.Tasks.Parallel.For(0, threads, t =>
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
        // The core invariant: after maintenance settles, the cache respects its bound and is readable.
        Assert.True(cache.EstimatedSize() <= max,
            $"size {cache.EstimatedSize()} should be <= {max} after cleanup");
        // And it is still functional.
        cache.Put(999999, "final");
        Assert.Equal("final", cache.GetIfPresent(999999));
    }
}
