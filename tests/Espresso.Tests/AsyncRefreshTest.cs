using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// An async refresh keeps the current completed value mapped while the reload is in flight, so readers
/// are never handed the pending reload. A successful reload replaces the value (notifying the old one as
/// replaced and restarting its timers), a failed reload leaves the entry exactly as it was (including its
/// write time), and a reload that resolves to null removes the entry.
/// </summary>
public sealed class AsyncRefreshTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed class Clock
    {
        private long _nanos;
        public long Read() => Interlocked.Read(ref _nanos);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _nanos, by.Ticks * 100L);
    }

    /// <summary>Loads "v1"; reloads through a replaceable function and counts the reloads.</summary>
    private sealed class Loader : IAsyncCacheLoader<int, string>
    {
        public Func<int, string, Task<string?>> Reload = (_, _) => Task.FromResult<string?>("v2");
        public int Loads;
        public int Reloads;

        public Task<string?> AsyncLoad(int key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Loads);
            return Task.FromResult<string?>("v1");
        }

        public Task<string?> AsyncReload(int key, string oldValue, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reloads);
            return Reload(key, oldValue);
        }
    }

    private sealed record Removal(int Key, string? Value, RemovalCause Cause);

    private static IAsyncLoadingCache<int, string> Build(
        Loader loader, Clock clock, ConcurrentQueue<Removal>? removals = null, TimeSpan? expireAfterWrite = null)
    {
        var builder = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(clock.Read)
            .Executor(DirectExecutor.Instance)
            .RecordStats();
        if (expireAfterWrite is TimeSpan duration)
        {
            builder = builder.ExpireAfterWrite(duration);
        }
        if (removals != null)
        {
            builder = builder.RemovalListener((k, v, c) => removals.Enqueue(new Removal(k, v, c)));
        }
        return builder.BuildAsync(loader);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task InFlightReload_OldValueStaysMappedAndCompleted()
    {
        var clock = new Clock();
        var loader = new Loader();
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Reload = (_, _) => gate.Task;
        var cache = Build(loader, clock);

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = cache.GetIfPresent(1); // starts the reload
        Assert.Equal(1, loader.Reloads);

        Task<string>? present = cache.GetIfPresent(1);
        Assert.NotNull(present);
        Assert.True(present!.IsCompletedSuccessfully);
        Assert.Equal("v1", await present);
        Task<string> loaded = cache.Get(1);
        Assert.True(loaded.IsCompletedSuccessfully);
        Assert.Equal("v1", await loaded);
        Assert.Equal(1, loader.Reloads); // the in-flight reload debounces later reads

        gate.SetResult("v2");
        await WaitUntil(() => cache.GetIfPresent(1) is { IsCompletedSuccessfully: true, Result: "v2" });
        Assert.Equal(2, cache.Stats().LoadSuccessCount);
    }

    [Fact]
    public async Task HungReload_DoesNotBlockReaders_AndTheEntryStillExpires()
    {
        var clock = new Clock();
        var loader = new Loader();
        var never = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Reload = (_, _) => never.Task;
        var cache = Build(loader, clock, expireAfterWrite: TimeSpan.FromMinutes(2));

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = cache.GetIfPresent(1); // starts a reload that never completes

        Task<string> served = cache.Get(1);
        Assert.True(served.IsCompletedSuccessfully);
        Assert.Equal("v1", await served);

        // The old value keeps its original write time, so it expires on schedule.
        clock.Advance(TimeSpan.FromSeconds(64)); // 125s after the write
        Assert.Null(cache.GetIfPresent(1));
        Task<string> reloaded = cache.Get(1);
        Assert.True(reloaded.IsCompletedSuccessfully);
        Assert.Equal(2, loader.Loads);
    }

    [Fact]
    public async Task FailedReload_KeepsOldValueAndItsWriteTime()
    {
        var clock = new Clock();
        var loader = new Loader();
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Reload = (_, _) => gate.Task;
        var cache = Build(loader, clock, expireAfterWrite: TimeSpan.FromMinutes(2));

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = cache.GetIfPresent(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        gate.SetException(new InvalidOperationException("reload failed"));
        await WaitUntil(() => cache.Stats().LoadFailureCount == 1);

        Assert.Equal("v1", await cache.GetIfPresent(1)!);

        // Expires two minutes after the original write, not after the failed reload.
        clock.Advance(TimeSpan.FromSeconds(63)); // 125s after the write
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public async Task FailedReload_AllowsALaterRefresh()
    {
        var clock = new Clock();
        var loader = new Loader();
        loader.Reload = (_, _) => Task.FromException<string?>(new InvalidOperationException("reload failed"));
        var cache = Build(loader, clock);

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("v1", await cache.GetIfPresent(1)!);
        Assert.Equal(1, loader.Reloads);
        Assert.Equal(1, cache.Stats().LoadFailureCount);

        loader.Reload = (_, _) => Task.FromResult<string?>("v2");
        Assert.Equal("v1", await cache.GetIfPresent(1)!); // still overdue, and no reload is registered
        Assert.Equal(2, loader.Reloads);
        Assert.Equal("v2", await cache.GetIfPresent(1)!);
    }

    [Fact]
    public async Task SynchronouslyThrowingReload_KeepsOldValue()
    {
        var clock = new Clock();
        var loader = new Loader();
        loader.Reload = (_, _) => throw new InvalidOperationException("reload failed to start");
        var cache = Build(loader, clock);

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        Task<string>? present = cache.GetIfPresent(1);
        Assert.Equal("v1", await present!);
        Assert.Equal("v1", await cache.GetIfPresent(1)!);
        Assert.Equal(2, loader.Reloads); // nothing stays registered after a failure to start
    }

    [Fact]
    public async Task SuccessfulReload_NotifiesReplacedAndRestartsTheWriteTime()
    {
        var clock = new Clock();
        var loader = new Loader();
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Reload = (_, _) => gate.Task;
        var removals = new ConcurrentQueue<Removal>();
        var cache = Build(loader, clock, removals, expireAfterWrite: TimeSpan.FromMinutes(2));

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = cache.GetIfPresent(1);
        clock.Advance(TimeSpan.FromSeconds(4));
        gate.SetResult("v2");
        await WaitUntil(() => !removals.IsEmpty);

        Assert.Equal(new[] { new Removal(1, "v1", RemovalCause.Replaced) }, removals.ToArray());
        Assert.Equal(2, cache.Stats().LoadSuccessCount);

        // The write time restarts at the commit (65s), so the entry outlives the original 120s.
        clock.Advance(TimeSpan.FromSeconds(60)); // 125s
        Assert.Equal("v2", await cache.GetIfPresent(1)!);
    }

    [Fact]
    public async Task SameInstanceReload_IsNotNotified()
    {
        var clock = new Clock();
        var loader = new Loader();
        var removals = new ConcurrentQueue<Removal>();
        loader.Reload = (_, old) => Task.FromResult<string?>(old);
        var cache = Build(loader, clock, removals);

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = cache.GetIfPresent(1);
        Assert.Equal(1, loader.Reloads);
        Assert.Equal("v1", await cache.GetIfPresent(1)!);

        // The same-instance reload still restarts the write time, so the entry is not overdue now.
        _ = cache.GetIfPresent(1);
        Assert.Equal(1, loader.Reloads);
        Assert.Empty(removals);
    }

    [Fact]
    public async Task NullReload_RemovesTheEntry()
    {
        var clock = new Clock();
        var loader = new Loader();
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Reload = (_, _) => gate.Task;
        var removals = new ConcurrentQueue<Removal>();
        var cache = Build(loader, clock, removals);

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = cache.GetIfPresent(1);
        gate.SetResult(null);
        await WaitUntil(() => !removals.IsEmpty);

        Assert.Equal(new[] { new Removal(1, "v1", RemovalCause.Explicit) }, removals.ToArray());
        Assert.Equal(1, cache.Stats().LoadFailureCount);
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public async Task ReloadCompletingAfterAWrite_IsDiscarded()
    {
        var clock = new Clock();
        var loader = new Loader();
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Reload = (_, _) => gate.Task;
        var removals = new ConcurrentQueue<Removal>();
        var cache = Build(loader, clock, removals);

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = cache.GetIfPresent(1);
        cache.Put(1, Task.FromResult("put"));
        gate.SetResult("v2");
        await WaitUntil(() => removals.Count == 2);

        Assert.Equal("put", await cache.GetIfPresent(1)!);
        Assert.Equal(
            new[] { new Removal(1, "v1", RemovalCause.Replaced), new Removal(1, "v2", RemovalCause.Replaced) },
            removals.ToArray());
    }

    [Fact]
    public async Task ReloadCompletingAfterAnInvalidate_IsDiscarded()
    {
        var clock = new Clock();
        var loader = new Loader();
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Reload = (_, _) => gate.Task;
        var removals = new ConcurrentQueue<Removal>();
        var cache = Build(loader, clock, removals);

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = cache.GetIfPresent(1);
        cache.Invalidate(1);
        gate.SetResult("v2");
        await WaitUntil(() => removals.Count == 2);

        Assert.Null(cache.GetIfPresent(1));
        Assert.Equal(
            new[] { new Removal(1, "v1", RemovalCause.Explicit), new Removal(1, "v2", RemovalCause.Explicit) },
            removals.ToArray());
    }

    [Fact]
    public async Task ReloadCompletingAfterTheEntryExpired_IsDiscarded()
    {
        var clock = new Clock();
        var loader = new Loader();
        var gate = new TaskCompletionSource<string?>(); // the commit runs inline on completion
        loader.Reload = (_, _) => gate.Task;
        var removals = new ConcurrentQueue<Removal>();
        var cache = Build(loader, clock, removals, expireAfterWrite: TimeSpan.FromMinutes(2));

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal("v1", await cache.GetIfPresent(1)!); // starts the reload
        clock.Advance(TimeSpan.FromSeconds(90)); // expired, not yet removed
        gate.SetResult("v2");

        Assert.Null(cache.GetIfPresent(1));
        Assert.Equal(
            new[] { new Removal(1, "v1", RemovalCause.Expired), new Removal(1, "v2", RemovalCause.Explicit) },
            removals.ToArray());
        Assert.Equal(1, cache.Stats().EvictionCount);
    }

    [Fact]
    public async Task DiscardedReload_OfTheValueAWriteStored_IsNotNotified()
    {
        var clock = new Clock();
        var loader = new Loader();
        var gate = new TaskCompletionSource<string?>(); // the commit runs inline on completion
        loader.Reload = (_, _) => gate.Task;
        var removals = new ConcurrentQueue<Removal>();
        var cache = Build(loader, clock, removals);
        string stored = new('s', 3);

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = cache.GetIfPresent(1); // starts the reload
        cache.Put(1, Task.FromResult(stored));
        gate.SetResult(stored);

        Assert.Same(stored, await cache.GetIfPresent(1)!);
        Assert.Equal(new[] { new Removal(1, "v1", RemovalCause.Replaced) }, removals.ToArray());
    }
}
