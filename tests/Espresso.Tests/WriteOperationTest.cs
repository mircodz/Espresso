using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Write operations follow the entry lifecycle: a removed node is retired and unlinked,
/// an expired entry is reported as EXPIRED with an eviction stat, conditional operations respect
/// expiry, and a throwing loader is counted as a miss and a load failure.
/// </summary>
public class WriteOperationTest
{
    private sealed class Clock
    {
        public long Now;
        public long Read() => Interlocked.Read(ref Now);
        public void Advance(TimeSpan by) => Interlocked.Add(ref Now, by.Ticks * 100L);
    }

    private sealed class ManualExecutor : IExecutor
    {
        private readonly Queue<Action> _tasks = new();
        public void Execute(Action action) => _tasks.Enqueue(action);
        public void RunAll()
        {
            while (_tasks.Count != 0) _tasks.Dequeue()();
        }
    }

    private static long PolicyWeight<V>(object cache) where V : class
        => (long)typeof(BoundedLocalCache<int, V>)
            .GetField("_weightedSize", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;

    private static ConcurrentHashMap<object, object> Refreshes(object cache)
        => (ConcurrentHashMap<object, object>)typeof(BoundedLocalCache<int, string>)
            .GetField("_refreshes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;

    private static Node<int, string>? NodeOf(object cache, int key)
        => ((ConcurrentHashMap<int, Node<int, string>>)typeof(BoundedLocalCache<int, string>)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!).GetOrDefault(key);

    // ----- an expired entry whose recomputation yields nothing is retired like a removal -----

    [Fact]
    public void ExpiredNullLoad_ReclaimsPolicyAndRecordsEviction()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        var removals = new List<(string?, RemovalCause)>();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10).ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .Ticker(clock.Read).Executor(executor).RecordStats()
            .RemovalListener((_, v, c) => removals.Add((v, c)))
            .Build();
        cache.Put(1, "old");
        executor.RunAll();
        Node<int, string> node = NodeOf(cache, 1)!;
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Null(cache.Get(1, _ => null));
        executor.RunAll();

        Assert.Equal(0, cache.EstimatedSize());
        Assert.False(node.IsAlive);
        Assert.Equal(0L, PolicyWeight<string>(cache));
        Assert.Equal(1, cache.Stats().EvictionCount);
        Assert.Equal(new[] { ((string?)"old", RemovalCause.Expired) }, removals);
    }

