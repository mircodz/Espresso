using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class ConcurrentHashMapRecursiveUpdateTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecursiveEmptyBinUpdateMustThrow(bool compute)
    {
        var map = new ConcurrentHashMap<int, string>();
        string? Mapping(int _)
        {
            map.Put(16, "inner");
            return null;
        }

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (compute) map.Compute(0, (key, _) => Mapping(key));
            else map.ComputeIfAbsent(0, Mapping);
        });
        Assert.Equal("Recursive update", error.Message);
        Assert.Null(map.GetOrDefault(0));
        Assert.Null(map.GetOrDefault(16));
        Assert.Equal(0, map.Count);
        Assert.Empty(map.AsEnumerable());
        map.Put(16, "retry");
        Assert.Equal("retry", map.GetOrDefault(16));
        Assert.Equal(1, map.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecursiveAppendMustNotSilentlyOverwriteInnerInsert(bool compute)
    {
        var map = new ConcurrentHashMap<int, string>(64);
        map.Put(0, "head");
        string Mapping(int _)
        {
            map.Put(128, "inner");
            return "outer";
        }

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (compute) map.Compute(64, (key, _) => Mapping(key));
            else map.ComputeIfAbsent(64, Mapping);
        });
        Assert.Equal("Recursive update", error.Message);
        Assert.Equal("head", map.GetOrDefault(0));
        Assert.Equal("inner", map.GetOrDefault(128));
        Assert.Null(map.GetOrDefault(64));
        Assert.Equal(2, map.Count);
        Assert.Equal(map.Count, map.AsEnumerable().LongCount());
    }

    [Theory]
    [InlineData("Put")]
    [InlineData("PutIfAbsent")]
    [InlineData("Remove")]
    [InlineData("Replace")]
    [InlineData("ComputeIfAbsent")]
    [InlineData("Compute")]
    [InlineData("ComputeIfPresent")]
    [InlineData("StatefulComputeIfPresent")]
    [InlineData("Merge")]
    [InlineData("Clear")]
    public void Reservation_ReentrantMutationThrowsWithoutChangingEntries(string operation)
    {
        var map = new ConcurrentHashMap<int, string>();
        map.Put(1, "retained");
        var error = Assert.Throws<InvalidOperationException>(() => map.ComputeIfAbsent(0, _ =>
        {
            int state = 0;
            switch (operation)
            {
                case "Put": map.Put(0, "inner"); break;
                case "PutIfAbsent": map.PutIfAbsent(0, "inner"); break;
                case "Remove": map.Remove(0); break;
                case "Replace": map.Replace(0, "inner"); break;
                case "ComputeIfAbsent": map.ComputeIfAbsent(0, _ => "inner"); break;
                case "Compute": map.Compute(0, (_, _) => "inner"); break;
                case "ComputeIfPresent": map.ComputeIfPresent(0, (_, _) => "inner"); break;
                case "StatefulComputeIfPresent":
                    map.ComputeIfPresent(0, static (string _, ref int state) => { state++; return "inner"; }, ref state);
                    break;
                case "Merge": map.Merge(0, "inner", (_, _) => "merged"); break;
                case "Clear": map.Clear(); break;
                default: throw new ArgumentException(operation);
            }
            return "outer";
        }));

        Assert.Equal("Recursive update", error.Message);
        Assert.Equal("retained", map.GetOrDefault(1));
        Assert.Null(map.GetOrDefault(0));
        Assert.Equal(1, map.Count);
        Assert.Equal(map.Count, map.AsEnumerable().LongCount());
    }

    [Fact]
    public void Transfer_ReentrantReservationThrowsBeforeMigratingIt()
    {
        var map = new ConcurrentHashMap<int, string>();
        for (int key = 0; key < 11; key++) map.Put(key, "retained");
        var error = Assert.Throws<InvalidOperationException>(() => map.Compute(15, (_, _) =>
        {
            map.Put(11, "trigger resize");
            return "outer";
        }));
        Assert.Equal("Recursive update", error.Message);
        Assert.Null(map.GetOrDefault(15));
        for (int key = 0; key < 11; key++) Assert.Equal("retained", map.GetOrDefault(key));
        Assert.Equal("trigger resize", map.GetOrDefault(11));
        Assert.Equal(12, map.Count);
        Assert.Equal(map.Count, map.AsEnumerable().LongCount());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CacheGet_RecursivePutThrowsAndPreservesAllCompletedWrites(bool bounded, bool append)
    {
        var builder = Cache.NewBuilder<int, string>().InitialCapacity(64).Executor(DirectExecutor.Instance);
        if (bounded) builder.MaximumSize(100);
        using var cache = builder.Build();
        cache.Put(1, "retained");
        if (append) cache.Put(128, "head");

        var error = Assert.Throws<InvalidOperationException>(() => cache.Get(0, _ =>
        {
            cache.Put(64, "inner");
            return append ? "outer" : null;
        }));

        Assert.Equal("Recursive update", error.Message);
        cache.CleanUp();
        Assert.Equal("retained", cache.GetIfPresent(1));
        Assert.Null(cache.GetIfPresent(0));
        Assert.Equal(append ? "inner" : null, cache.GetIfPresent(64));
        Assert.Equal(append ? 3 : 1, cache.EstimatedSize());
        Assert.Equal(EnumeratedCacheSize(cache), cache.EstimatedSize());
        cache.Put(0, "retry");
        Assert.Equal("retry", cache.GetIfPresent(0));
        Assert.Equal(EnumeratedCacheSize(cache), cache.EstimatedSize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reservation_ConcurrentWaitersCoalesceAndSeePublishedValue(bool compute)
    {
        var map = new ConcurrentHashMap<int, string>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        string Mapping(int _)
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait();
            return "value";
        }
        var owner = Task.Factory.StartNew(
            () => compute ? map.Compute(0, (key, _) => Mapping(key)) : map.ComputeIfAbsent(0, Mapping),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Null(map.GetOrDefault(0));
        Assert.Empty(map.AsEnumerable());
        var waiters = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(
            () => map.ComputeIfAbsent(0, _ => { Interlocked.Increment(ref calls); return "wrong"; }),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try
        {
            Assert.NotSame(waiters[0], await Task.WhenAny(waiters[0], Task.Delay(50)));
        }
        finally
        {
            release.Set();
        }
        Assert.Equal("value", await owner.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.All(await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(10)), value => Assert.Equal("value", value));
        Assert.Equal(1, calls);
        Assert.Equal(1, map.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Reservation_NullOrThrowingCallbackClearsSlot(bool compute, bool throws)
    {
        var map = new ConcurrentHashMap<int, string>();
        string? Mapping(int _) => throws ? throw new ApplicationException("mapping failed") : null;
        if (throws)
        {
            Assert.Throws<ApplicationException>(() =>
            {
                if (compute) map.Compute(0, (key, _) => Mapping(key));
                else map.ComputeIfAbsent(0, Mapping);
            });
        }
        else
        {
            Assert.Null(compute ? map.Compute(0, (key, _) => Mapping(key)) : map.ComputeIfAbsent(0, Mapping));
        }
        Assert.Equal(0, map.Count);
        Assert.Empty(map.AsEnumerable());
        Assert.Equal("retry", map.ComputeIfAbsent(0, _ => "retry"));
        Assert.Equal(1, map.Count);
    }

    private static long EnumeratedCacheSize(object cache)
    {
        object data = cache.GetType().GetField("_data", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache)!;
        var iterator = (IEnumerator)data.GetType().GetMethod("GetEnumerator")!.Invoke(data, null)!;
        try
        {
            long count = 0;
            while (iterator.MoveNext()) count++;
            return count;
        }
        finally
        {
            ((IDisposable)iterator).Dispose();
        }
    }
}
