using System;
using System.Collections.Generic;
using System.Linq;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Coverage for the less-exercised unbounded-cache surface (keyed InvalidateAll, CleanUp, Refresh,
/// enumeration) and the async expiry adapter's ready/in-flight transitions.
/// </summary>
public sealed class AsyncEdgesTest
{
    // ----- UnboundedLocalCache surface -----

    [Fact]
    public void Unbounded_InvalidateAll_WithKeys_RemovesOnlyThose()
    {
        var removed = new List<string>();
        var cache = Cache.NewBuilder<string, string>()
            .RemovalListener((k, _, c) => { if (c == RemovalCause.Explicit && k != null) removed.Add(k); })
            .Executor(DirectExecutor.Instance)
            .Build();
        cache.Put("a", "1");
        cache.Put("b", "2");
        cache.Put("c", "3");

        cache.InvalidateAll(new[] { "a", "c", "missing" });

        Assert.Null(cache.GetIfPresent("a"));
        Assert.Equal("2", cache.GetIfPresent("b"));
        Assert.Null(cache.GetIfPresent("c"));
        Assert.Equal(new[] { "a", "c" }, removed.OrderBy(x => x));
    }

    [Fact]
    public void Unbounded_CleanUp_IsNoOpAndKeepsEntries()
    {
        var cache = Cache.NewBuilder<int, string>().Build();
        cache.Put(1, "a");

        cache.CleanUp();

        Assert.Equal("a", cache.GetIfPresent(1));
        Assert.Equal(1, cache.EstimatedSize());
    }

    [Fact]
    public void Unbounded_GetAllPresent_ReturnsSnapshotOfKnownKeys()
    {
        var cache = Cache.NewBuilder<int, string>().Build();
        cache.Put(1, "a");
        cache.Put(2, "b");
        cache.Put(3, "c");

        IReadOnlyDictionary<int, string> present = cache.GetAllPresent(new[] { 1, 3, 99 });

        Assert.Equal(2, present.Count);
        Assert.Equal("a", present[1]);
        Assert.Equal("c", present[3]);
        Assert.False(present.ContainsKey(99));
    }

    [Fact]
    public void Unbounded_Loading_Refresh_ReloadsValue()
    {
        int loads = 0;
        var cache = Cache.NewBuilder<int, string>()
            .Executor(DirectExecutor.Instance)
            .Build(k => { loads++; return "load" + loads + "-" + k; });

        Assert.Equal("load1-1", cache.Get(1));

        cache.Refresh(1);

        Assert.Equal("load2-1", cache.GetIfPresent(1));
        Assert.Equal(2, loads);
    }

    // ----- AsyncExpiry adapter: ready future computes real duration; in-flight pins with the sentinel -----

    private sealed class ConstExpiry : IExpiry<int, string>
    {
        public long ExpireAfterCreate(int key, string value, long now) => 1000;
        public long ExpireAfterUpdate(int key, string value, long now, long current) => 2000;
        public long ExpireAfterRead(int key, string value, long now, long current) => 3000;
    }

    [Fact]
    public void AsyncExpiry_ReadyFuture_ComputesRealDuration()
    {
        var expiry = new AsyncExpiry<int, string>(new ConstExpiry());
        var ready = System.Threading.Tasks.Task.FromResult("v");

        Assert.Equal(1000, expiry.ExpireAfterCreate(1, ready, 0));
        Assert.Equal(3000, expiry.ExpireAfterRead(1, ready, 0, 500));
    }

    [Fact]
    public void AsyncExpiry_InFlightFuture_PinsWithSentinel()
    {
        var expiry = new AsyncExpiry<int, string>(new ConstExpiry());
        var inFlight = new System.Threading.Tasks.TaskCompletionSource<string>().Task;

        Assert.Equal(BoundedLocalCache<int, string>.AsyncExpiry, expiry.ExpireAfterCreate(1, inFlight, 0));
        Assert.Equal(BoundedLocalCache<int, string>.AsyncExpiry, expiry.ExpireAfterUpdate(1, inFlight, 0, 0));
        Assert.Equal(BoundedLocalCache<int, string>.AsyncExpiry, expiry.ExpireAfterRead(1, inFlight, 0, 0));
    }

    [Fact]
    public void AsyncExpiry_UpdateFromSentinel_RoutesToCreate()
    {
        var expiry = new AsyncExpiry<int, string>(new ConstExpiry());
        var ready = System.Threading.Tasks.Task.FromResult("v");

        // currentDuration still holds the async sentinel => completion transition => ExpireAfterCreate (1000).
        long sentinel = BoundedLocalCache<int, string>.AsyncExpiry;
        Assert.Equal(1000, expiry.ExpireAfterUpdate(1, ready, 0, sentinel));

        // A normal update (real current duration) => ExpireAfterUpdate (2000).
        Assert.Equal(2000, expiry.ExpireAfterUpdate(1, ready, 0, 500));
    }
}
