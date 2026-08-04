using System;
using System.Reflection;
using Xunit;

namespace Espresso.Tests;

public sealed class HillClimberTest
{
    private static ICache<int, string> NewCache(long maximumSize)
        => Cache.NewBuilder<int, string>()
            .MaximumSize(maximumSize)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .Build();

    private static long Field(ICache<int, string> cache, string name)
    {
        FieldInfo f = cache.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"field {name} not found");
        return (long)f.GetValue(cache)!;
    }

    private static long WindowMaximum(ICache<int, string> c) => Field(c, "_windowMaximum");
    private static long MainProtectedMaximum(ICache<int, string> c) => Field(c, "_mainProtectedMaximum");

    [Fact]
    public void InitialWindowRatio_IsOnePercent()
    {
        var cache = NewCache(10_000);
        // window = max - 0.99*max = 1% of max; mainProtected = 0.80*(max-window).
        Assert.Equal(100, WindowMaximum(cache));
        Assert.Equal((long)(0.80 * (10_000 - 100)), MainProtectedMaximum(cache));
    }

    [Fact]
    public void WindowPlusMain_InvariantHoldsAfterClimbing()
    {
        const int max = 1_000;
        var cache = NewCache(max);

        var rng = new Random(42);
        for (int round = 0; round < 40; round++)
        {
            for (int i = 0; i < max; i++)
            {
                int key = rng.Next(max * 3);
                if (cache.GetIfPresent(key) == null)
                {
                    cache.Put(key, "v" + key);
                }
            }
            cache.CleanUp(); // run maintenance (incl. climb) synchronously
        }

        long window = WindowMaximum(cache);
        long mainProtected = MainProtectedMaximum(cache);
        Assert.True(window >= 0, $"window {window} negative");
        Assert.True(mainProtected >= 0, $"mainProtected {mainProtected} negative");
        Assert.True(window <= max, $"window {window} exceeds max {max}");
        Assert.True(window + mainProtected <= max, $"window+protected {window + mainProtected} exceeds max {max}");
    }

    [Fact]
    public void Workload_AdaptsTheWindow_FromInitialRatio()
    {
        const int max = 500;
        var cache = NewCache(max);
        long initialWindow = WindowMaximum(cache);

        // Drive a mixed recency/frequency workload for many sample periods. The climber must move the
        // window away from its fixed 1% starting point (direction depends on the workload; here we
        // only assert that adaptation happens and the partition stays valid).
        var rng = new Random(11);
        for (int round = 0; round < 80; round++)
        {
            for (int i = 0; i < max * 2; i++)
            {
                // Half the traffic is a hot frequency-friendly set, half is a recency-friendly scan.
                int key = (i % 2 == 0) ? rng.Next(max / 4) : (i % (max + max / 2));
                if (cache.GetIfPresent(key) == null)
                {
                    cache.Put(key, "v" + key);
                }
            }
            cache.CleanUp();
        }

        long window = WindowMaximum(cache);
        Assert.True(window != initialWindow,
            $"window {window} did not adapt from its initial {initialWindow}");
        // The adapted partition must remain valid.
        Assert.True(window >= 0 && window + MainProtectedMaximum(cache) <= max);
    }

    [Fact]
    public void Climbing_DoesNotBreakEviction_SizeStaysBounded()
    {
        const int max = 400;
        var cache = NewCache(max);
        var rng = new Random(7);
        for (int i = 0; i < 200_000; i++)
        {
            int key = rng.Next(max * 4);
            if (cache.GetIfPresent(key) == null)
            {
                cache.Put(key, "v" + key);
            }
        }
        cache.CleanUp();
        Assert.True(cache.EstimatedSize() <= max,
            $"size {cache.EstimatedSize()} exceeded bound {max} after climbing");
    }

    [Fact]
    public void SmallCache_ClimberDoesNotCrash()
    {
        // Small caches (<= 512) use the alternate step-decay / sample-ratio path.
        var cache = NewCache(64);
        for (int round = 0; round < 100; round++)
        {
            for (int i = 0; i < 200; i++)
            {
                int key = i % 96;
                if (cache.GetIfPresent(key) == null)
                {
                    cache.Put(key, "v" + key);
                }
            }
            cache.CleanUp();
        }
        Assert.True(cache.EstimatedSize() <= 64);
    }
}
