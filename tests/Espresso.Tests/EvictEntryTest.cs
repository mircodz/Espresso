using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>Tests for <c>BoundedLocalCache.EvictEntry</c> on retired nodes and refresh cleanup.</summary>
public class EvictEntryTest
{
    private static T Field<T>(object o, string name)
        => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static Node<K, V> NodeOf<K, V>(BoundedLocalCache<K, V> cache, K key)
        where K : notnull where V : class
        => Field<ConcurrentHashMap<K, Node<K, V>>>(cache, "_data").GetOrDefault(key)!;

    [Fact]
    public void EvictEntry_OnRetiredNode_StringKey_DoesNotThrow()
    {
        var cache = (BoundedLocalCache<string, string>)Cache.NewBuilder<string, string>()
            .MaximumSize(100).Executor(DirectExecutor.Instance).Build();
        ((ICache<string, string>)cache).Put("a", "1");
        Node<string, string> node = NodeOf(cache, "a");

        // A concurrent remove retires the node (removing it from the map) before maintenance reaches it.
        node.Retire();

        Assert.True(cache.EvictEntry(node, RemovalCause.Expired, 0L));
        Assert.True(node.IsDead);
    }

    [Fact]
    public void EvictEntry_DiscardsTheEvictedKeysRefreshToken()
    {
        long now = 0;
        var executor = new QueuedExecutor();
        var cache = (BoundedLocalCache<int, string>)Cache.NewBuilder<int, string>()
            .ExpireAfterWrite(TimeSpan.FromMinutes(10))
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(() => Volatile.Read(ref now))
            .Executor(executor)
            .Build(k => "L" + k);
        ILoadingCache<int, string> api = cache;
        api.Put(1, "v1");
        api.CleanUp();

        Interlocked.Add(ref now, TimeSpan.FromMinutes(2).Ticks * 100);
        Assert.Equal("v1", api.GetIfPresent(1)); // starts a refresh whose load stays queued
        var refreshes = Field<ConcurrentHashMap<object, object>>(cache, "_refreshes");
        Assert.Equal(1, refreshes.Count);

        Interlocked.Add(ref now, TimeSpan.FromMinutes(10).Ticks * 100);
        api.CleanUp(); // expires (evicts) key 1

        Assert.Null(api.GetIfPresent(1));
        Assert.Equal(0, refreshes.Count);
    }

    [Fact]
    public void SizeEviction_OfAnExpiredEntry_IsReportedAsExpired()
    {
        // Caffeine's evictEntry derives the cause under the node's lock: a victim chosen for size
        // that has already expired is evicted (and counted) as EXPIRED.
        var removals = new List<(int Key, string? Value, RemovalCause Cause)>();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumWeight(3)
            .Weigher((_, v) => v.Length)
            .ExpireAfter(new LifetimeByValue())
            .Ticker(() => 0L)
            .Executor(DirectExecutor.Instance)
            .RemovalListener((k, v, c) => removals.Add((k, v, c)))
            .RecordStats()
            .Build();

        cache.Put(3, "aa");    // lives six minutes
        cache.Put(2, "bbb");   // expires at once, and overflows the maximum

        Assert.Equal([(2, (string?)"bbb", RemovalCause.Expired)], removals);
        Assert.Equal(1, cache.Stats().EvictionCount);
        Assert.Equal(3, cache.Stats().EvictionWeight);
        Assert.Equal("aa", cache.GetIfPresent(3));
    }

    [Fact]
    public void SizeEviction_OfALiveEntry_IsReportedAsSize()
    {
        var removals = new List<(int Key, string? Value, RemovalCause Cause)>();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumWeight(3)
            .Weigher((_, v) => v.Length)
            .ExpireAfter(new LifetimeByValue())
            .Ticker(() => 0L)
            .Executor(DirectExecutor.Instance)
            .RemovalListener((k, v, c) => removals.Add((k, v, c)))
            .Build();

        cache.Put(3, "aa");
        cache.Put(2, "cc");    // lives six minutes, and overflows the maximum

        Assert.Single(removals);
        Assert.Equal(RemovalCause.Size, removals[0].Cause);
    }

    /// <summary>"bbb" expires immediately; every other value lives six minutes.</summary>
    private sealed class LifetimeByValue : IExpiry<int, string>
    {
        private static long Lifetime(string value) => value == "bbb" ? 0L : TimeSpan.FromMinutes(6).Ticks * 100;
        public long ExpireAfterCreate(int key, string value, long currentTime) => Lifetime(value);
        public long ExpireAfterUpdate(int key, string value, long currentTime, long currentDuration) => Lifetime(value);
        public long ExpireAfterRead(int key, string value, long currentTime, long currentDuration) => currentDuration;
    }

    private sealed class QueuedExecutor : IExecutor
    {
        public void Execute(Action command) { /* dropped: keeps work in flight */ }
    }
}
