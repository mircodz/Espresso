using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// An async loader is started only after the map's bin lock is released. A .NET async method runs
/// synchronously until its first incomplete await, so the loader's synchronous prefix must not run
/// under the bin lock: a dependent load of a key in the same bin must stay mapped, a re-entrant load
/// of the same key must coalesce onto the in-flight load, and writers to the bin must not wait for
/// the prefix. Under the lock the cache installs only an in-flight placeholder, which the loader's
/// outcome completes afterwards.
/// </summary>
public sealed class AsyncLoaderOutsideLockTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A key whose instances all hash to the same bin.</summary>
    private sealed class Colliding
    {
        public Colliding(int id) => Id = id;
        public int Id { get; }
        public override int GetHashCode() => 42;
        public override bool Equals(object? obj) => obj is Colliding other && other.Id == Id;
        public override string ToString() => "k" + Id;
    }

    private static IAsyncCache<TKey, string> NewCache<TKey>() where TKey : notnull
        => Cache.NewBuilder<TKey, string>()
            .MaximumSize(100)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .BuildAsync();

    /// <summary>Records each submission, then runs it inline.</summary>
    private sealed class RecordingExecutor(List<string> events) : IExecutor
    {
        public void Execute(Action action)
        {
            lock (events)
            {
                events.Add("maintenance");
            }
            action();
        }
    }

    [Fact]
    public async Task Loader_StartsBeforeTheInsertionsMaintenanceIsSubmitted()
    {
        var events = new List<string>();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(100)
            .Executor(new RecordingExecutor(events))
            .BuildAsync();

        Task<string> loaded = cache.Get(1, (_, _) =>
        {
            lock (events)
            {
                events.Add("loader");
            }
            return Task.FromResult<string?>("v");
        });

        Assert.Equal("v", await loaded.WaitAsync(Timeout));
        lock (events)
        {
            Assert.Equal("loader", events[0]);
        }
    }

    [Fact]
    public async Task DependentLoadInSameBin_IsNotLost()
    {
        var cache = NewCache<Colliding>();
        cache.Put(new Colliding(0), Task.FromResult("zero")); // a non-empty bin

        Task<string> child = cache.Get(new Colliding(1), async (_, _) =>
        {
            string parent = await cache.Get(new Colliding(2), (_, _) => Task.FromResult<string?>("parent"));
            return "child-of-" + parent;
        });

        Assert.Equal("child-of-parent", await child.WaitAsync(Timeout));
        Assert.Equal("parent", await cache.GetIfPresent(new Colliding(2))!.WaitAsync(Timeout));
        Assert.Equal("zero", await cache.GetIfPresent(new Colliding(0))!.WaitAsync(Timeout));
        Assert.Equal(3, cache.EstimatedSize());
    }

    [Fact]
    public async Task DependentLoadInEmptyBin_IsNotLost()
    {
        var cache = NewCache<Colliding>();

        Task<string> child = cache.Get(new Colliding(1), async (_, _) =>
        {
            string parent = await cache.Get(new Colliding(2), (_, _) => Task.FromResult<string?>("parent"));
            return "child-of-" + parent;
        });

        Assert.Equal("child-of-parent", await child.WaitAsync(Timeout));
        Assert.Equal("parent", await cache.GetIfPresent(new Colliding(2))!.WaitAsync(Timeout));
        Assert.Equal(2, cache.EstimatedSize());
    }

    [Fact]
    public async Task ReentrantLoadOfSameKey_CoalescesOntoTheInFlightLoad()
    {
        var cache = NewCache<int>();
        int innerLoads = 0;
        Task<string>? inner = null;

        Task<string> outer = cache.Get(1, (k, _) =>
        {
            inner = cache.Get(k, (_, _) =>
            {
                Interlocked.Increment(ref innerLoads);
                return Task.FromResult<string?>("inner");
            });
            return Task.FromResult<string?>("outer");
        });

        Assert.Equal("outer", await outer.WaitAsync(Timeout));
        Assert.Same(outer, inner);
        Assert.Equal(0, innerLoads);
        Assert.Equal("outer", await cache.GetIfPresent(1)!.WaitAsync(Timeout));
    }

    [Fact]
    public async Task LoaderSynchronousPrefix_DoesNotBlockWritersToTheBin()
    {
        var cache = NewCache<Colliding>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        Task<Task<string>> loading = Task.Factory.StartNew(() => cache.Get(new Colliding(1), (_, _) =>
        {
            entered.SetResult();
            release.Wait(Timeout); // a blocking synchronous prefix
            return Task.FromResult<string?>("loaded");
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await entered.Task.WaitAsync(Timeout);

        Task writer = Task.Factory.StartNew(() => cache.Put(new Colliding(2), Task.FromResult("written")),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        bool writerFinished = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(10))) == writer;
        release.Set();

        Assert.True(writerFinished, "a write to the loading key's bin waited for the loader");
        Assert.Equal("loaded", await (await loading.WaitAsync(Timeout)).WaitAsync(Timeout));
        Assert.Equal("written", await cache.GetIfPresent(new Colliding(2))!.WaitAsync(Timeout));
    }

    [Fact]
    public async Task ConcurrentGets_LoadOnceAndShareTheFuture()
    {
        var cache = NewCache<int>();
        int loads = 0;
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var futures = new Task<string>[16];
        using var start = new Barrier(futures.Length);

        var threads = new Task[futures.Length];
        for (int i = 0; i < futures.Length; i++)
        {
            int index = i;
            threads[i] = Task.Factory.StartNew(() =>
            {
                start.SignalAndWait(Timeout);
                futures[index] = cache.Get(1, (_, _) =>
                {
                    Interlocked.Increment(ref loads);
                    return gate.Task;
                });
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        await Task.WhenAll(threads).WaitAsync(Timeout);
        gate.SetResult("v");

        Assert.Equal(1, loads);
        foreach (Task<string> future in futures)
        {
            Assert.Same(futures[0], future);
            Assert.Equal("v", await future.WaitAsync(Timeout));
        }
        Assert.Equal(1, cache.Stats().MissCount);
        Assert.Equal(futures.Length - 1, cache.Stats().HitCount);
    }

    [Fact]
    public void LoaderThrowsSynchronously_PropagatesAndLeavesNoEntry()
    {
        var cache = NewCache<int>();

        Assert.Throws<InvalidOperationException>(
            () => { _ = cache.Get(1, (Func<int, CancellationToken, Task<string?>>)((_, _) => throw new InvalidOperationException("boom"))); });

        Assert.Equal(1, cache.Stats().MissCount);
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public void LoaderReturnsNullTask_ThrowsAndLeavesNoEntry()
    {
        var cache = NewCache<int>();

        Assert.Throws<ArgumentNullException>(() => { _ = cache.Get(1, (_, _) => null!); });

        Assert.Null(cache.GetIfPresent(1));
        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("cancel")]
    [InlineData("null")]
    public async Task UnsuccessfulLoad_RemovesEntryAndRecordsFailure(string outcome)
    {
        var cache = NewCache<int>();
        var load = new TaskCompletionSource<string?>();

        Task<string> future = cache.Get(1, (_, _) => load.Task);
        switch (outcome)
        {
            case "fault": load.SetException(new InvalidOperationException("boom")); break;
            case "cancel": load.SetCanceled(); break;
            default: load.SetResult(null); break;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => future.WaitAsync(Timeout));
        // The completion records the failure, then removes the entry, so the removal implies both.
        for (int i = 0; i < 400 && cache.GetIfPresent(1) != null; i++)
        {
            await Task.Delay(5);
        }
        Assert.Null(cache.GetIfPresent(1));
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        if (outcome == "cancel")
        {
            Assert.True(future.IsCanceled);
        }
    }
}
