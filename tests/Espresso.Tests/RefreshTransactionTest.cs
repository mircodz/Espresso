using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// An automatic refresh commits transactionally: it validates and mutates the entry under the entry's
/// lock, evaluates user callbacks before mutating, retires the node on a null reload, reports a
/// discarded reload, and always releases its own registration without failing the reader.
/// </summary>
public class RefreshTransactionTest
{
    private static readonly long Hour = TimeSpan.FromHours(1).Ticks * 100L;

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

    private sealed class SwallowingExecutor : IExecutor
    {
        public int Thrown;
        public void Execute(Action command)
        {
            try { command(); }
            catch { Interlocked.Increment(ref Thrown); }
        }
    }

    private sealed class ToggleRejectingExecutor : IExecutor
    {
        public bool Reject = true;
        public void Execute(Action command)
        {
            if (Reject) throw new InvalidOperationException("rejected");
            command();
        }
    }

    private sealed class ThrowOnceUpdateExpiry : IExpiry<int, string>
    {
        public bool ThrowOnUpdate = true;
        public long ExpireAfterCreate(int key, string value, long currentTime) => Hour;
        public long ExpireAfterUpdate(int key, string value, long currentTime, long currentDuration)
        {
            if (ThrowOnUpdate)
            {
                ThrowOnUpdate = false;
                throw new InvalidOperationException("expiry boom");
            }
            return currentDuration;
        }
        public long ExpireAfterRead(int key, string value, long currentTime, long currentDuration) => currentDuration;
    }

    private sealed class GatedUpdateExpiry : IExpiry<int, string>
    {
        public volatile bool Armed;
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public long ExpireAfterCreate(int key, string value, long currentTime) => Hour;
        public long ExpireAfterUpdate(int key, string value, long currentTime, long currentDuration)
        {
            if (Armed)
            {
                Armed = false;
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            return currentDuration;
        }
        public long ExpireAfterRead(int key, string value, long currentTime, long currentDuration) => currentDuration;
    }

    private sealed class ReloadingAsyncLoader(Func<string, Task<string?>> reload) : IAsyncCacheLoader<int, string>
    {
        public Task<string?> AsyncLoad(int key, CancellationToken ct) => Task.FromResult<string?>("v1");
        public Task<string?> AsyncReload(int key, string oldValue, CancellationToken ct) => reload(oldValue);
    }

    private static T Field<T>(object cache, string name)
        => (T)cache.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;

    private static long RefreshCount(object cache) => Field<ConcurrentHashMap<object, object>>(cache, "_refreshes").Count;

    private static Node<int, string>? NodeOf(object cache, int key)
        => Field<ConcurrentHashMap<int, Node<int, string>>>(cache, "_data").GetOrDefault(key);

    // ----- failure paths release the registration and never reach the reader (C31) -----

    [Fact]
    public void ThrowingCommit_LeavesEntryUntouchedAndReleasesRegistration()
    {
        var clock = new Clock();
        int calls = 0;
        var executor = new SwallowingExecutor();
        var cache = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .ExpireAfter(new ThrowOnceUpdateExpiry())
            .Ticker(clock.Read).Executor(executor)
            .Build(k => $"{k}-v{++calls}");

        cache.Put(1, "initial");
        clock.Advance(TimeSpan.FromSeconds(61));
        cache.GetIfPresent(1); // reload; the commit throws in ExpireAfterUpdate

        Assert.Equal(0, executor.Thrown);
        Assert.Equal(1, calls);
        Assert.Equal("initial", ((BoundedLocalCache<int, string>)cache).GetIfPresentQuietly(1));
        Assert.Equal(0L, RefreshCount(cache));

        clock.Advance(TimeSpan.FromSeconds(61));
        cache.GetIfPresent(1); // a new reload must be armed
        Assert.Equal(2, calls);
        Assert.Equal("1-v2", cache.GetIfPresent(1));
    }

    [Fact]
    public void ThrowingWeigherOnCommit_LeavesEntryUntouched()
    {
        var clock = new Clock();
        var executor = new SwallowingExecutor();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumWeight(100)
            .Weigher((_, v) => v == "BAD" ? throw new InvalidOperationException("weigh") : 1)
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(clock.Read).Executor(executor)
            .Build(_ => "BAD");

        cache.Put(1, "initial");
        clock.Advance(TimeSpan.FromSeconds(61));
        cache.GetIfPresent(1);

        Assert.Equal(0, executor.Thrown);
        Assert.Equal("initial", cache.GetIfPresent(1));
        Assert.Equal(1, NodeOf(cache, 1)!.Weight);
        Assert.Equal(0L, RefreshCount(cache));
    }

    [Fact]
    public void ExecutorRejection_DoesNotThrowToReaderOrLeakRegistration()
    {
        var clock = new Clock();
        var executor = new ToggleRejectingExecutor();
        var cache = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(clock.Read).Executor(executor)
            .Build(_ => "reloaded");

        cache.Put(1, "initial");
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(Record.Exception(() => cache.GetIfPresent(1)));
        Assert.Equal(0L, RefreshCount(cache));

        executor.Reject = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        cache.GetIfPresent(1); // re-arms the refresh
        Assert.Equal("reloaded", cache.GetIfPresent(1));
    }

    [Fact]
    public async Task Async_ThrowingWeigherOnInstall_DoesNotThrowToReaderAndPreservesEntry()
    {
        var clock = new Clock();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumWeight(100)
            .Weigher((_, v) => v == "BAD" ? throw new InvalidOperationException("weigh") : 1)
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(clock.Read).Executor(DirectExecutor.Instance)
            .BuildAsync(new ReloadingAsyncLoader(_ => Task.FromResult<string?>("BAD")));

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(await Record.ExceptionAsync(async () => await cache.Get(1)));
        Assert.Equal("v1", await cache.GetIfPresent(1)!);
    }