    [Fact]
    public void ExpiredThrowingLoad_RecordsFailureAndRetiresExpiredMapping()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        var removals = new List<(string?, RemovalCause)>();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10).ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .Ticker(clock.Read).Executor(executor).RecordStats()
            .RemovalListener((_, v, c) => removals.Add((v, c)))
            .Build();
        cache.Put(1, "old");
        executor.RunAll();
        Node<int, string> node = NodeOf(cache, 1)!;
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Throws<InvalidOperationException>(
            () => cache.Get(1, _ => throw new InvalidOperationException()));
        executor.RunAll();

        Assert.Equal(0, cache.EstimatedSize());
        Assert.False(node.IsAlive);
        Assert.Equal(0L, PolicyWeight<string>(cache));
        Assert.Equal(1, cache.Stats().MissCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Equal(1, cache.Stats().EvictionCount);
        Assert.Equal(new[] { ((string?)"old", RemovalCause.Expired) }, removals);
    }

    [Fact]
    public void ExpiredLoad_WeigherThrows_RetiresExpiredMappingWithoutMutatingIt()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        var removals = new List<(string?, RemovalCause)>();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumWeight(100).ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .Weigher((_, v) => v == "bad" ? throw new InvalidOperationException() : 1)
            .Ticker(clock.Read).Executor(executor)
            .RemovalListener((_, v, c) => removals.Add((v, c)))
            .Build();
        cache.Put(1, "old");
        executor.RunAll();
        Node<int, string> node = NodeOf(cache, 1)!;
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Throws<InvalidOperationException>(() => cache.Get(1, _ => "bad"));
        executor.RunAll();

        Assert.Equal(0, cache.EstimatedSize());
        Assert.False(node.IsAlive);
        Assert.Equal(0L, PolicyWeight<string>(cache));
        Assert.Equal(new[] { ((string?)"old", RemovalCause.Expired) }, removals);
    }

    // ----- the expired-entry recomputation serializes with the node-locked Put -----

    [Fact]
    public async Task ExpiredCompute_SerializesLoaderWithPut()
    {
        var clock = new Clock();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10).ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .Ticker(clock.Read).Executor(DirectExecutor.Instance)
            .Build();
        cache.Put(1, "old");
        clock.Advance(TimeSpan.FromSeconds(2));

        using var loaderEntered = new ManualResetEventSlim();
        using var releaseLoader = new ManualResetEventSlim();
        Task<string?> loader = DedicatedThread.Run(() => cache.Get(1, _ =>
        {
            loaderEntered.Set();
            releaseLoader.Wait(TimeSpan.FromSeconds(10));
            return "loaded";
        }));
        Assert.True(loaderEntered.Wait(TimeSpan.FromSeconds(10)));

        Task put = DedicatedThread.Run(() => cache.Put(1, "put"));
        await Task.WhenAny(put, Task.Delay(200)); // the Put must wait for the in-flight recomputation
        releaseLoader.Set();
        await loader;
        await put;

        Assert.Equal("put", cache.GetIfPresent(1));
    }

    // ----- Remove / conditional Remove / Replace respect expiry and report it -----

    [Fact]
    public void ExpiredRemove_RecordsEviction()
    {
        var clock = new Clock();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .Ticker(clock.Read).Executor(DirectExecutor.Instance)
            .RecordStats().Build();
        cache.Put(1, "old");
        clock.Advance(TimeSpan.FromSeconds(2));

        cache.Invalidate(1);

        Assert.Equal(1, cache.Stats().EvictionCount);
        Assert.Equal(1, cache.Stats().EvictionWeight);
    }

    [Fact]
    public void ExpiredConditionalRemove_IsFalseAndReportsExpired()
    {
        var clock = new Clock();
        var removals = new List<(string?, RemovalCause)>();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .Ticker(clock.Read).Executor(DirectExecutor.Instance)
            .RemovalListener((_, v, c) => removals.Add((v, c)))
            .RecordStats().Build();
        cache.Put(1, "old");
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.False(((BoundedLocalCache<int, string>)cache).Remove(1, "old"));

        Assert.Equal(0, cache.EstimatedSize());
        Assert.Equal(new[] { ((string?)"old", RemovalCause.Expired) }, removals);
        Assert.Equal(1, cache.Stats().EvictionCount);
    }

    [Fact]
    public void ExpiredConditionalRemove_OfDifferentValue_StillRemovesAsExpired()
    {
        var clock = new Clock();
        var removals = new List<(string?, RemovalCause)>();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .Ticker(clock.Read).Executor(DirectExecutor.Instance)
            .RemovalListener((_, v, c) => removals.Add((v, c)))
            .Build();
        cache.Put(1, "old");
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.False(((BoundedLocalCache<int, string>)cache).Remove(1, "other"));

        Assert.Equal(0, cache.EstimatedSize());
        Assert.Equal(new[] { ((string?)"old", RemovalCause.Expired) }, removals);
    }

    [Fact]
    public void ConditionalRemove_NotifiesTheStoredInstance()
    {
        string stored = new string('v', 3);
        string equal = new string('v', 3);
        object? observed = null;
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10).Executor(DirectExecutor.Instance)
            .RemovalListener((_, v, _) => observed = v)
            .Build();
        cache.Put(1, stored);

        Assert.True(((BoundedLocalCache<int, string>)cache).Remove(1, equal));

        Assert.Same(stored, observed);
    }

    [Fact]
    public void ExpiredReplace_DoesNotReviveEntry()
    {
        var clock = new Clock();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .Ticker(clock.Read).Executor(DirectExecutor.Instance).Build();
        cache.Put(1, "old");
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.False(((BoundedLocalCache<int, string>)cache).Replace(1, "old", "new"));

        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public void Replace_NotifiesOldValueAsReplaced()
    {
        var removals = new List<(string?, RemovalCause)>();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10).Executor(DirectExecutor.Instance)
            .RemovalListener((_, v, c) => removals.Add((v, c)))
            .Build();
        cache.Put(1, "old");

        Assert.True(((BoundedLocalCache<int, string>)cache).Replace(1, "old", "new"));

        Assert.Equal(new[] { ((string?)"old", RemovalCause.Replaced) }, removals);
        Assert.Equal("new", cache.GetIfPresent(1));
    }

    [Fact]
    public void Replace_SameInstance_DoesNotNotify()
    {
        var removals = new List<(string?, RemovalCause)>();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10).Executor(DirectExecutor.Instance)
            .RemovalListener((_, v, c) => removals.Add((v, c)))
            .Build();
        cache.Put(1, "old");

        Assert.True(((BoundedLocalCache<int, string>)cache).Replace(1, "old", "old"));

        Assert.Empty(removals);
    }

    [Fact]
    public void Replace_DiscardsAnInFlightRefresh()
    {
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10).RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Executor(DirectExecutor.Instance)
            .Build(k => "loaded");
        cache.Put(1, "old");
        Refreshes(cache).Put(1, new object()); // a reload registered for the key

        Assert.True(((BoundedLocalCache<int, string>)cache).Replace(1, "old", "new"));

        Assert.Equal(0, Refreshes(cache).Count);
    }

    // ----- a throwing loader records a miss and a load failure -----

    [Fact]
    public void Bounded_ThrowingLoader_RecordsMissAndLoadFailure()
    {
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10).Executor(DirectExecutor.Instance).RecordStats().Build();

        Assert.Throws<InvalidOperationException>(
            () => cache.Get(1, _ => throw new InvalidOperationException()));

        Assert.Equal(1, cache.Stats().MissCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    [Fact]
    public void Unbounded_ThrowingLoader_RecordsMissAndLoadFailure()
    {
        var cache = Cache.NewBuilder<int, string>().RecordStats().Build();

        Assert.Throws<InvalidOperationException>(
            () => cache.Get(1, _ => throw new InvalidOperationException()));

        Assert.Equal(1, cache.Stats().MissCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    [Fact]
    public void Async_SynchronouslyThrowingLoader_RecordsMissAndLoadFailure()
    {
        var cache = Cache.NewBuilder<int, string>()
            .Executor(DirectExecutor.Instance).RecordStats().BuildAsync();

        Func<int, CancellationToken, Task<string?>> loader = (_, _) => throw new InvalidOperationException();
        Exception? thrown = null;
        try
        {
            _ = cache.Get(1, loader); // the mapping function throws synchronously
        }
        catch (InvalidOperationException e)
        {
            thrown = e;
        }

        Assert.NotNull(thrown);

        Assert.Equal(1, cache.Stats().MissCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    [Fact]
    public void BoundedRefresh_ThrowingLoader_RecordsLoadFailure()
    {
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10).Executor(DirectExecutor.Instance).RecordStats()
            .Build(k => throw new InvalidOperationException());

        Assert.Throws<InvalidOperationException>(() => cache.Refresh(1));

        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    [Fact]
    public void UnboundedRefresh_ThrowingLoader_RecordsLoadFailure()
    {
        var cache = Cache.NewBuilder<int, string>().RecordStats()
            .Build(k => throw new InvalidOperationException());

        Assert.Throws<InvalidOperationException>(() => cache.Refresh(1));

        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }
}
