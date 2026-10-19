using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class ExpirationTest : CacheTestBase
{
    private sealed class FakeTicker
    {
        private long _nanos;
        public long Read() => _nanos;
        public void Advance(TimeSpan by) => _nanos += by.Ticks * 100L;
    }

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FixedExpiration_MaxValueDuration_DoesNotExpireImmediately(bool access)
    {
        var builder = Cache.NewBuilder<int, string>()
            .Ticker(() => 0L)
            .Executor(DirectExecutor.Instance);
        if (access) builder.ExpireAfterAccess(TimeSpan.MaxValue);
        else builder.ExpireAfterWrite(TimeSpan.MaxValue);
        using var cache = builder.Build();

        cache.Put(1, "v");

        Assert.Equal("v", cache.GetIfPresent(1));
        Assert.Equal(1, cache.EstimatedSize());
    }

    [Fact]
    public void ExpireAfterWrite_EvictsAfterDuration()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .Build();

        cache.Put(1, "a");
        Assert.Equal("a", cache.GetIfPresent(1));

        ticker.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("a", cache.GetIfPresent(1)); // not yet expired

        ticker.Advance(TimeSpan.FromSeconds(31)); // total 61s > 60s
        Assert.Null(cache.GetIfPresent(1)); // logically expired -> absent

        cache.CleanUp();
        Assert.Equal(0, cache.EstimatedSize()); // physically evicted
    }

    [Fact]
    public void ExpireAfterWrite_NotResetByReads()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        for (int i = 0; i < 5; i++)
        {
            ticker.Advance(TimeSpan.FromSeconds(15));
            cache.GetIfPresent(1); // reads must NOT extend a write-based expiry
        }

        // 75s elapsed since write -> expired despite the reads.
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public void ExpireAfterWrite_ResetByUpdate()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        ticker.Advance(TimeSpan.FromSeconds(45));
        cache.Put(1, "b"); // re-write resets the clock
        ticker.Advance(TimeSpan.FromSeconds(45)); // 45s since the update

        Assert.Equal("b", cache.GetIfPresent(1));
    }

    [Fact]
    public void ExpireAfterAccess_ResetByReads()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterAccess(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        for (int i = 0; i < 5; i++)
        {
            ticker.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal("a", cache.GetIfPresent(1)); // each read extends the access expiry
        }

        // Now idle past the duration.
        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(cache.GetIfPresent(1));
    }

    [Fact]
    public void ExpireAfterAccess_EvictsIdleEntries()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterAccess(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        ticker.Advance(TimeSpan.FromSeconds(61));

        cache.CleanUp();
        Assert.Equal(0, cache.EstimatedSize());
    }

    [Fact]
    public void Expiration_NotifiesExpiredCause()
    {
        var ticker = new FakeTicker();
        RemovalCause? observed = null;
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .RemovalListener(new Listener((_, _, cause) => observed = cause))
            .Build();

        cache.Put(1, "a");
        ticker.Advance(TimeSpan.FromSeconds(61));
        cache.CleanUp();

        Assert.Equal(RemovalCause.Expired, observed);
    }

    [Fact]
    public void Expiration_WithMaximumSize_BothApply()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(100)
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build();

        for (int i = 0; i < 50; i++)
        {
            cache.Put(i, "v" + i);
        }
        Assert.Equal("v10", cache.GetIfPresent(10));

        ticker.Advance(TimeSpan.FromSeconds(61));

        // all expired despite being under the size bound
        cache.CleanUp();
        Assert.Equal(0, cache.EstimatedSize());
    }

    [Fact]
    public void ExpireAfterWrite_ReAddAfterExpiry()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        ticker.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(cache.GetIfPresent(1));

        cache.Put(1, "b"); // re-insert after expiry
        Assert.Equal("b", cache.GetIfPresent(1));
    }

    [Fact]
    public void Get_RecomputesExpiredEntry()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build();

        int calls = 0;
        Assert.Equal("v1", cache.Get(1, k => { calls++; return "v" + calls; }));
        ticker.Advance(TimeSpan.FromSeconds(61));

        // Expired -> the mapping function runs again.
        Assert.Equal("v2", cache.Get(1, k => { calls++; return "v" + calls; }));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Put_OverExpiredEntry_ReturnsNull_NotStale()
    {
        var ticker = new FakeTicker();
        RemovalCause? cause = null;
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .RemovalListener(new Listener((_, _, c) => cause = c))
            .Build();

        cache.Put(1, "old");
        ticker.Advance(TimeSpan.FromSeconds(61)); // expired
        Assert.Null(cache.GetIfPresent(1));

        cache.Put(1, "new");
        Assert.Equal("new", cache.GetIfPresent(1));
        Assert.NotEqual(RemovalCause.Replaced, cause); // expired, not replaced
    }

    [Fact]
    public void Remove_ExpiredEntry_ReturnsNull()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "old");
        ticker.Advance(TimeSpan.FromSeconds(61));
        cache.Invalidate(1);

        Assert.Null(cache.GetIfPresent(1));
    }

    // Regression: GetAllPresent must honor expiration like every other read path — an expired entry
    // must be a miss (not leaked to the caller, not counted as a hit).
    [Fact]
    public void GetAllPresent_SkipsExpiredEntries()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .Build();

        cache.Put(1, "a");
        cache.Put(2, "b");
        ticker.Advance(TimeSpan.FromSeconds(30));
        cache.Put(3, "c"); // fresher than 1 and 2

        // Age past 1 and 2's expiry (61s) but within 3's (31s since its write).
        ticker.Advance(TimeSpan.FromSeconds(31));

        var present = cache.GetAllPresent(new[] { 1, 2, 3 });
        Assert.False(present.ContainsKey(1), "expired entry 1 leaked");
        Assert.False(present.ContainsKey(2), "expired entry 2 leaked");
        Assert.Equal("c", present[3]);
        Assert.Single(present);

        // Only the live hit is recorded as a hit; the two expired keys are misses.
        Assert.Equal(1, cache.Stats().HitCount);
        Assert.Equal(2, cache.Stats().MissCount);
    }

    // GetAllPresent under expireAfterAccess must extend the entries it reads (not let them die
    // despite being accessed).
    [Fact]
    public void GetAllPresent_ExtendsAccessedEntries()
    {
        var ticker = new FakeTicker();
        var cache = Cache.NewBuilder<int, string>()
            .ExpireAfterAccess(OneMinute)
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build();

        cache.Put(1, "a");
        // Read via GetAllPresent every 40s; the access must keep the entry alive past 60s.
        for (int i = 0; i < 3; i++)
        {
            ticker.Advance(TimeSpan.FromSeconds(40));
            var present = cache.GetAllPresent(new[] { 1 });
            Assert.Equal("a", present[1]);
        }
    }

    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private static void SetField(object instance, string name, object value)
        => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static void AssertDequeConsistent<T>(ILinkedDeque<Node<int, T>> deque, long count, int? queueType = null)
        where T : class
    {
        var seen = new HashSet<Node<int, T>>();
        Node<int, T>? previous = null;
        for (var node = deque.PeekFirst; node != null; node = deque.GetNext(node))
        {
            Assert.True(seen.Add(node), "cycle in expiration deque");
            Assert.True(node.IsAlive);
            Assert.Same(previous, deque.GetPrevious(node));
            if (queueType.HasValue) Assert.Equal(queueType.Value, node.QueueType);
            previous = node;
        }
        Assert.Same(previous, deque.PeekLast);
        Assert.Equal(count, seen.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FixedScan_ReentrantRemoval_DoesNotReorderDetachedSuccessor(bool access)
    {
        long now = 0L;
        bool reentered = false;
        ICache<int, string>? cache = null;
        var builder = Cache.NewBuilder<int, string>().Ticker(() => now)
            .Executor(DirectExecutor.Instance)
            .RemovalListener(new Listener((key, _, cause) =>
            {
                if (key != 1 || cause != RemovalCause.Expired || reentered) return;
                reentered = true;
                cache!.Put(2, "renewed");
                cache.Invalidate(2);
                cache.CleanUp();
            }));
        if (access) builder.ExpireAfterAccess(OneMinute);
        else builder.ExpireAfterWrite(OneMinute);
        cache = builder.Build();
        using (cache)
        {
            cache.Put(1, "a");
            now = TimeSpan.FromSeconds(30).Ticks * 100L;
            cache.Put(2, "b");
            cache.Put(3, "c");
            now = TimeSpan.FromSeconds(45).Ticks * 100L;
            cache.Put(4, "d");
            now = TimeSpan.FromSeconds(61).Ticks * 100L;

            cache.CleanUp();

            Assert.True(reentered);
            Assert.Equal(2, cache.EstimatedSize());
            var deque = Field<ILinkedDeque<Node<int, string>>>(cache,
                access ? "_windowDeque" : "_writeOrderDeque");
            AssertDequeConsistent(deque, cache.EstimatedSize());
            now = TimeSpan.FromSeconds(122).Ticks * 100L;
            cache.CleanUp();
            Assert.Equal(0, cache.EstimatedSize());
            AssertDequeConsistent(deque, 0);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task FixedScan_ReentrantRenewal_TerminatesWithOrWithoutCapturedTail(bool access, bool removeTail)
    {
        long now = 0L;
        bool reentered = false;
        ICache<int, string>? cache = null;
        var builder = Cache.NewBuilder<int, string>().Ticker(() => Volatile.Read(ref now))
            .Executor(DirectExecutor.Instance)
            .RemovalListener(new Listener((key, _, cause) =>
            {
                if (key != 1 || cause != RemovalCause.Expired || reentered) return;
                reentered = true;
                cache!.Put(2, "b");
                cache.Put(3, "c");
                if (removeTail) cache.Invalidate(4);
                cache.CleanUp();
            }));
        if (access) builder.ExpireAfterAccess(OneMinute);
        else builder.ExpireAfterWrite(OneMinute);
        cache = builder.Build();
        using (cache)
        {
            cache.Put(1, "a");
            now = TimeSpan.FromSeconds(30).Ticks * 100L;
            cache.Put(2, "b");
            cache.Put(3, "c");
            now = TimeSpan.FromSeconds(45).Ticks * 100L;
            cache.Put(4, "d");
            var deque = Field<ILinkedDeque<Node<int, string>>>(cache,
                access ? "_windowDeque" : "_writeOrderDeque");
            var capturedTail = deque.PeekLast!;
            now = TimeSpan.FromSeconds(61).Ticks * 100L;

            Task cleanup = Task.Run(cache.CleanUp);
            bool terminated = await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(2))) == cleanup;
            if (!terminated)
            {
                // Release the broken baseline scan before asserting; never abandon a spinning worker.
                long recoveryTime = TimeSpan.FromSeconds(62).Ticks * 100L;
                Volatile.Write(ref now, recoveryTime);
                capturedTail.AccessTime = recoveryTime;
                capturedTail.WriteTime = recoveryTime;
            }
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(terminated, "expiration scan rotated renewed successors indefinitely");
            Assert.True(reentered);
            Assert.Equal(removeTail ? 2 : 3, cache.EstimatedSize());
            AssertDequeConsistent(deque, cache.EstimatedSize());
            Volatile.Write(ref now, TimeSpan.FromSeconds(123).Ticks * 100L);
            cache.CleanUp();
            Assert.Equal(0, cache.EstimatedSize());
            AssertDequeConsistent(deque, 0);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FixedScan_PendingAsyncHead_DoesNotHideExpiredEntry(bool access)
    {
        long now = 0L;
        var builder = Cache.NewBuilder<int, string>().Ticker(() => now).Executor(DirectExecutor.Instance);
        if (access) builder.ExpireAfterAccess(OneMinute);
        else builder.ExpireAfterWrite(OneMinute);
        using var cache = builder.BuildAsync();
        var first = new TaskCompletionSource<string>();
        var last = new TaskCompletionSource<string>();
        cache.Put(1, first.Task);
        cache.Put(2, Task.FromResult("expired"));
        now = TimeSpan.FromSeconds(1).Ticks * 100L;
        cache.Put(3, last.Task);
        object store = Field<object>(cache, "Cache");
        var deque = Field<ILinkedDeque<Node<int, Task<string>>>>(store,
            access ? "_windowDeque" : "_writeOrderDeque");
        Node<int, Task<string>>? pendingHead = null;
        foreach (var node in deque) if (node.Key == 1) pendingHead = node;
        Assert.NotNull(pendingHead);
        deque.MoveToFront(pendingHead);
        now = TimeSpan.FromSeconds(61).Ticks * 100L;

        cache.CleanUp();

        Assert.Equal(2, cache.EstimatedSize());
        AssertDequeConsistent(deque, 2);
        Assert.Same(first.Task, cache.GetIfPresent(1));
        Assert.Same(last.Task, cache.GetIfPresent(3));
    }

    [Fact]
    public void AccessScan_TransferredTail_RearmsEvenWhenLinksRemainLive()
    {
        long now = 0L;
        bool reentered = false;
        ICache<int, string>? cache = null;
        ILinkedDeque<Node<int, string>>? window = null;
        ILinkedDeque<Node<int, string>>? probation = null;
        Node<int, string>? capturedTail = null;
        cache = Cache.NewBuilder<int, string>().MaximumSize(100).ExpireAfterAccess(OneMinute)
            .Ticker(() => now).Executor(DirectExecutor.Instance)
            .RemovalListener(new Listener((key, _, cause) =>
            {
                if (key != 1 || cause != RemovalCause.Expired || reentered) return;
                reentered = true;
                Assert.True(window!.Remove(capturedTail!));
                capturedTail!.MakeMainProbation();
                probation!.OfferLast(capturedTail);
                SetField(cache!, "_windowWeightedSize",
                    Field<long>(cache!, "_windowWeightedSize") - capturedTail.PolicyWeight);
            })).Build();
        using (cache)
        {
            SetField(cache, "_windowMaximum", 100L);
            cache.Put(1, "a");
            now = TimeSpan.FromSeconds(30).Ticks * 100L;
            cache.Put(2, "b");
            cache.Put(3, "c");
            now = TimeSpan.FromSeconds(45).Ticks * 100L;
            cache.Put(4, "d");
            cache.Put(5, "e");
            window = Field<ILinkedDeque<Node<int, string>>>(cache, "_windowDeque");
            probation = Field<ILinkedDeque<Node<int, string>>>(cache, "_probationDeque");
            var staged = window.PollLast()!;
            staged.MakeMainProbation();
            probation.OfferLast(staged);
            SetField(cache, "_windowWeightedSize", Field<long>(cache, "_windowWeightedSize") - staged.PolicyWeight);
            capturedTail = window.PeekLast!;
            now = TimeSpan.FromSeconds(61).Ticks * 100L;
            Assert.Equal(0, Field<int>(cache, "_drainStatus"));

            lock (Field<object>(cache, "_evictionLock"))
            {
                typeof(BoundedLocalCache<int, string>).GetMethod("ExpireAfterAccessEntries",
                    BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(long) }, null)!
                    .Invoke(cache, new object[] { now });
            }

            Assert.True(reentered);
            Assert.Equal(3, Field<int>(cache, "_drainStatus"));
            AssertDequeConsistent(window, 2, Node<int, string>.Window);
            AssertDequeConsistent(probation, 2, Node<int, string>.Probation);
            Assert.Equal(4, cache.EstimatedSize());
        }
    }

    private sealed class Listener : IRemovalListener<int, string>
    {
        private readonly Action<int, string?, RemovalCause> _onRemoval;
        public Listener(Action<int, string?, RemovalCause> onRemoval) => _onRemoval = onRemoval;
        public void OnRemoval(int key, string? value, RemovalCause cause) => _onRemoval(key, value, cause);
    }
}
