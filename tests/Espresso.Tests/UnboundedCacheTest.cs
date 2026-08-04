using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

public sealed class UnboundedCacheTest
{
    private static ICache<string, string> NewCache(bool stats = true)
    {
        var builder = Cache.NewBuilder<string, string>();
        if (stats) builder.RecordStats();
        return builder.Build();
    }

    [Fact]
    public void GetIfPresent_HitAndMiss()
    {
        var cache = NewCache();
        Assert.Null(cache.GetIfPresent("a"));
        cache.Put("a", "1");
        Assert.Equal("1", cache.GetIfPresent("a"));

        var stats = cache.Stats();
        Assert.Equal(1, stats.HitCount);
        Assert.Equal(1, stats.MissCount);
    }

    [Fact]
    public void Get_WithMappingFunction_ComputesOnMiss()
    {
        var cache = NewCache();
        int calls = 0;
        string? v = cache.Get("k", _ => { calls++; return "loaded"; });
        Assert.Equal("loaded", v);
        Assert.Equal(1, calls);

        v = cache.Get("k", _ => { calls++; return "again"; });
        Assert.Equal("loaded", v); // present, function not called
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Get_NullResult_NotCached()
    {
        var cache = NewCache();
        Assert.Null(cache.Get("k", _ => null));
        Assert.Equal(0, cache.EstimatedSize());
        var stats = cache.Stats();
        Assert.Equal(1, stats.MissCount);
        Assert.Equal(1, stats.LoadFailureCount);
    }

    [Fact]
    public void Put_Replace_NotifiesRemoval()
    {
        var listener = new RecordingListener();
        var cache = Cache.NewBuilder<string, string>()
            .RemovalListener(listener)
            .Build();

        cache.Put("a", "1");
        cache.Put("a", "2");
        Assert.Equal("2", cache.GetIfPresent("a"));

        Assert.Single(listener.Events);
        Assert.Equal(("a", "1", RemovalCause.Replaced), listener.Events[0]);
    }

    [Fact]
    public void Invalidate_NotifiesExplicit()
    {
        var listener = new RecordingListener();
        var cache = Cache.NewBuilder<string, string>()
            .RemovalListener(listener)
            .Build();

        cache.Put("a", "1");
        cache.Invalidate("a");
        Assert.Null(cache.GetIfPresent("a"));
        Assert.Single(listener.Events);
        Assert.Equal(("a", "1", RemovalCause.Explicit), listener.Events[0]);
    }

    [Fact]
    public void PutAll_And_GetAllPresent()
    {
        var cache = NewCache(stats: false);
        cache.PutAll(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2", ["c"] = "3" });
        var present = cache.GetAllPresent(new[] { "a", "c", "x" });
        Assert.Equal(2, present.Count);
        Assert.Equal("1", present["a"]);
        Assert.Equal("3", present["c"]);
    }

    [Fact]
    public void InvalidateAll_ClearsEverything()
    {
        var cache = NewCache(stats: false);
        cache.Put("a", "1");
        cache.Put("b", "2");
        cache.InvalidateAll();
        Assert.Equal(0, cache.EstimatedSize());
    }

    [Fact]
    public void EstimatedSize_TracksEntries()
    {
        var cache = NewCache(stats: false);
        Assert.Equal(0, cache.EstimatedSize());
        cache.Put("a", "1");
        cache.Put("b", "2");
        Assert.Equal(2, cache.EstimatedSize());
    }

    [Fact]
    public void Get_IsAtomic_FactoryRunsAtMostOncePerKey()
    {
        var cache = NewCache(stats: false);
        int factoryCalls = 0;
        const int threads = 16;
        using var start = new Barrier(threads);

        Parallel.For(0, threads, _ =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 500; i++)
            {
                cache.Get("k" + i, k =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    return "v-" + k;
                });
            }
        });

        Assert.Equal(500, factoryCalls); // exactly one load per distinct key
        Assert.Equal(500, cache.EstimatedSize());
    }

    private sealed class RecordingListener : IRemovalListener<string, string>
    {
        public readonly List<(string?, string?, RemovalCause)> Events = new();
        public void OnRemoval(string? key, string? value, RemovalCause cause)
        {
            lock (Events) { Events.Add((key, value, cause)); }
        }
    }
}
