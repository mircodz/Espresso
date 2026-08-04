using System;
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
}
