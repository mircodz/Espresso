using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// A bulk <c>GetAll</c> inserts a proxy future per missing key before it calls the bulk function. Any
/// failure before the bulk load is under way (an invalid or failing key, a throwing or null-returning
/// bulk function) must roll back this call's own proxies and fault them, so a later <c>Get</c> of
/// those keys loads normally instead of waiting forever on an orphaned proxy.
/// </summary>
public sealed class AsyncGetAllSetupTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A key whose hash code throws once its budget of calls is used up.</summary>
    private sealed class FragileKey
    {
        private int _hashBudget;
        private readonly Action? _beforeThrow;

        public FragileKey(int id, int hashBudget = int.MaxValue, Action? beforeThrow = null)
        {
            Id = id;
            _hashBudget = hashBudget;
            _beforeThrow = beforeThrow;
        }

        public int Id { get; }

        public override int GetHashCode()
        {
            if (_hashBudget-- <= 0)
            {
                _beforeThrow?.Invoke();
                throw new InvalidOperationException("hash failure");
            }
            return Id;
        }

        public override bool Equals(object? obj) => obj is FragileKey other && other.Id == Id;
    }

    private static IAsyncCache<TKey, string> NewCache<TKey>() where TKey : notnull
        => Cache.NewBuilder<TKey, string>()
            .MaximumSize(100)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();

    private static Task<IReadOnlyDictionary<TKey, string>> Values<TKey>(IReadOnlyCollection<TKey> keys)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, string>();
        foreach (TKey key in keys)
        {
            result[key] = "bulk-" + key;
        }
        return Task.FromResult<IReadOnlyDictionary<TKey, string>>(result);
    }

    private static IEnumerable<int> ThrowAfter(params int[] keys)
    {
        foreach (int key in keys)
        {
            yield return key;
        }
        throw new InvalidOperationException("enumeration failure");
    }

    [Fact]
    public async Task GetAll_NullKey_ThrowsWithoutLeavingProxies()
    {
        var cache = NewCache<string>();

        Assert.Throws<ArgumentNullException>(() => { _ = cache.GetAll(new[] { "a", null! }, (k, _) => Values(k)); });

        Assert.Null(cache.GetIfPresent("a"));
        Assert.Equal("loaded", await cache.Get("a", _ => "loaded").WaitAsync(Timeout));
    }

    [Fact]
    public async Task GetAll_ThrowingKeyEnumeration_LeavesNoProxies()
    {
        var cache = NewCache<int>();

        Assert.Throws<InvalidOperationException>(() => { _ = cache.GetAll(ThrowAfter(1, 2), (k, _) => Values(k)); });

        Assert.Null(cache.GetIfPresent(1));
        Assert.Null(cache.GetIfPresent(2));
        Assert.Equal("loaded", await cache.Get(1, _ => "loaded").WaitAsync(Timeout));
    }

    [Fact]
    public async Task GetAll_StoreSetupFailure_RollsBackAndFaultsOwnProxies()
    {
        var cache = NewCache<FragileKey>();
        var first = new FragileKey(1);
        var second = new FragileKey(2);
        // One hash for de-duplication, then the store lookup fails.
        var broken = new FragileKey(3, hashBudget: 1);

        Assert.Throws<InvalidOperationException>(
            () => { _ = cache.GetAll(new[] { first, second, broken }, (k, _) => Values(k)); });

        Assert.Null(cache.GetIfPresent(first));
        Assert.Null(cache.GetIfPresent(second));
        Assert.Equal("loaded", await cache.Get(new FragileKey(1), _ => "loaded").WaitAsync(Timeout));
        Assert.Equal(1, cache.EstimatedSize());
    }

    [Fact]
    public void GetAll_StoreSetupFailure_DoesNotRemoveAConcurrentWrite()
    {
        var cache = NewCache<FragileKey>();
        var first = new FragileKey(1);
        Task<string> concurrent = Task.FromResult("concurrent");
        // A writer replaces the first key's proxy while the setup is still running, then it fails.
        var broken = new FragileKey(2, hashBudget: 1, beforeThrow: () => cache.Put(new FragileKey(1), concurrent));

        Assert.Throws<InvalidOperationException>(
            () => { _ = cache.GetAll(new[] { first, broken }, (k, _) => Values(k)); });

        Assert.Same(concurrent, cache.GetIfPresent(first));
    }

    [Fact]
    public async Task GetAll_BulkFunctionReturnsNullTask_FaultsAndRollsBack()
    {
        var cache = NewCache<int>();

        await Assert.ThrowsAnyAsync<Exception>(
            () => cache.GetAll(new[] { 1, 2 }, (_, _) => null!).WaitAsync(Timeout));

        Assert.Null(cache.GetIfPresent(1));
        Assert.Null(cache.GetIfPresent(2));
        Assert.Equal("loaded", await cache.Get(1, _ => "loaded").WaitAsync(Timeout));
    }

    private sealed class NullBulkLoader : IAsyncCacheLoader<int, string>
    {
        public Task<string?> AsyncLoad(int key, CancellationToken ct) => Task.FromResult<string?>("single-" + key);

        public Task<IReadOnlyDictionary<int, string>> AsyncLoadAll(IReadOnlyCollection<int> keys, CancellationToken ct)
            => null!;
    }

    [Fact]
    public async Task LoadingGetAll_LoaderReturnsNullTask_FaultsAndRollsBack()
    {
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(100)
            .Executor(DirectExecutor.Instance)
            .BuildAsync(new NullBulkLoader());

        await Assert.ThrowsAnyAsync<Exception>(() => cache.GetAll(new[] { 1, 2 }).WaitAsync(Timeout));

        Assert.Null(cache.GetIfPresent(1));
        Assert.Equal("single-2", await cache.Get(2).WaitAsync(Timeout));
    }
}
