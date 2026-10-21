using System;
using System.Reflection;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// An async cache is weighted only when the user configured a weigher. The internal future-aware
/// weigher must not make every async cache report varying weights: that would size the frequency
/// sketch from the live entry count (re-allocating it while filling, forgetting counts) and give
/// non-evicting async caches the larger weighted node. A size-bounded async cache still uses the
/// weighted node so an in-flight load weighs zero and is not evicted while loading.
/// </summary>
public sealed class AsyncWeightingTest
{
    private static BoundedLocalCache<int, Task<string>> StoreOf(IAsyncCache<int, string> cache)
        => (BoundedLocalCache<int, Task<string>>)typeof(LocalAsyncCache<int, string>)
            .GetField("Cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;

    private static T Field<T>(BoundedLocalCache<int, Task<string>> store, string name)
        => (T)typeof(BoundedLocalCache<int, Task<string>>)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    [Fact]
    public void UnweightedAsyncCache_IsNotWeighted_AndSizesSketchFromMaximum()
    {
        const int maximum = 10_000;
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(maximum)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();
        var store = StoreOf(cache);
        Assert.False(Field<bool>(store, "_isWeighted"));

        // The sketch initializes at half the maximum, sized by the maximum rather than the count.
        for (int i = 0; i <= maximum / 2; i++)
        {
            cache.Put(i, Task.FromResult("v"));
        }
        cache.CleanUp();

        var sketch = Field<FrequencySketch>(store, "_sketch");
        Assert.Equal(Common.CeilingPowerOfTwo(maximum), sketch.table!.Length);
    }

    [Fact]
    public void WeightedAsyncCache_IsWeighted()
    {
        var cache = Cache.NewBuilder<int, string>()
            .MaximumWeight(100)
            .Weigher((_, v) => v.Length)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();

        var store = StoreOf(cache);
        Assert.True(Field<bool>(store, "_isWeighted"));
        Assert.True((Field<NodeFeature>(store, "_nodeFeatures") & NodeFeature.MaximumWeight) != 0);
    }

    [Fact]
    public void SizeBoundedAsyncCache_KeepsWeightedNodeForInFlightLoads()
    {
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();

        NodeFeature features = Field<NodeFeature>(StoreOf(cache), "_nodeFeatures");
        Assert.True((features & NodeFeature.MaximumWeight) != 0);
    }

    [Fact]
    public void NonEvictingAsyncCache_UsesUnweightedNode()
    {
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(TimeSpan.FromMinutes(1))
            .Executor(DirectExecutor.Instance)
            .BuildAsync();

        NodeFeature features = Field<NodeFeature>(StoreOf(cache), "_nodeFeatures");
        Assert.Equal(NodeFeature.None, features & (NodeFeature.MaximumWeight | NodeFeature.MaximumSize));
    }
}
