using System;
using Xunit;

namespace Espresso.Tests;

public sealed class EspressoBuilderTest
{
    [Fact]
    public void Build_ReturnsUsableCache()
    {
        var cache = Cache.NewBuilder<string, string>().Build();
        cache.Put("a", "1");
        Assert.Equal("1", cache.GetIfPresent("a"));
    }

    [Fact]
    public void InitialCapacity_Validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cache.NewBuilder<string, string>().InitialCapacity(-1));
    }

    [Fact]
    public void InitialCapacity_SetTwice_Throws()
    {
        var b = Cache.NewBuilder<string, string>().InitialCapacity(10);
        Assert.Throws<InvalidOperationException>(() => b.InitialCapacity(20));
    }

    [Fact]
    public void MaximumSize_And_Weight_AreMutuallyExclusive()
    {
        var b = Cache.NewBuilder<string, string>().MaximumSize(100);
        Assert.Throws<InvalidOperationException>(() => b.MaximumWeight(100));
    }

    [Fact]
    public void NegativeDuration_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Cache.NewBuilder<string, string>().ExpireAfterWrite(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void BoundedOptions_Build_Succeeds()
    {
        Assert.NotNull(Cache.NewBuilder<string, string>().MaximumSize(100).Build());
        Assert.NotNull(Cache.NewBuilder<string, string>()
            .MaximumWeight(100).Weigher(new FuncWeigher<string, string>((_, v) => v.Length)).Build());
    }

    [Fact]
    public void RecordStats_TogglesCounter()
    {
        var withStats = Cache.NewBuilder<string, string>().RecordStats().Build();
        withStats.GetIfPresent("absent");
        Assert.Equal(1, withStats.Stats().MissCount);

        var withoutStats = Cache.NewBuilder<string, string>().Build();
        withoutStats.GetIfPresent("absent");
        Assert.Equal(0, withoutStats.Stats().MissCount);
    }

    [Fact]
    public void Smoke_BuilderToGet()
    {
        var cache = Cache.NewBuilder<string, string>().RecordStats().Build();
        Assert.Equal("v", cache.Get("k", _ => "v"));
        Assert.Equal(1, cache.Stats().MissCount);
    }

    // weigher without maximumWeight is rejected at build time
    [Fact]
    public void Weigher_WithoutMaximumWeight_ThrowsOnBuild()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Cache.NewBuilder<string, string>()
                .Weigher(new FuncWeigher<string, string>((_, v) => v.Length))
                .Build());
    }

    // maximumWeight without a weigher is likewise rejected.
    [Fact]
    public void MaximumWeight_WithoutWeigher_ThrowsOnBuild()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Cache.NewBuilder<string, string>().MaximumWeight(100).Build());
    }

    // refreshAfterWrite requires a loading cache: a non-loading Build() is rejected.
    [Fact]
    public void RefreshAfterWrite_WithoutLoader_ThrowsOnBuild()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Cache.NewBuilder<string, string>()
                .RefreshAfterWrite(TimeSpan.FromMinutes(1))
                .Build());
        // But is accepted with a loader.
        Assert.NotNull(Cache.NewBuilder<string, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Build(new FuncCacheLoader<string, string>(k => k)));
    }

    // a weigher returning a negative weight violates its contract
    [Fact]
    public void NegativeWeigherResult_Throws()
    {
        var cache = Cache.NewBuilder<string, string>()
            .MaximumWeight(100)
            .Weigher(new FuncWeigher<string, string>((_, _) => -1))
            .Build();
        Assert.Throws<ArgumentException>(() => cache.Put("k", "v"));
    }

    // a null key throws ArgumentNullException
    [Fact]
    public void NullKey_ThrowsArgumentNullException()
    {
        var cache = Cache.NewBuilder<string, string>().MaximumSize(10).Build();
        Assert.Throws<ArgumentNullException>(() => cache.GetIfPresent(null!));
        Assert.Throws<ArgumentNullException>(() => cache.Put(null!, "v"));
        Assert.Throws<ArgumentNullException>(() => cache.Invalidate(null!));
    }
}
