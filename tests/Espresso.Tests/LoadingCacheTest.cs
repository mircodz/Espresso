using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Espresso.Tests;

public sealed class LoadingCacheTest : CacheTestBase
{
    private sealed class CountingLoader(Func<string, string?> fn) : ICacheLoader<string, string>
    {
        public int Calls;

        public string? Load(string key)
        {
            Calls++;
            return fn(key);
        }
    }

    private static ILoadingCache<string, string> NewLoading(Func<string, string?> fn, out CountingLoader loader)
    {
        loader = new CountingLoader(fn);
        return Cache.NewBuilder<string, string>().RecordStats().Build(loader);
    }

    [Fact]
    public void Get_MissingKey_LoadsOnceThenCaches()
    {
        var cache = NewLoading(k => "v-" + k, out var loader);

        Assert.Equal("v-a", cache.Get("a"));
        Assert.Equal("v-a", cache.Get("a"));
        Assert.Equal(1, loader.Calls);
    }

    [Fact]
    public void Get_LoaderReturnsNull_LeavesKeyAbsentAndRecordsFailure()
    {
        var cache = NewLoading(_ => null, out _);

        Assert.Null(cache.Get("a"));
        Assert.Equal(0, cache.EstimatedSize());
        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    [Fact]
    public void GetAll_MixedPresence_LoadsOnlyMissingKeys()
    {
        var cache = NewLoading(k => "v-" + k, out var loader);
        cache.Put("a", "existing");

        var all = cache.GetAll(new[] { "a", "b", "c" });

        Assert.Equal("existing", all["a"]);
        Assert.Equal("v-b", all["b"]);
        Assert.Equal("v-c", all["c"]);
        Assert.Equal(2, loader.Calls); // only b and c loaded
    }

    [Fact]
    public void Refresh_LoaderReturnsNewValue_ReplacesExisting()
    {
        int version = 1;
        var cache = NewLoading(k => $"{k}-v{version}", out _);
        Assert.Equal("a-v1", cache.Get("a"));

        version = 2;
        cache.Refresh("a");

        Assert.Equal("a-v2", cache.GetIfPresent("a"));
    }

    [Fact]
    public void Refresh_LoaderReturnsNull_KeepsPreviousValue()
    {
        bool returnNull = false;
        var cache = NewLoading(k => returnNull ? null : "v-" + k, out _);
        Assert.Equal("v-a", cache.Get("a"));

        returnNull = true;
        cache.Refresh("a");

        Assert.Equal("v-a", cache.GetIfPresent("a"));
    }

    [Fact]
    public void Get_OnManualCacheWithoutLoader_Throws()
    {
        var cache = (ILoadingCache<string, string>)Cache.NewBuilder<string, string>().Build();

        var act = () => cache.Get("a");

        Assert.Throws<InvalidOperationException>(act);
    }

    private sealed class BulkLoader(Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string>> fn)
        : ICacheLoader<string, string>
    {
        public int Calls;
        public readonly List<string[]> Batches = new();

        public string? Load(string key)
        {
            Calls++;
            return "single-" + key;
        }

        public IReadOnlyDictionary<string, string> LoadAll(IEnumerable<string> keys)
        {
            string[] batch = keys.ToArray();
            Batches.Add(batch);
            return fn(batch);
        }
    }

    private static ILoadingCache<string, string> NewLoading(bool bounded, ICacheLoader<string, string> loader)
    {
        var builder = Cache.NewBuilder<string, string>().RecordStats().Executor(DirectExecutor.Instance);
        if (bounded) builder.MaximumSize(100);
        return builder.Build(loader);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAll_DuplicateKeyWhoseLoadReturnsNull_IsLoadedOnce(bool bounded)
    {
        var loader = new CountingLoader(k => k == "x" ? null : "v-" + k);
        var cache = NewLoading(bounded, loader);

        var all = cache.GetAll(new[] { "x", "a", "x" });

        Assert.Equal(new Dictionary<string, string> { ["a"] = "v-a" }, all);
        Assert.Equal(2, loader.Calls);
        Assert.Equal(2, cache.Stats().MissCount);
        Assert.Equal(1, cache.Stats().LoadSuccessCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAll_NullKey_ThrowsBeforeAnyLoad(bool bounded)
    {
        var loader = new CountingLoader(k => "v-" + k);
        var cache = NewLoading(bounded, loader);

        Assert.Throws<ArgumentNullException>(() => cache.GetAll(new[] { "a", null! }));

        Assert.Equal(0, loader.Calls);
        Assert.Equal(0, cache.EstimatedSize());
        Assert.Equal(0, cache.Stats().RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAll_LoadThrows_RecordsTheRemainingKeysAsMisses(bool bounded)
    {
        var loader = new CountingLoader(k => k == "b" ? throw new InvalidOperationException("boom") : "v-" + k);
        var cache = NewLoading(bounded, loader);

        Assert.Throws<InvalidOperationException>(() => cache.GetAll(new[] { "a", "b", "c", "d", "c" }));

        // a and b were loaded; c and d were never reached but still count as misses
        var stats = cache.Stats();
        Assert.Equal(4, stats.MissCount);
        Assert.Equal(0, stats.HitCount);
        Assert.Equal(1, stats.LoadSuccessCount);
        Assert.Equal(1, stats.LoadFailureCount);
        Assert.Equal(2, loader.Calls);
        Assert.Equal("v-a", cache.GetIfPresent("a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAll_DefaultLoadAll_LoadsEachMissingKeyThroughLoad(bool bounded)
    {
        var loader = new CountingLoader(k => "v-" + k);
        var cache = NewLoading(bounded, loader);

        var all = cache.GetAll(new[] { "a", "b" });

        Assert.Equal(2, all.Count);
        Assert.Equal(2, loader.Calls);
        Assert.Equal(2, cache.Stats().LoadSuccessCount); // one load per key, not one bulk load
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAll_OverriddenLoadAll_LoadsTheMissingKeysInOneCall(bool bounded)
    {
        var loader = new BulkLoader(keys => keys.ToDictionary(k => k, k => "bulk-" + k));
        var cache = NewLoading(bounded, loader);
        cache.Put("a", "existing");

        var all = cache.GetAll(new[] { "a", "b", "c", "b" });

        Assert.Equal(new Dictionary<string, string> { ["a"] = "existing", ["b"] = "bulk-b", ["c"] = "bulk-c" }, all);
        Assert.Equal(new[] { "b", "c" }, Assert.Single(loader.Batches));
        Assert.Equal(0, loader.Calls);
        Assert.Equal("bulk-b", cache.GetIfPresent("b"));
        var stats = cache.Stats();
        Assert.Equal(2, stats.HitCount); // a in GetAll, b in GetIfPresent
        Assert.Equal(2, stats.MissCount); // b and c
        Assert.Equal(1, stats.LoadSuccessCount);
        Assert.Equal(0, stats.LoadFailureCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAll_OverriddenLoadAll_AllPresent_DoesNotLoad(bool bounded)
    {
        var loader = new BulkLoader(keys => keys.ToDictionary(k => k, k => "bulk-" + k));
        var cache = NewLoading(bounded, loader);
        cache.Put("a", "existing");

        var all = cache.GetAll(new[] { "a" });

        Assert.Equal("existing", Assert.Single(all).Value);
        Assert.Empty(loader.Batches);
        Assert.Equal(0, cache.Stats().LoadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAll_OverriddenLoadAll_CachesExtraKeysAndOmitsMissingOnes(bool bounded)
    {
        var loader = new BulkLoader(_ => new Dictionary<string, string> { ["b"] = "bulk-b", ["extra"] = "bulk-extra" });
        var cache = NewLoading(bounded, loader);

        var all = cache.GetAll(new[] { "b", "c" });

        Assert.Equal(new Dictionary<string, string> { ["b"] = "bulk-b" }, all);
        Assert.Equal("bulk-extra", cache.GetIfPresent("extra"));
        Assert.Null(cache.GetIfPresent("c"));
        Assert.Equal(1, cache.Stats().LoadSuccessCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAll_OverriddenLoadAll_EmptyResult_RecordsLoadFailure(bool bounded)
    {
        var loader = new BulkLoader(_ => new Dictionary<string, string>());
        var cache = NewLoading(bounded, loader);

        Assert.Empty(cache.GetAll(new[] { "b" }));

        Assert.Equal(0, cache.Stats().LoadSuccessCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAll_OverriddenLoadAll_Throws_RecordsLoadFailure(bool bounded)
    {
        var loader = new BulkLoader(_ => throw new InvalidOperationException("boom"));
        var cache = NewLoading(bounded, loader);

        Assert.Throws<InvalidOperationException>(() => cache.GetAll(new[] { "b", "c" }));

        Assert.Equal(2, cache.Stats().MissCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Equal(0, cache.EstimatedSize());
    }
}