    // ----- the commit serializes with the node-locked Put (C15, refresh part) -----

    [Fact]
    public async Task Commit_SerializesWithPut()
    {
        var clock = new Clock();
        var expiry = new GatedUpdateExpiry();
        var cache = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .ExpireAfter(expiry)
            .Ticker(clock.Read).Executor(DirectExecutor.Instance)
            .Build(_ => "reloaded");
        cache.Put(1, "initial");
        clock.Advance(TimeSpan.FromSeconds(61));

        expiry.Armed = true;
        Task reader = DedicatedThread.Run(() => cache.GetIfPresent(1)); // the reload commits inline and pauses
        Assert.True(expiry.Entered.Wait(TimeSpan.FromSeconds(10)));

        Task put = DedicatedThread.Run(() => cache.Put(1, "put"));
        await Task.WhenAny(put, Task.Delay(200)); // the Put must wait for the in-flight commit
        expiry.Release.Set();
        await reader;
        await put;

        Assert.Equal("put", cache.GetIfPresent(1));
    }

    // ----- a null reload retires the node (C32) -----

    [Fact]
    public void NullReload_RetiresAndUnlinksNode()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        var removals = new List<(string?, RemovalCause)>();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(10).RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(clock.Read).Executor(executor)
            .RemovalListener((_, v, c) => removals.Add((v, c)))
            .Build(_ => null);
        cache.Put(1, "initial");
        executor.RunAll();
        Node<int, string> node = NodeOf(cache, 1)!;

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("initial", cache.GetIfPresent(1));
        executor.RunAll(); // the reload returns null
        executor.RunAll();

        Assert.Equal(0, cache.EstimatedSize());
        Assert.False(node.IsAlive);
        Assert.Equal(0L, Field<long>(cache, "_weightedSize"));
        Assert.Equal(0L, RefreshCount(cache));
        Assert.Equal(new[] { ((string?)"initial", RemovalCause.Explicit) }, removals);
    }

    // ----- the commit resets the access time -----

    [Fact]
    public void Commit_ResetsAccessTime()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterAccess(TimeSpan.FromMinutes(1))
            .RefreshAfterWrite(TimeSpan.FromSeconds(30))
            .Ticker(clock.Read).Executor(executor)
            .Build(_ => "reloaded");
        cache.Put(1, "initial");
        clock.Advance(TimeSpan.FromSeconds(31));
        cache.GetIfPresent(1); // the reload is queued
        clock.Advance(TimeSpan.FromSeconds(58)); // t = 89 s
        executor.RunAll();
        clock.Advance(TimeSpan.FromSeconds(11)); // t = 100 s
        cache.CleanUp();

        Assert.Equal(1, cache.EstimatedSize());
    }

    // ----- a discarded reload is reported (C34) -----

    [Fact]
    public void SupersededReload_NotifiesReloadedValueAsReplaced()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        var removals = new List<(string?, RemovalCause)>();
        var cache = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(clock.Read).Executor(executor)
            .RemovalListener((_, v, c) => removals.Add((v, c)))
            .Build(_ => "reloaded");
        cache.Put(1, "a");
        clock.Advance(TimeSpan.FromSeconds(61));
        cache.GetIfPresent(1); // the reload is queued
        cache.Put(1, "b");     // supersedes it
        executor.RunAll();
        executor.RunAll();

        Assert.Equal("b", cache.GetIfPresent(1));
        Assert.Contains(("reloaded", RemovalCause.Replaced), removals);
        Assert.Equal(0L, RefreshCount(cache));
    }

    [Fact]
    public void ReloadOfRemovedEntry_NotifiesReloadedValueAsExplicit()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        var removals = new List<(string?, RemovalCause)>();
        var cache = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(clock.Read).Executor(executor)
            .RemovalListener((_, v, c) => removals.Add((v, c)))
            .Build(_ => "reloaded");
        cache.Put(1, "a");
        clock.Advance(TimeSpan.FromSeconds(61));
        cache.GetIfPresent(1); // the reload is queued
        cache.Invalidate(1);
        executor.RunAll();
        executor.RunAll();

        Assert.Null(cache.GetIfPresent(1));
        Assert.Contains(("reloaded", RemovalCause.Explicit), removals);
        Assert.Equal(0L, RefreshCount(cache));
    }

    [Fact]
    public async Task Async_SupersededReload_NotifiesReloadedValueAsReplaced()
    {
        var clock = new Clock();
        var removals = new List<(string?, RemovalCause)>();
        IAsyncLoadingCache<int, string>? cache = null;
        var reload = new TaskCompletionSource<string?>();
        cache = Cache.NewBuilder<int, string>()
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(clock.Read).Executor(DirectExecutor.Instance)
            .RemovalListener((_, v, c) => { lock (removals) removals.Add((v, c)); })
            .BuildAsync(new ReloadingAsyncLoader(_ =>
            {
                cache!.Put(1, Task.FromResult("b")); // a write lands while the reload starts
                return reload.Task;
            }));

        Assert.Equal("v1", await cache.Get(1));
        clock.Advance(TimeSpan.FromSeconds(61));
        await cache.Get(1); // starts the reload, which is superseded before it is installed
        reload.SetResult("reloaded");

        Assert.Equal("b", await cache.GetIfPresent(1)!);
        bool notified = false;
        for (int i = 0; i < 500 && !notified; i++)
        {
            lock (removals)
            {
                notified = removals.Contains(("reloaded", RemovalCause.Replaced));
            }
            if (!notified) await Task.Delay(10); // delivered once the reloaded future completes
        }
        Assert.True(notified);
    }
}
