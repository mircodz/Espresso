using System;
using System.Reflection;
using System.Threading;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Write paths invalidate the key's refresh (which hashes the user's key and so can throw) before they
/// publish the new value and weight. A failing invalidation therefore leaves the entry as it was, or,
/// for a compute over an expired entry, retires it at the weight the policy actually credited, so the
/// weighted totals never drift (or go negative).
/// </summary>
public class WriteRefreshDiscardFailureTest
{
    private const long Second = 1_000_000_000L;

    private sealed class Clock
    {
        public long Now;
        public long Read() => Interlocked.Read(ref Now);
    }

    /// <summary>Fails the N-th key hash after being armed (one shot).</summary>
    private sealed class HashFault
    {
        private int _countdown;
        public void FailOnHash(int n) => Volatile.Write(ref _countdown, n);

        public void OnHash()
        {
            if (Volatile.Read(ref _countdown) > 0 && Interlocked.Decrement(ref _countdown) == 0)
            {
                throw new InvalidOperationException("hash boom");
            }
        }
    }

    private sealed class FaultyKey : IEquatable<FaultyKey>
    {
        private readonly int _id;
        private readonly HashFault _fault;

        public FaultyKey(int id, HashFault fault)
        {
            _id = id;
            _fault = fault;
        }

        public override int GetHashCode()
        {
            _fault.OnHash();
            return _id;
        }

        public bool Equals(FaultyKey? other) => other != null && other._id == _id;
        public override bool Equals(object? obj) => Equals(obj as FaultyKey);
    }

    private sealed class Setup
    {
        public required Clock Clock;
        public required HashFault Fault;
        public required BoundedLocalCache<FaultyKey, string> Cache;
        public FaultyKey Key => new(1, Fault);
    }

    /// <summary>A weighted (weight = length) cache with refresh enabled, holding "a" (weight 1).</summary>
    private static Setup Create(Func<HashFault, Func<FaultyKey, string, int>>? weigher = null)
    {
        var clock = new Clock();
        var fault = new HashFault();
        var cache = (BoundedLocalCache<FaultyKey, string>)Cache.NewBuilder<FaultyKey, string>()
            .Ticker(clock.Read).Executor(DirectExecutor.Instance)
            .MaximumWeight(100).Weigher(weigher?.Invoke(fault) ?? ((_, v) => v.Length))
            .ExpireAfterWrite(TimeSpan.FromSeconds(10)).RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Build(_ => "loaded");
        var setup = new Setup { Clock = clock, Fault = fault, Cache = cache };
        cache.Put(setup.Key, "a");
        cache.CleanUp();
        return setup;
    }

    private static long Field(object cache, string name)
        => (long)typeof(BoundedLocalCache<FaultyKey, string>)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;

    private static void AssertWeights(Setup s, long expected)
    {
        s.Cache.CleanUp();
        Assert.Equal(expected, Field(s.Cache, "_weightedSize"));
        Assert.InRange(Field(s.Cache, "_windowWeightedSize"), 0, expected);
        Assert.InRange(Field(s.Cache, "_mainProtectedWeightedSize"), 0, expected);
    }

    [Fact]
    public void ExpiredCompute_FailingRefreshInvalidation_RetiresAtTheCreditedWeight()
    {
        Setup s = Create();
        s.Clock.Now = 20 * Second; // "a" has expired but is still mapped

        Assert.Throws<InvalidOperationException>(() => s.Cache.ComputeIfAbsent(s.Key, _ =>
        {
            s.Fault.FailOnHash(1); // the refresh invalidation is the next hash of the key
            return "bb";
        }, recordStats: false));

        Assert.Null(s.Cache.GetIfPresentQuietly(s.Key));
        AssertWeights(s, 0);

        // The cache keeps working with consistent totals.
        s.Cache.Put(s.Key, "ccc");
        AssertWeights(s, 3);
    }

    [Fact]
    public void Put_FailingRefreshInvalidation_LeavesTheEntryUnchanged()
    {
        Setup s = Create(fault => (_, v) =>
        {
            if (v == "bb") fault.FailOnHash(1); // the invalidation is the next hash after weighing
            return v.Length;
        });

        Assert.Throws<InvalidOperationException>(() => s.Cache.Put(s.Key, "bb"));

        Assert.Equal("a", s.Cache.GetIfPresentQuietly(s.Key));
        AssertWeights(s, 1);
        s.Cache.Remove(s.Key);
        AssertWeights(s, 0);
    }

    [Fact]
    public void Replace_FailingRefreshInvalidation_LeavesTheEntryUnchanged()
    {
        Setup s = Create(fault => (_, v) =>
        {
            if (v == "bb") fault.FailOnHash(2); // the bin lookup, then the invalidation
            return v.Length;
        });

        Assert.Throws<InvalidOperationException>(() => s.Cache.Replace(s.Key, "a", "bb"));

        Assert.Equal("a", s.Cache.GetIfPresentQuietly(s.Key));
        AssertWeights(s, 1);
        s.Cache.Remove(s.Key);
        AssertWeights(s, 0);
    }
}
