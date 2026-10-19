using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

public sealed class UnboundedCacheNotificationTest
{
    [Fact]
    public void RemovalNotifications_UseConfiguredExecutor()
    {
        var executor = new QueuedExecutor();
        var events = new List<(string?, string?, RemovalCause)>();
        using var cache = Cache.NewBuilder<string, string>()
            .Executor(executor)
            .RemovalListener((key, value, cause) => events.Add((key, value, cause)))
            .Build(_ => "refreshed");

        cache.Put("key", "first");
        cache.Put("key", "second");
        cache.Refresh("key");
        cache.Invalidate("key");
        cache.Put("another", "last");
        cache.InvalidateAll();

        Assert.Empty(events);
        Assert.Equal(4, executor.Calls);
        executor.RunAll();
        Assert.Equal(new (string?, string?, RemovalCause)[]
        {
            ("key", "first", RemovalCause.Replaced),
            ("key", "second", RemovalCause.Replaced),
            ("key", "refreshed", RemovalCause.Explicit),
            ("another", "last", RemovalCause.Explicit),
        }, events);
        Assert.Equal(0, cache.EstimatedSize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingListener_IsIsolatedWithQueuedOrRejectingExecutor(bool reject)
    {
        var executor = new QueuedExecutor { Reject = reject };
        int calls = 0;
        using var cache = Cache.NewBuilder<string, string>()
            .Executor(executor)
            .RemovalListener((_, _, _) =>
            {
                calls++;
                throw new InvalidOperationException("listener failed");
            })
            .Build();

        cache.Put("key", "first");
        Assert.Null(Record.Exception(() => cache.Put("key", "second")));
        Assert.Null(Record.Exception(() => cache.Invalidate("key")));
        Assert.Equal(2, executor.Calls);
        Assert.Equal(reject ? 2 : 0, calls);
        Assert.Null(Record.Exception(executor.RunAll));
        Assert.Equal(2, calls);
        Assert.Equal(0, cache.EstimatedSize());
    }

    [Fact]
    public void ThrowingListener_IsIsolatedWithDirectExecutor()
    {
        int calls = 0;
        using var cache = Cache.NewBuilder<string, string>()
            .Executor(DirectExecutor.Instance)
            .RemovalListener((_, _, _) =>
            {
                calls++;
                throw new InvalidOperationException("listener failed");
            })
            .Build();

        cache.Put("key", "first");
        Assert.Null(Record.Exception(() => cache.Put("key", "second")));
        Assert.Null(Record.Exception(() => cache.Invalidate("key")));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RemovalNotification_DefaultExecutorRunsOnThreadPool()
    {
        var notificationThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = Cache.NewBuilder<string, string>()
            .RemovalListener((_, _, _) => notificationThread.SetResult(Environment.CurrentManagedThreadId))
            .Build();
        int callerThread = 0;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            cache.Put("key", "value");
            cache.Invalidate("key");
        }) { IsBackground = true };

        caller.Start();
        int observedThread = await notificationThread.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(caller.Join(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(callerThread, observedThread);
    }

    [Fact]
    public void QueuedNotification_DoesNotRetainCache()
    {
        var executor = new QueuedExecutor();
        var events = new List<(string?, string?, RemovalCause)>();
        WeakReference cache = QueueNotification(executor, events);
        Assert.Single(executor.Tasks);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(cache.IsAlive);
        executor.RunAll();
        Assert.Equal(("key", "value", RemovalCause.Explicit), Assert.Single(events));
        GC.KeepAlive(executor);
    }

    [Fact]
    public void WithoutListener_DoesNotDispatchNotifications()
    {
        var executor = new QueuedExecutor();
        using var cache = Cache.NewBuilder<string, string>().Executor(executor).Build();
        cache.Put("key", "first");
        cache.Put("key", "second");
        cache.Invalidate("key");
        Assert.Equal(0, executor.Calls);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference QueueNotification(
        QueuedExecutor executor, List<(string?, string?, RemovalCause)> events)
    {
        var cache = Cache.NewBuilder<string, string>()
            .Executor(executor)
            .RemovalListener((key, value, cause) => events.Add((key, value, cause)))
            .Build();
        cache.Put("key", "value");
        cache.Invalidate("key");
        return new WeakReference(cache);
    }

    private sealed class QueuedExecutor : IExecutor
    {
        internal readonly Queue<Action> Tasks = new();
        internal bool Reject;
        internal int Calls;

        public void Execute(Action command)
        {
            Calls++;
            if (Reject)
            {
                throw new InvalidOperationException("executor rejected notification");
            }
            Tasks.Enqueue(command);
        }

        internal void RunAll()
        {
            while (Tasks.TryDequeue(out Action? command))
            {
                command();
            }
        }
    }
}
