using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Evicting an entry discards its refresh while the key's bin is still locked (as Caffeine does inside
/// its <c>computeIfPresent</c>), so the discard can never remove the refresh registration of an entry
/// that was re-inserted for the same key after the eviction released the bin.
/// </summary>
public class EvictionRefreshDiscardTest
{
    private const long Second = 1_000_000_000L;

    private sealed class Clock
    {
        public long Now;
        public long Read() => Interlocked.Read(ref Now);
    }

    private sealed class ManualExecutor : IExecutor
    {
        private readonly Queue<Action> _tasks = new();
        public void Execute(Action action)
        {
            lock (_tasks) _tasks.Enqueue(action);
        }
    }

    /// <summary>
    /// A key that parks the evicting thread while the refresh registry hashes it during the eviction
    /// (detected by a <see cref="ConcurrentHashMap{TKey,TValue}"/> removal frame under
    /// <c>EvictEntry</c>), giving the test a deterministic window at the refresh discard.
    /// </summary>
    private sealed class HookKey : IEquatable<HookKey>
    {
        public static volatile int EvictorThread = -1;
        public static readonly ManualResetEventSlim Parked = new();
        public static readonly ManualResetEventSlim Resume = new();

        private readonly int _id;
        public HookKey(int id) => _id = id;

        public override int GetHashCode()
        {
            if (Environment.CurrentManagedThreadId == EvictorThread && IsRefreshDiscardUnderEviction())
            {
                EvictorThread = -1;
                Parked.Set();
                if (!Resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            return _id;
        }

        private static bool IsRefreshDiscardUnderEviction()
        {
            bool removing = false, evicting = false;
            foreach (StackFrame frame in new StackTrace(false).GetFrames())
            {
                MethodBase? method = frame.GetMethod();
                if (method == null) continue;
                if (method.Name == "ReplaceNode" && method.DeclaringType?.Name.StartsWith("ConcurrentHashMap", StringComparison.Ordinal) == true)
                {
                    removing = true;
                }
                else if (method.Name == "EvictEntry")
                {
                    evicting = true;
                }
            }
            return removing && evicting;
        }

        public bool Equals(HookKey? other) => other != null && other._id == _id;
        public override bool Equals(object? obj) => Equals(obj as HookKey);
    }

    private static long RefreshCount(object cache)
        => ((ConcurrentHashMap<object, object>)typeof(BoundedLocalCache<HookKey, string>)
            .GetField("_refreshes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!).Count;

    [Fact]
    public async Task Eviction_DoesNotCancelARefreshOfAReinsertedEntry()
    {
        var clock = new Clock();
        var cache = (BoundedLocalCache<HookKey, string>)Cache.NewBuilder<HookKey, string>()
            .Ticker(clock.Read).Executor(new ManualExecutor())
            .ExpireAfterWrite(TimeSpan.FromSeconds(10)).RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .Build(_ => "reloaded");

        cache.Put(new HookKey(1), "old");
        cache.CleanUp(); // link the entry into the write-order deque
        clock.Now = 20 * Second; // expired

        // The evictor expires the old entry and parks inside the refresh discard for its key.
        Task evictor = DedicatedThread.Run(() =>
        {
            HookKey.EvictorThread = Environment.CurrentManagedThreadId;
            cache.CleanUp();
        });
        Assert.True(HookKey.Parked.Wait(TimeSpan.FromSeconds(10)), "the eviction never reached the refresh discard");

        // A successor is written for the same key and becomes due for refresh, which registers a reload.
        Task successor = DedicatedThread.Run(() =>
        {
            cache.Put(new HookKey(1), "new");
            Interlocked.Exchange(ref clock.Now, 25 * Second);
            Assert.Equal("new", cache.GetIfPresent(new HookKey(1), recordStats: false));
        });
        // With the discard inside the eviction's bin lock the successor waits for the eviction to
        // finish; with the discard after it, the successor registers its refresh while the evictor is parked.
        await Task.WhenAny(successor, Task.Delay(300));

        HookKey.Resume.Set();
        await evictor;
        await successor;

        Assert.Equal("new", cache.GetIfPresentQuietly(new HookKey(1)));
        Assert.Equal(1, RefreshCount(cache)); // the successor's reload is still registered
    }
}
