using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Putting an in-flight future over an existing entry records the async sentinel (not the current
/// time) in its expiration timestamps, as a new in-flight entry gets. Otherwise a load that outlives
/// the expiry is discarded on completion (its <c>Replace(f, f)</c> is refused as expired), callers
/// lose the loaded value, and the refused node stays mapped at the in-flight weight of 0.
/// </summary>
public class InFlightPutTimestampsTest
{
    private const long Second = 1_000_000_000L;
    private const int K = 1, X = 2, Y = 3;

    private sealed class Clock
    {
        public long Now;
        public long Read() => Interlocked.Read(ref Now);
        public void Set(double seconds) => Interlocked.Exchange(ref Now, (long)(seconds * Second));
    }

    private sealed class Setup
    {
        public required Clock Clock;
        public required IAsyncCache<int, string> Async;
        public required BoundedLocalCache<int, Task<string>> Store;
    }

    private static Setup Create(Action<EspressoBuilder<int, string>> expiry)
    {
        var clock = new Clock();
        var builder = Cache.NewBuilder<int, string>().Ticker(clock.Read).Executor(DirectExecutor.Instance)
            .MaximumWeight(1_000).Weigher((_, v) => v.Length);
        expiry(builder);
        IAsyncCache<int, string> async = builder.BuildAsync();
        var store = (BoundedLocalCache<int, Task<string>>)typeof(LocalAsyncCache<int, string>)
            .GetField("Cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(async)!;
        return new Setup { Clock = clock, Async = async, Store = store };
    }

    private static Node<int, Task<string>>? NodeOf(BoundedLocalCache<int, Task<string>> store, int key)
    {
        var data = (ConcurrentHashMap<int, Node<int, Task<string>>>)typeof(BoundedLocalCache<int, Task<string>>)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        return data.GetOrDefault(key);
    }

    /// <summary>After quiescence the policy's total equals the user weight of every mapped value.</summary>
    private static void AssertWeightsConsistent(Setup s)
    {
        s.Store.CleanUp();
        long expected = 0;
        foreach (int key in new[] { K, X, Y })
        {
            Node<int, Task<string>>? node = NodeOf(s.Store, key);
            if (node?.Value is { IsCompletedSuccessfully: true } done)
            {
                expected += done.Result.Length;
            }
        }
        long weightedSize = (long)typeof(BoundedLocalCache<int, Task<string>>)
            .GetField("_weightedSize", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Store)!;
        Assert.Equal(expected, weightedSize);
    }

    [Fact]
    public async Task ExpireAfterAccess_LoadOutlivingTheExpiry_IsKeptOnCompletion()
    {
        // The access deque ends up [x@5, k@1, y@9.5], so the scan at 11.5s stops
        // at x and never reaches k to re-stamp it.
        Setup s = Create(b => b.ExpireAfterAccess(TimeSpan.FromSeconds(10)));
        s.Async.Put(K, Task.FromResult("k"));
        s.Async.Put(X, Task.FromResult("x"));
        var load = new TaskCompletionSource<string>();

        s.Clock.Set(1);
        s.Async.Put(K, load.Task);
        s.Clock.Set(5);
        Assert.NotNull(s.Async.GetIfPresent(X));
        s.Clock.Set(6);
        s.Store.CleanUp(); // rotates the in-flight k behind x
        s.Clock.Set(9);
        Assert.Same(load.Task, s.Async.GetIfPresent(K));
        s.Clock.Set(9.5);
        s.Async.Put(Y, Task.FromResult("y"));
        s.Clock.Set(11.5);
        s.Store.CleanUp(); // stops at x

        s.Clock.Set(12);
        load.SetResult("loaded");

        Task<string>? hit = s.Async.GetIfPresent(K);
        Assert.NotNull(hit);
        Assert.Equal("loaded", await hit!);
        AssertWeightsConsistent(s);
    }

    [Fact]
    public async Task ExpireAfterWrite_LoadOutlivingTheExpiry_IsKeptOnCompletion()
    {
        // Write order ends up [x@2, k@0-1, y@9.5] (the scan rotates the in-flight k behind x), so the
        // scan at 11.5s stops at x and never reaches k to re-stamp it.
        Setup s = Create(b => b.ExpireAfterWrite(TimeSpan.FromSeconds(10)));
        s.Async.Put(K, Task.FromResult("k"));
        var load = new TaskCompletionSource<string>();

        s.Clock.Set(1);
        s.Async.Put(K, load.Task);
        s.Clock.Set(2);
        s.Async.Put(X, Task.FromResult("x"));
        s.Clock.Set(6);
        s.Store.CleanUp(); // rotates the in-flight k behind x
        s.Clock.Set(9.5);
        s.Async.Put(Y, Task.FromResult("y"));
        s.Clock.Set(11.5);
        s.Store.CleanUp(); // stops at x

        load.SetResult("loaded");

        Task<string>? hit = s.Async.GetIfPresent(K);
        Assert.NotNull(hit);
        Assert.Equal("loaded", await hit!);
        AssertWeightsConsistent(s);
    }
}
