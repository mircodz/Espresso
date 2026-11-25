using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// An async load that produces null completes the caller's future with null, as Caffeine does: the
/// entry is removed (only if still mapped to that future), the load is recorded as a failure, and no
/// removal notification is delivered for the missing value. Until its completion removes it, a future
/// that completed with null is treated like one still in flight (Caffeine's <c>Async.isReady</c>), so
/// it neither expires nor is weighed as a value.
/// </summary>
public sealed class AsyncNullLoadTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed class Loader : IAsyncCacheLoader<int, string>
    {
        public Func<int, Task<string?>> Load = _ => Task.FromResult<string?>(null);
        public Func<IReadOnlyCollection<int>, Task<IReadOnlyDictionary<int, string>>>? LoadAll;
        public int Loads;

        public Task<string?> AsyncLoad(int key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Loads);
            return Load(key);
        }

        public async Task<IReadOnlyDictionary<int, string>> AsyncLoadAll(
            IReadOnlyCollection<int> keys, CancellationToken cancellationToken)
        {
            if (LoadAll != null)
            {
                return await LoadAll(keys);
            }
            var result = new Dictionary<int, string>();
            foreach (int key in keys)
            {
                string? value = await AsyncLoad(key, cancellationToken);
                if (value != null) result[key] = value;
            }
            return result;
        }
    }

    private static EspressoBuilder<int, string> NewBuilder(ConcurrentQueue<string> notifications)
        => Cache.NewBuilder<int, string>()
            .MaximumSize(100)
            .Executor(DirectExecutor.Instance)
            .RemovalListener((k, v, c) => notifications.Enqueue($"{k}={v ?? "<null>"} ({c})"))
            .RecordStats();

    /// <summary>Waits for the completion to remove the entry, without recording any statistics.</summary>
    private static async Task WaitUntilSize(IAsyncCache<int, string> cache, long size = 0)
    {
        for (int i = 0; i < 2000 && cache.EstimatedSize() != size; i++)
        {
            await Task.Delay(5);
        }
        Assert.Equal(size, cache.EstimatedSize());
    }

    [Fact]
    public async Task Get_SyncFunctionReturnsNull_CompletesWithNullAndRemoves()
    {
        var notifications = new ConcurrentQueue<string>();
        IAsyncCache<int, string> cache = NewBuilder(notifications).BuildAsync();

        var future = cache.Get(1, _ => null);

        Assert.True(future.IsCompletedSuccessfully);
        Assert.Null(await future);
        Assert.Equal(0, cache.EstimatedSize()); // removed before Get returned (the load completed inline)
        Assert.Equal(1, cache.Stats().MissCount);
        Assert.Equal(0, cache.Stats().HitCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Equal(0, cache.Stats().LoadSuccessCount);
        Assert.Null(cache.GetIfPresent(1));
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Get_AsyncFunctionCompletesNullLater_EveryJoinedCallerGetsNull()
    {
        var notifications = new ConcurrentQueue<string>();
        IAsyncCache<int, string> cache = NewBuilder(notifications).BuildAsync();
        var gate = new TaskCompletionSource<string?>();
        int calls = 0;
        Func<int, CancellationToken, Task<string?>> load = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        };

        var first = cache.Get(1, load);
        var second = cache.Get(1, load);
        var third = cache.Get(1, _ => "unused");
        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Same(first, cache.GetIfPresent(1));
        Assert.Equal(1, calls);

        gate.SetResult(null);

        Assert.Null(await first.WaitAsync(Timeout));
        Assert.Null(await second.WaitAsync(Timeout));
        Assert.Null(await third.WaitAsync(Timeout));
        await WaitUntilSize(cache);
        Assert.Equal(1, calls);
        Assert.Equal(1, cache.Stats().MissCount);
        Assert.Equal(3, cache.Stats().HitCount); // two joined Gets and the GetIfPresent
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Equal(0, cache.Stats().LoadSuccessCount);
        Assert.Null(cache.GetIfPresent(1));
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Get_SyncFunctionOnExecutorReturnsNull_CompletesWithNull()
    {
        IAsyncCache<int, string> cache = Cache.NewBuilder<int, string>().RecordStats().BuildAsync();

        Assert.Null(await cache.Get(1, _ => null).WaitAsync(Timeout));

        await WaitUntilSize(cache);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Equal(0, cache.Stats().LoadSuccessCount);
    }

    [Fact]
    public async Task LoadingGet_LoaderReturnsNull_CompletesWithNullAndRemoves()
    {
        var notifications = new ConcurrentQueue<string>();
        var loader = new Loader();
        IAsyncLoadingCache<int, string> cache = NewBuilder(notifications).BuildAsync(loader);

        Assert.Null(await cache.Get(1));

        Assert.Equal(0, cache.EstimatedSize());
        Assert.Equal(1, cache.Stats().MissCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Equal(0, cache.Stats().LoadSuccessCount);
        Assert.Empty(notifications);

        // Not cached: the next Get loads again.
        Assert.Null(await cache.Get(1));
        Assert.Equal(2, loader.Loads);
        Assert.Equal(2, cache.Stats().MissCount);
        Assert.Equal(2, cache.Stats().LoadFailureCount);
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public async Task LoadingGet_LoaderCompletesNullLater_JoinedCallersGetNull()
    {
        var notifications = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource<string?>();
        var loader = new Loader { Load = _ => gate.Task };
        IAsyncLoadingCache<int, string> cache = NewBuilder(notifications).BuildAsync(loader);

        var first = cache.Get(1);
        var second = cache.Get(1);
        Assert.Same(first, second);

        gate.SetResult(null);

        Assert.Null(await first.WaitAsync(Timeout));
        Assert.Null(await second.WaitAsync(Timeout));
        await WaitUntilSize(cache);
        Assert.Equal(1, loader.Loads);
        Assert.Equal(1, cache.Stats().MissCount);
        Assert.Equal(1, cache.Stats().HitCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Equal(0, cache.Stats().LoadSuccessCount);
        Assert.Null(cache.GetIfPresent(1));
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task GetAll_JoiningAnInFlightLoadThatCompletesNull_OmitsTheKey()
    {
        var notifications = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource<string?>();
        var loader = new Loader { Load = k => k == 1 ? gate.Task : Task.FromResult<string?>("v" + k) };
        IAsyncLoadingCache<int, string> cache = NewBuilder(notifications).BuildAsync(loader);

        var single = cache.Get(1);
        Task<IReadOnlyDictionary<int, string>> all = cache.GetAll(new[] { 1, 2 });
        gate.SetResult(null);

        IReadOnlyDictionary<int, string> result = await all.WaitAsync(Timeout);
        Assert.Equal(new Dictionary<int, string> { [2] = "v2" }, result);
        Assert.Null(await single.WaitAsync(Timeout));
        await WaitUntilSize(cache, 1); // key 2 stays cached
        Assert.Null(cache.GetIfPresent(1));
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task GetAll_BulkLoadOmitsAKey_JoinedGetGetsNull()
    {
        var notifications = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource<IReadOnlyDictionary<int, string>>();
        var loader = new Loader { LoadAll = _ => gate.Task };
        IAsyncLoadingCache<int, string> cache = NewBuilder(notifications).BuildAsync(loader);

        Task<IReadOnlyDictionary<int, string>> all = cache.GetAll(new[] { 1, 2 });
        var joined = cache.Get(2);
        gate.SetResult(new Dictionary<int, string> { [1] = "v1" });

        Assert.Equal(new Dictionary<int, string> { [1] = "v1" }, await all.WaitAsync(Timeout));
        Assert.Null(await joined.WaitAsync(Timeout));
        Assert.Null(cache.GetIfPresent(2));
        Assert.Equal("v1", await cache.GetIfPresent(1)!);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task GetAll_BulkLoadOfANullKey_OmitsTheKey()
    {
        var notifications = new ConcurrentQueue<string>();
        var loader = new Loader { Load = k => Task.FromResult(k == 2 ? null : "v" + k) };
        IAsyncLoadingCache<int, string> cache = NewBuilder(notifications).BuildAsync(loader);

        IReadOnlyDictionary<int, string> result = await cache.GetAll(new[] { 1, 2, 3 }).WaitAsync(Timeout);

        Assert.Equal(new Dictionary<int, string> { [1] = "v1", [3] = "v3" }, result);
        await WaitUntilSize(cache, 2);
        Assert.Null(cache.GetIfPresent(2));
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Get_ConcurrentCallersJoinALoadThatCompletesNull_AllGetNull()
    {
        var notifications = new ConcurrentQueue<string>();
        IAsyncCache<int, string> cache = NewBuilder(notifications).BuildAsync();
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int loads = 0;
        var futures = new Task<string?>[16];
        using var start = new Barrier(futures.Length);

        var threads = new Task[futures.Length];
        for (int i = 0; i < threads.Length; i++)
        {
            int index = i;
            threads[i] = Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                futures[index] = cache.Get(1, (_, _) =>
                {
                    Interlocked.Increment(ref loads);
                    return gate.Task;
                });
            }, TaskCreationOptions.LongRunning);
        }
        await Task.WhenAll(threads).WaitAsync(Timeout);
        gate.SetResult(null);

        foreach (Task<string?> future in futures)
        {
            Assert.Same(futures[0], future);
            Assert.Null(await future.WaitAsync(Timeout));
        }
        await WaitUntilSize(cache);
        Assert.Equal(1, loads);
        Assert.Equal(1, cache.Stats().MissCount);
        Assert.Equal(futures.Length - 1, cache.Stats().HitCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Equal(0, cache.Stats().LoadSuccessCount);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Put_FutureCompletingNull_IsRemovedAndRecordedAsAFailure()
    {
        var notifications = new ConcurrentQueue<string>();
        IAsyncCache<int, string> cache = NewBuilder(notifications).BuildAsync();
        var gate = new TaskCompletionSource<string?>();

        cache.Put(1, gate.Task!);
        Assert.Same(gate.Task, cache.GetIfPresent(1));
        gate.SetResult(null);

        await WaitUntilSize(cache);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Equal(0, cache.Stats().LoadSuccessCount);
        Assert.Empty(notifications);
    }

    [Fact]
    public void Put_FutureAlreadyCompletedWithNull_IsRemovedAndRecordedAsAFailure()
    {
        var notifications = new ConcurrentQueue<string>();
        IAsyncCache<int, string> cache = NewBuilder(notifications).BuildAsync();

        cache.Put(1, Task.FromResult<string>(null!));

        Assert.Null(cache.GetIfPresent(1));
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Empty(notifications);
    }

    /// <summary>
    /// A future stored in the map that completed with null is not a value: like an in-flight load it
    /// keeps the async sentinel, so it neither expires nor is evicted before its completion removes it.
    /// </summary>
    [Fact]
    public void StoredFutureCompletedWithNull_IsTreatedAsInFlight()
    {
        long now = 0;
        IAsyncCache<int, string> cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(TimeSpan.FromSeconds(10))
            .ExpireAfterAccess(TimeSpan.FromSeconds(10))
            .Ticker(() => Interlocked.Read(ref now))
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .BuildAsync();
        var store = (BoundedLocalCache<int, Task<string>>)typeof(LocalAsyncCache<int, string>)
            .GetField("Cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        Task<string> nullFuture = Task.FromResult<string>(null!);

        // Bypass the completion handler, which would remove it at once.
        store.Put(1, nullFuture);
        Assert.Same(nullFuture, store.GetIfPresent(1, recordStats: false));
        Interlocked.Exchange(ref now, 20L * 1_000_000_000L);
        store.CleanUp();

        Assert.Same(nullFuture, store.GetIfPresent(1, recordStats: false));
        Assert.Equal(0, cache.Stats().EvictionCount);
    }
}

/// <summary>
/// Null async loads must not leave a faulted task for the finalizer to report: the caller's future
/// completes with null, so nothing faults. Runs alone, so no other test's unobserved task is counted.
/// </summary>
[CollectionDefinition(nameof(AsyncNullLoadUnobservedTest), DisableParallelization = true)]
[Collection(nameof(AsyncNullLoadUnobservedTest))]
public sealed class AsyncNullLoadUnobservedTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void NullLoads_DoNotRaiseUnobservedTaskExceptions()
    {
        CollectGarbage(); // flush anything left over from earlier tests
        var unobserved = new ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) => unobserved.Enqueue(e.Exception);
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            RunNullLoadsAndDropTheirTasks();
            CollectGarbage();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }

        Assert.Empty(unobserved);
    }

    private static void CollectGarbage()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// Starts null loads and drops their futures without observing them (waiting only for completion),
    /// so a future that faulted would be finalized unobserved once collected.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunNullLoadsAndDropTheirTasks()
    {
        IAsyncCache<int, string> cache = Cache.NewBuilder<int, string>()
            .MaximumSize(100)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();
        AwaitCompletion(cache.Get(1, _ => null));

        var gate = new TaskCompletionSource<string?>();
        var later = cache.Get(2, (_, _) => gate.Task);
        var joined = cache.Get(2, _ => "unused");
        gate.SetResult(null);
        AwaitCompletion(later);
        AwaitCompletion(joined);

        var loadGate = new TaskCompletionSource<string?>();
        IAsyncLoadingCache<int, string> loading = Cache.NewBuilder<int, string>()
            .MaximumSize(100)
            .Executor(DirectExecutor.Instance)
            .BuildAsync((k, _) => k == 1 ? loadGate.Task : Task.FromResult<string?>(null));
        AwaitCompletion(loading.Get(2));
        var inFlight = loading.Get(1);
        var all = loading.GetAll(new[] { 1, 3 });
        loadGate.SetResult(null);
        AwaitCompletion(inFlight);
        AwaitCompletion(all);

        Assert.True(SpinWait.SpinUntil(
            () => cache.GetIfPresent(2) == null && loading.GetIfPresent(1) == null, Timeout));
    }

    private static void AwaitCompletion(Task task)
        => Assert.True(((IAsyncResult)task).AsyncWaitHandle.WaitOne(Timeout));
}
