using System;
using Xunit;

namespace Espresso.Tests;

public sealed class EspressoBuilderTest
{
    [Fact]
    public void Build_Default_ReturnsUsableCache()
    {
        var cache = Cache.NewBuilder<string, string>().Build();

        cache.Put("a", "1");

        Assert.Equal("1", cache.GetIfPresent("a"));
    }

    [Fact]
    public void InitialCapacity_Negative_Throws()
    {
        Action act = () => Cache.NewBuilder<string, string>().InitialCapacity(-1);

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    [Fact]
    public void InitialCapacity_SetTwice_Throws()
    {
        var builder = Cache.NewBuilder<string, string>().InitialCapacity(10);

        Action act = () => builder.InitialCapacity(20);

        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void MaximumWeight_AfterMaximumSize_Throws()
    {
        var builder = Cache.NewBuilder<string, string>().MaximumSize(100);

        Action act = () => builder.MaximumWeight(100);

        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void ExpireAfterWrite_NegativeDuration_Throws()
    {
        Action act = () => Cache.NewBuilder<string, string>().ExpireAfterWrite(TimeSpan.FromSeconds(-1));

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    [Fact]
    public void MaximumSize_Build_Succeeds()
    {
        Assert.NotNull(Cache.NewBuilder<string, string>().MaximumSize(100).Build());
    }

    [Fact]
    public void MaximumWeight_WithWeigher_Build_Succeeds()
    {
        Assert.NotNull(Cache.NewBuilder<string, string>()
            .MaximumWeight(100)
            .Weigher(new FuncWeigher<string, string>((_, v) => v.Length))
            .Build());
    }

    [Fact]
    public void RecordStats_Enabled_CountsMisses()
    {
        var withStats = Cache.NewBuilder<string, string>().RecordStats().Build();

        withStats.GetIfPresent("absent");

        Assert.Equal(1, withStats.Stats().MissCount);
    }

    [Fact]
    public void RecordStats_Disabled_DoesNotCountMisses()
    {
        var withoutStats = Cache.NewBuilder<string, string>().Build();

        withoutStats.GetIfPresent("absent");

        Assert.Equal(0, withoutStats.Stats().MissCount);
    }

    [Fact]
    public void Get_WithLoader_ComputesValueAndRecordsMiss()
    {
        var cache = Cache.NewBuilder<string, string>().RecordStats().Build();

        Assert.Equal("v", cache.Get("k", _ => "v"));
        Assert.Equal(1, cache.Stats().MissCount);
    }

    // weigher without maximumWeight is rejected at build time
    [Fact]
    public void Weigher_WithoutMaximumWeight_ThrowsOnBuild()
    {
        Action act = () => Cache.NewBuilder<string, string>()
            .Weigher(new FuncWeigher<string, string>((_, v) => v.Length))
            .Build();

        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void MaximumWeight_WithoutWeigher_ThrowsOnBuild()
    {
        Action act = () => Cache.NewBuilder<string, string>().MaximumWeight(100).Build();

        Assert.Throws<InvalidOperationException>(act);
    }

    // refreshAfterWrite requires a loading cache: a non-loading Build() is rejected.
    [Fact]
    public void RefreshAfterWrite_WithoutLoader_ThrowsOnBuild()
    {
        Action act = () => Cache.NewBuilder<string, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Build();

        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void RefreshAfterWrite_WithLoader_Build_Succeeds()
    {
        Assert.NotNull(Cache.NewBuilder<string, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Build(new FuncCacheLoader<string, string>(k => k)));
    }

    [Fact]
    public void Put_NegativeWeigherResult_Throws()
    {
        var cache = Cache.NewBuilder<string, string>()
            .MaximumWeight(100)
            .Weigher(new FuncWeigher<string, string>((_, _) => -1))
            .Build();

        Action act = () => cache.Put("k", "v");

        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void GetIfPresent_NullKey_Throws()
    {
        var cache = Cache.NewBuilder<string, string>().MaximumSize(10).Build();

        Action act = () => cache.GetIfPresent(null!);

        Assert.Throws<ArgumentNullException>(act);
    }

    [Fact]
    public void Put_NullKey_Throws()
    {
        var cache = Cache.NewBuilder<string, string>().MaximumSize(10).Build();

        Action act = () => cache.Put(null!, "v");

        Assert.Throws<ArgumentNullException>(act);
    }

    [Fact]
    public void Invalidate_NullKey_Throws()
    {
        var cache = Cache.NewBuilder<string, string>().MaximumSize(10).Build();

        Action act = () => cache.Invalidate(null!);

        Assert.Throws<ArgumentNullException>(act);
    }
}
