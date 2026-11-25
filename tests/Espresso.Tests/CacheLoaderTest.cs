using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class CacheLoaderTest
{
    private sealed class SyncLoader : ICacheLoader<int, string>
    {
        public int Calls;

        public string? Load(int key)
        {
            Calls++;
            return key == 2 ? null : $"v{key}";
        }
    }

    private sealed class AsyncLoader : IAsyncCacheLoader<int, string>
    {
        public int Calls;
        public CancellationToken LastToken;

        public Task<string?> AsyncLoad(int key, CancellationToken cancellationToken)
        {
            Calls++;
            LastToken = cancellationToken;
            return Task.FromResult(key == 2 ? null : $"v{key}");
        }
    }

    [Fact]
    public void LoadAll_DefaultImplementation_LoadsEachKeyAndOmitsNulls()
    {
        var loader = new SyncLoader();

        IReadOnlyDictionary<int, string> result =
            ((ICacheLoader<int, string>)loader).LoadAll(new[] { 1, 2, 3 });

        Assert.Equal(3, loader.Calls);
        Assert.Equal(2, result.Count);
        Assert.Equal("v1", result[1]);
        Assert.Equal("v3", result[3]);
        Assert.False(result.ContainsKey(2));
    }

    [Fact]
    public async Task AsyncLoadAll_DefaultImplementation_LoadsEachKeyAndOmitsNulls()
    {
        var loader = new AsyncLoader();
        using var source = new CancellationTokenSource();

        IReadOnlyDictionary<int, string> result =
            await ((IAsyncCacheLoader<int, string>)loader)
                .AsyncLoadAll(new[] { 1, 2, 3 }, source.Token);

        Assert.Equal(3, loader.Calls);
        Assert.Equal(source.Token, loader.LastToken);
        Assert.Equal(2, result.Count);
        Assert.Equal("v1", result[1]);
        Assert.Equal("v3", result[3]);
        Assert.False(result.ContainsKey(2));
    }

    [Fact]
    public async Task AsyncReload_DefaultImplementation_DelegatesToAsyncLoad()
    {
        var loader = new AsyncLoader();
        using var source = new CancellationTokenSource();

        string? result =
            await ((IAsyncCacheLoader<int, string>)loader).AsyncReload(4, "old", source.Token);

        Assert.Equal("v4", result);
        Assert.Equal(1, loader.Calls);
        Assert.Equal(source.Token, loader.LastToken);
    }

    private class ImplicitBulkLoader : ICacheLoader<int, string>
    {
        public string? Load(int key) => null;
        public IReadOnlyDictionary<int, string> LoadAll(IEnumerable<int> keys) => new Dictionary<int, string>();
    }

    private sealed class DerivedBulkLoader : ImplicitBulkLoader;

    private sealed class ExplicitBulkLoader : ICacheLoader<int, string>
    {
        public string? Load(int key) => null;
        IReadOnlyDictionary<int, string> ICacheLoader<int, string>.LoadAll(IEnumerable<int> keys) => new Dictionary<int, string>();
    }

    private interface IBulkLoader : ICacheLoader<int, string>
    {
        IReadOnlyDictionary<int, string> ICacheLoader<int, string>.LoadAll(IEnumerable<int> keys) => new Dictionary<int, string>();
    }

    private sealed class InterfaceBulkLoader : IBulkLoader
    {
        public string? Load(int key) => null;
    }

    [Fact]
    public void HasLoadAll_DetectsAnOverrideOfTheDefaultImplementation()
    {
        Assert.False(LocalLoadingCache.HasLoadAll<int, string>(new SyncLoader()));
        Assert.True(LocalLoadingCache.HasLoadAll<int, string>(new ImplicitBulkLoader()));
        Assert.True(LocalLoadingCache.HasLoadAll<int, string>(new DerivedBulkLoader()));
        Assert.True(LocalLoadingCache.HasLoadAll<int, string>(new ExplicitBulkLoader()));
        Assert.True(LocalLoadingCache.HasLoadAll<int, string>(new InterfaceBulkLoader()));
    }
}
