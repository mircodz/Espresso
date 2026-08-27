using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
}
