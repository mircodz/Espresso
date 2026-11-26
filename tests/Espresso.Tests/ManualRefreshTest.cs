using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// <see cref="ILoadingCache{K,V}.Refresh"/> follows Caffeine's <c>LoadingCache.refresh</c>, on both the
/// bounded and the unbounded cache: the refresh is registered before its loader runs on the executor,
/// joins a refresh already in flight for the key, and installs its value only if nothing modified the
/// entry in the meantime. The outcome is reported through the returned task.
/// </summary>
public sealed class ManualRefreshTest
{
    public static IEnumerable<object[]> Both() => new[] { new object[] { false }, new object[] { true } };

    private static readonly long Minute = TimeSpan.FromMinutes(1).Ticks * 100L;

    private sealed class Clock
    {
        private long _now;
        public long Read() => Interlocked.Read(ref _now);
        public void Advance(long nanos) => Interlocked.Add(ref _now, nanos);
    }

    /// <summary>Queues every task (loads, notifications, maintenance) until <see cref="RunAll"/>.</summary>
    private sealed class ManualExecutor : IExecutor
    {
        private readonly Queue<Action> _tasks = new();
        public bool Reject;
        public int Pending => _tasks.Count;

        public void Execute(Action command)
        {
            if (Reject)
            {
                throw new InvalidOperationException("rejected");
            }
            _tasks.Enqueue(command);
        }

        public void RunAll()
        {
            while (_tasks.Count != 0)
            {
                _tasks.Dequeue()();
            }
        }
    }

    private sealed class Harness
    {
        public readonly List<(int Key, string? Value, RemovalCause Cause)> Removals = new();
        public Func<int, string?> OnLoad = k => "v" + k;
        public int Loads;
        public ILoadingCache<int, string> Cache = null!;

        public string? Load(int key)
        {
            Interlocked.Increment(ref Loads);
            return OnLoad(key);
        }
    }

    private static Harness Build(bool bounded, IExecutor? executor = null,
        Func<EspressoBuilder<int, string>, EspressoBuilder<int, string>>? configure = null)
    {
        var harness = new Harness();
        EspressoBuilder<int, string> builder = Cache.NewBuilder<int, string>()
            .Executor(executor ?? DirectExecutor.Instance)
            .RecordStats()
            .RemovalListener((k, v, cause) =>
            {
                lock (harness.Removals)
                {
                    harness.Removals.Add((k, v, cause));
                }
            });
        if (bounded)
        {
            builder.MaximumSize(100);
        }
        builder = configure?.Invoke(builder) ?? builder;
        harness.Cache = builder.Build(harness.Load);
        return harness;
    }

    private static T ThrowsSynchronously<T>(Func<Task> call)
        where T : Exception
    {
        try
        {
            _ = call();
        }
        catch (T e)
        {
            return e;
        }
        Assert.Fail($"Expected {typeof(T).Name} to be thrown synchronously");
        return null!;
    }

