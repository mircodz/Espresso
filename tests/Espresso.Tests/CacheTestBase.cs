using System;

namespace Espresso.Tests;

/// <summary>
/// Shared builders and helpers for cache tests. Direct-executor caches make maintenance observable via
/// <see cref="ICache{K,V}.CleanUp"/>, so most tests can assert eviction/expiry synchronously.
/// </summary>
public abstract class CacheTestBase
{
    /// <summary>A size-bounded, stats-recording cache whose maintenance runs inline (DirectExecutor).</summary>
    protected static ICache<int, string> SizeCache(long maximumSize)
        => Cache.NewBuilder<int, string>()
            .MaximumSize(maximumSize)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .Build();

    /// <summary>A weight-bounded cache whose maintenance runs inline (DirectExecutor).</summary>
    protected static ICache<int, string> WeightCache(long maximumWeight, Func<int, string, int> weigher)
        => Cache.NewBuilder<int, string>()
            .MaximumWeight(maximumWeight)
            .Weigher(new FuncWeigher<int, string>(weigher))
            .Executor(DirectExecutor.Instance)
            .Build();

    /// <summary>Inserts keys <c>[0, count)</c> mapped to <c>"v" + key</c>.</summary>
    protected static void Fill(ICache<int, string> cache, long count)
    {
        for (int i = 0; i < count; i++)
        {
            cache.Put(i, "v" + i);
        }
    }
}