    [Theory]
    [MemberData(nameof(Both))]
    public void Refresh_NullKey_ThrowsBeforeInvokingTheLoader(bool bounded)
    {
        int loads = 0;
        EspressoBuilder<string, string> builder = Cache.NewBuilder<string, string>()
            .Executor(DirectExecutor.Instance).RecordStats();
        if (bounded)
        {
            builder.MaximumSize(100);
        }
        using ILoadingCache<string, string> cache = builder.Build(_ => { loads++; return "v"; });

        var error = ThrowsSynchronously<ArgumentNullException>(() => cache.Refresh(null!));

        Assert.Equal("key", error.ParamName);
        Assert.Equal(0, loads);
        Assert.Equal(0, cache.Stats().LoadCount);
        Assert.Equal(0, cache.EstimatedSize());
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_PresentKey_InstallsTheReloadInline(bool bounded)
    {
        Harness h = Build(bounded);
        h.Cache.Put(1, "old");

        Task<string?> refresh = h.Cache.Refresh(1);

        Assert.True(refresh.IsCompletedSuccessfully); // a direct executor completes it inline
        Assert.Equal("v1", await refresh);
        Assert.Equal("v1", h.Cache.GetIfPresent(1));
        Assert.Equal(new[] { (1, (string?)"old", RemovalCause.Replaced) }, h.Removals);
        Assert.Equal(1, h.Cache.Stats().LoadSuccessCount);
        Assert.Equal(0, h.Cache.Stats().LoadFailureCount);
        Assert.Equal(0, h.Cache.Stats().MissCount);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_AbsentKey_LoadsAndInserts(bool bounded)
    {
        Harness h = Build(bounded);

        Assert.Equal("v1", await h.Cache.Refresh(1));

        Assert.Equal("v1", h.Cache.GetIfPresent(1));
        Assert.Empty(h.Removals);
        Assert.Equal(1, h.Cache.Stats().LoadSuccessCount);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_SameInstanceReload_IsNotReportedAsReplaced(bool bounded)
    {
        Harness h = Build(bounded);
        const string value = "same";
        h.OnLoad = _ => value;
        h.Cache.Put(1, value);

        Assert.Same(value, await h.Cache.Refresh(1));

        Assert.Same(value, h.Cache.GetIfPresent(1));
        Assert.Empty(h.Removals);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_NullReload_RemovesTheEntry(bool bounded)
    {
        Harness h = Build(bounded);
        h.Cache.Put(1, "old");
        h.OnLoad = _ => null;

        Assert.Null(await h.Cache.Refresh(1));

        Assert.Null(h.Cache.GetIfPresent(1));
        Assert.Equal(new[] { (1, (string?)"old", RemovalCause.Explicit) }, h.Removals);
        Assert.Equal(1, h.Cache.Stats().LoadFailureCount);
        Assert.Equal(0, h.Cache.Stats().LoadSuccessCount);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_AbsentKey_NullReload_InsertsNothing(bool bounded)
    {
        Harness h = Build(bounded);
        h.OnLoad = _ => null;

        Assert.Null(await h.Cache.Refresh(1));

        Assert.Equal(0, h.Cache.EstimatedSize());
        Assert.Empty(h.Removals);
        Assert.Equal(1, h.Cache.Stats().LoadFailureCount);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_LoaderThrows_FaultsTheTaskWithoutThrowing(bool bounded)
    {
        Harness h = Build(bounded);
        h.Cache.Put(1, "old");
        var failure = new InvalidOperationException("boom");
        h.OnLoad = _ => throw failure;

        Task<string?> refresh = h.Cache.Refresh(1);

        Assert.True(refresh.IsFaulted);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => refresh));
        Assert.Equal("old", h.Cache.GetIfPresent(1));
        Assert.Empty(h.Removals);
        Assert.Equal(1, h.Cache.Stats().LoadFailureCount);
        Assert.Equal(0, h.Cache.Stats().LoadSuccessCount);

        // The failed refresh released its registration.
        h.OnLoad = _ => "new";
        Assert.Equal("new", await h.Cache.Refresh(1));
        Assert.Equal("new", h.Cache.GetIfPresent(1));
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_RecordsTheLoadTimeWithTheStatsTicker(bool bounded)
    {
        var clock = new Clock();
        Harness h = Build(bounded, configure: b => b.Ticker(clock.Read));
        h.OnLoad = k =>
        {
            clock.Advance(42);
            return "v" + k;
        };

        await h.Cache.Refresh(1);

        Assert.Equal(1, h.Cache.Stats().LoadSuccessCount);
        Assert.Equal(42, h.Cache.Stats().TotalLoadTime);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_NewerPutDuringTheReload_Wins(bool bounded)
    {
        Harness h = Build(bounded);
        h.Cache.Put(1, "old");
        // The loader runs outside every lock, so the racing write can run on the loader's thread.
        h.OnLoad = k =>
        {
            h.Cache.Put(k, "newer");
            return "stale";
        };

        Assert.Equal("stale", await h.Cache.Refresh(1));

        Assert.Equal("newer", h.Cache.GetIfPresent(1));
        Assert.Equal(new[]
        {
            (1, (string?)"old", RemovalCause.Replaced),
            (1, (string?)"stale", RemovalCause.Replaced),
        }, h.Removals);
        Assert.Equal(1, h.Cache.Stats().LoadSuccessCount);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_NewerPutFromAnotherThreadDuringTheReload_Wins(bool bounded)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Harness h = Build(bounded);
        h.OnLoad = _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException();
            }
            return "stale";
        };
        h.Cache.Put(1, "original");

        Task<Task<string?>> refresh = Task.Run<Task<string?>>(() => h.Cache.Refresh(1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        h.Cache.Put(1, "newer");
        release.Set();

        Assert.Equal("stale", await await refresh);
        Assert.Equal("newer", h.Cache.GetIfPresent(1));
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_SameInstancePutDuringTheReload_Wins(bool bounded)
    {
        Harness h = Build(bounded);
        const string old = "old";
        h.Cache.Put(1, old);
        h.OnLoad = k =>
        {
            h.Cache.Put(k, old); // a write of the very value the refresh read still supersedes it
            return "stale";
        };

        await h.Cache.Refresh(1);

        Assert.Same(old, h.Cache.GetIfPresent(1));
        Assert.Equal(new[] { (1, (string?)"stale", RemovalCause.Replaced) }, h.Removals);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_InvalidateDuringTheReload_DoesNotResurrectTheEntry(bool bounded)
    {
        Harness h = Build(bounded);
        h.Cache.Put(1, "old");
        h.OnLoad = k =>
        {
            h.Cache.Invalidate(k);
            return "stale";
        };

        Assert.Equal("stale", await h.Cache.Refresh(1));

        Assert.Null(h.Cache.GetIfPresent(1));
        Assert.Equal(new[]
        {
            (1, (string?)"old", RemovalCause.Explicit),
            (1, (string?)"stale", RemovalCause.Explicit),
        }, h.Removals);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_AbsentKey_InvalidateDuringTheLoad_InsertsNothing(bool bounded)
    {
        Harness h = Build(bounded);
        h.OnLoad = k =>
        {
            h.Cache.Invalidate(k);
            return "stale";
        };

        await h.Cache.Refresh(1);

        Assert.Null(h.Cache.GetIfPresent(1));
        Assert.Equal(new[] { (1, (string?)"stale", RemovalCause.Explicit) }, h.Removals);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_AbsentKey_InvalidateAllDuringTheLoad_InsertsNothing(bool bounded)
    {
        Harness h = Build(bounded);
        h.OnLoad = _ =>
        {
            h.Cache.InvalidateAll();
            return "stale";
        };

        await h.Cache.Refresh(1);

        Assert.Null(h.Cache.GetIfPresent(1));
        Assert.Equal(new[] { (1, (string?)"stale", RemovalCause.Explicit) }, h.Removals);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_AbsentKey_PutDuringTheLoad_Wins(bool bounded)
    {
        Harness h = Build(bounded);
        h.OnLoad = k =>
        {
            h.Cache.Put(k, "newer");
            return "stale";
        };

        await h.Cache.Refresh(1);

        Assert.Equal("newer", h.Cache.GetIfPresent(1));
        Assert.Equal(new[] { (1, (string?)"stale", RemovalCause.Replaced) }, h.Removals);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_ConcurrentRefreshes_Coalesce(bool bounded)
    {
        var executor = new ManualExecutor();
        Harness h = Build(bounded, executor);
        h.Cache.Put(1, "old");

        Task<string?> first = h.Cache.Refresh(1);
        Task<string?> second = h.Cache.Refresh(1);

        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        executor.RunAll();
        Assert.Equal(1, h.Loads);
        Assert.Equal("v1", await first);
        Assert.Equal("v1", h.Cache.GetIfPresent(1));

        // Once it completed, a refresh loads again.
        Task<string?> third = h.Cache.Refresh(1);
        Assert.NotSame(first, third);
        executor.RunAll();
        Assert.Equal(2, h.Loads);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_ThreadPoolExecutor_CompletesAfterTheCommit(bool bounded)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Harness h = Build(bounded, ThreadPoolExecutor.Instance);
        h.OnLoad = k =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException();
            }
            return "v" + k;
        };
        h.Cache.Put(1, "old");

        Task<string?> first = h.Cache.Refresh(1);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task<string?> second = h.Cache.Refresh(1);
        Assert.Same(first, second);
        Assert.Equal("old", h.Cache.GetIfPresent(1)); // served while reloading
        release.Set();

        Assert.Equal("v1", await first);
        Assert.Equal("v1", h.Cache.GetIfPresent(1));
        Assert.Equal(1, h.Loads);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_RejectedByTheExecutor_FaultsTheTaskWithoutThrowing(bool bounded)
    {
        var executor = new ManualExecutor();
        Harness h = Build(bounded, executor);
        h.Cache.Put(1, "old");
        executor.RunAll();

        executor.Reject = true;
        Task<string?> refresh = h.Cache.Refresh(1);
        executor.Reject = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
        Assert.Equal(0, h.Loads);
        Assert.Equal("old", h.Cache.GetIfPresent(1));

        // The rejected refresh released its registration.
        Task<string?> retry = h.Cache.Refresh(1);
        executor.RunAll();
        Assert.Equal("v1", await retry);
    }

    [Theory]
    [MemberData(nameof(Both))]
    public async Task Refresh_LoaderRefreshingItsKey_IsRejected(bool bounded)
    {
        Harness h = Build(bounded);
        h.Cache.Put(1, "old");
        h.OnLoad = k =>
        {
            // Stop after one reentry so a regression never overflows the test host's stack.
            if (h.Loads == 1)
            {
                _ = h.Cache.Refresh(k);
            }
            return "loaded";
        };

        Task<string?> refresh = h.Cache.Refresh(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
        Assert.Equal(1, h.Loads);
        Assert.Equal("old", h.Cache.GetIfPresent(1));
        Assert.Equal("loaded", await h.Cache.Refresh(1)); // nothing was left registered
    }

    // ----- expiration (bounded) -----

    private static Harness BuildExpiring(Clock clock, IExecutor? executor = null)
        => Build(bounded: true, executor, b => b.ExpireAfterWrite(TimeSpan.FromMinutes(1)).Ticker(clock.Read));

    [Fact]
    public async Task Refresh_ExpiredKey_LoadsAnewAndReportsTheExpiredValue()
    {
        var clock = new Clock();
        Harness h = BuildExpiring(clock);
        h.Cache.Put(1, "old");
        clock.Advance(2 * Minute);

        Assert.Equal("v1", await h.Cache.Refresh(1));

        Assert.Equal("v1", h.Cache.GetIfPresent(1));
        Assert.Equal(new[] { (1, (string?)"old", RemovalCause.Expired) }, h.Removals);
        Assert.Equal(1, h.Cache.Stats().EvictionCount);
        Assert.Equal(1, h.Cache.Stats().LoadSuccessCount);
    }

    [Fact]
    public async Task Refresh_ExpiredKey_NullReload_RemovesTheExpiredEntry()
    {
        var clock = new Clock();
        Harness h = BuildExpiring(clock);
        h.Cache.Put(1, "old");
        clock.Advance(2 * Minute);
        h.OnLoad = _ => null;

        Assert.Null(await h.Cache.Refresh(1));

        Assert.Equal(0, h.Cache.EstimatedSize());
        Assert.Equal(new[] { (1, (string?)"old", RemovalCause.Expired) }, h.Removals);
    }

    [Fact]
    public async Task Refresh_EntryExpiringDuringTheReload_DiscardsTheReload()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        Harness h = BuildExpiring(clock, executor);
        h.Cache.Put(1, "old");

        Task<string?> refresh = h.Cache.Refresh(1);
        clock.Advance(2 * Minute);
        executor.RunAll();

        Assert.Equal("v1", await refresh);
        Assert.Null(h.Cache.GetIfPresent(1));
        Assert.Equal(new[]
        {
            (1, (string?)"old", RemovalCause.Expired),
            (1, (string?)"v1", RemovalCause.Explicit),
        }, h.Removals);
    }

    [Fact]
    public async Task Refresh_ExpiredKey_ConcurrentRefreshesCoalesce()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        Harness h = BuildExpiring(clock, executor);
        h.Cache.Put(1, "old");
        clock.Advance(2 * Minute);

        Task<string?> first = h.Cache.Refresh(1);
        Task<string?> second = h.Cache.Refresh(1);
        Task<string?> third = h.Cache.Refresh(1);

        Assert.Same(first, second);
        Assert.Same(first, third);
        executor.RunAll();
        Assert.Equal(1, h.Loads);
        Assert.Equal("v1", await first);
        Assert.Equal("v1", h.Cache.GetIfPresent(1));
    }

    [Fact]
    public async Task Refresh_ExpiredKey_JoinsARefreshWhoseLoaderIsRunningInline()
    {
        // With an inline executor the first refresh is still being submitted while its loader runs;
        // a refresh from another thread joins it instead of replacing it and loading again.
        var clock = new Clock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Harness h = BuildExpiring(clock);
        h.OnLoad = _ =>
        {
            if (Interlocked.CompareExchange(ref h.Loads, 0, 0) == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException();
                }
            }
            return "reload";
        };
        h.Cache.Put(1, "old");
        clock.Advance(2 * Minute);

        Task<Task<string?>> first = Task.Run<Task<string?>>(() => h.Cache.Refresh(1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task<string?> second = await Task.Run<Task<string?>>(() => h.Cache.Refresh(1));
        release.Set();

        Assert.Same(await first, second);
        Assert.Equal("reload", await second);
        Assert.Equal(1, h.Loads);
        Assert.Equal("reload", h.Cache.GetIfPresent(1));
    }

    [Fact]
    public async Task Refresh_ExpiredKey_LoaderRefreshingItsKey_IsRejected()
    {
        var clock = new Clock();
        Harness h = BuildExpiring(clock);
        h.OnLoad = k =>
        {
            if (h.Loads == 1)
            {
                _ = h.Cache.Refresh(k);
            }
            return "loaded";
        };
        h.Cache.Put(1, "old");
        clock.Advance(2 * Minute);

        Task<string?> refresh = h.Cache.Refresh(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
        Assert.Equal(1, h.Loads);
        Assert.Null(h.Cache.GetIfPresent(1));
        Assert.Equal(new[] { (1, (string?)"old", RemovalCause.Expired) }, h.Removals);
    }

    // ----- refresh-after-write (bounded) -----

    private static Harness BuildRefreshing(Clock clock, ManualExecutor executor, bool expires = false)
        => Build(bounded: true, executor, b =>
        {
            b.RefreshAfterWrite(TimeSpan.FromMinutes(1)).Ticker(clock.Read);
            return expires ? b.ExpireAfterWrite(TimeSpan.FromMinutes(5)) : b;
        });

    [Fact]
    public async Task Refresh_JoinsAnInFlightAutomaticRefresh()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        Harness h = BuildRefreshing(clock, executor);
        h.Cache.Put(1, "old");
        executor.RunAll();
        clock.Advance(2 * Minute);
        Assert.Equal("old", h.Cache.GetIfPresent(1)); // starts the automatic reload

        Task<string?> refresh = h.Cache.Refresh(1);

        Assert.False(refresh.IsCompleted);
        executor.RunAll();
        Assert.Equal(1, h.Loads);
        Assert.Equal("v1", await refresh);
        Assert.Equal("v1", h.Cache.GetIfPresent(1));
    }

    [Fact]
    public async Task Refresh_InFlight_SuppressesAnAutomaticRefresh()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        Harness h = BuildRefreshing(clock, executor);
        h.Cache.Put(1, "old");
        executor.RunAll();
        clock.Advance(2 * Minute);

        Task<string?> refresh = h.Cache.Refresh(1);
        Assert.Equal("old", h.Cache.GetIfPresent(1)); // overdue, but a refresh is already in flight
        executor.RunAll();

        Assert.Equal(1, h.Loads);
        Assert.Equal("v1", await refresh);
        Assert.Equal("v1", h.Cache.GetIfPresent(1));
    }

    [Fact]
    public async Task Refresh_ReplacesAnAutomaticRefreshOfAnEntryThatExpired()
    {
        var clock = new Clock();
        var executor = new ManualExecutor();
        Harness h = BuildRefreshing(clock, executor, expires: true);
        h.OnLoad = _ => "load" + h.Loads;
        h.Cache.Put(1, "old");
        executor.RunAll();
        clock.Advance(2 * Minute);
        Assert.Equal("old", h.Cache.GetIfPresent(1)); // starts the automatic reload
        clock.Advance(10 * Minute); // the entry expires while that reload is in flight

        Task<string?> refresh = h.Cache.Refresh(1);
        executor.RunAll();

        Assert.Equal(2, h.Loads);
        Assert.Equal("load2", await refresh);
        Assert.Equal("load2", h.Cache.GetIfPresent(1));
        Assert.Equal(new[]
        {
            (1, (string?)"old", RemovalCause.Expired),
            (1, (string?)"load1", RemovalCause.Explicit), // the superseded automatic reload
        }, h.Removals);
    }
}
