using System;
using System.Collections;
using System.Collections.Generic;
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
        var map = new ConcurrentHashMap<int, string>(32); // 64 bins: keys 0, 64 and 128 share bin 0
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

    // PR #4 detects a callback that inserts its own key into a list bin; a tree bin must reject it the
    // same way instead of keeping the inner value, returning the outer one and counting the key twice.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RecursiveSameKeyInsert_IsRejectedInListAndTreeBins(bool treeBin, bool compute)
    {
        var map = new ConcurrentHashMap<int, string>(64, 1, new ZeroHash());
        int entries = treeBin ? 20 : 3;
        for (int key = 0; key < entries; key++) map.Put(key, "v" + key);
        Assert.Equal(treeBin ? "TreeBin" : "Node", HeadTypeName(map, 0));
        string Mapping(int key)
        {
            map.Put(key, "inner");
            return "outer";
        }

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (compute) map.Compute(100, (key, _) => Mapping(key));
            else map.ComputeIfAbsent(100, Mapping);
        });
        Assert.Equal("Recursive update", error.Message);
        Assert.Equal("inner", map.GetOrDefault(100));
        Assert.Equal(entries + 1, map.Count);
        Assert.Equal(map.Count, map.AsEnumerable().LongCount());
    }

    // A callback that writes another key while the map is at its resize threshold used to resize from
    // inside the callback: the transfer met the reservation this thread holds and threw after joining
    // the resize, leaving it registered forever, so the table never grew again. The resize must instead
    // wait for the outer operation, which completes and then grows the table normally.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservedBinCallback_WriteAtResizeThreshold_DefersResize(bool compute)
    {
        var map = new ConcurrentHashMap<int, string>();
        for (int key = 0; key < 11; key++) map.Put(key, "retained");
        string Mapping(int _)
        {
            map.Put(11, "trigger resize"); // the 12th entry reaches the 16-bin table's threshold
            Assert.Equal(16, TableLength(map));
            return "outer";
        }

        Assert.Equal("outer", compute ? map.Compute(15, (key, _) => Mapping(key)) : map.ComputeIfAbsent(15, Mapping));
        Assert.Equal(32, TableLength(map));
        AssertNoResizeInProgress(map);
        for (int key = 0; key < 11; key++) Assert.Equal("retained", map.GetOrDefault(key));
        Assert.Equal("trigger resize", map.GetOrDefault(11));
        Assert.Equal("outer", map.GetOrDefault(15));
        Assert.Equal(13, map.Count);
        Assert.Equal(map.Count, map.AsEnumerable().LongCount());

        for (int key = 100; key < 20_100; key++) map.Put(key, "v");
        Assert.Equal(32_768, TableLength(map));
        AssertNoResizeInProgress(map);
    }

    // The same with a list bin: a resize from inside the callback migrated the bin the outer
    // operation then appended to, so the new entry landed behind a node that now lives in the other
    // half of the new table, counted and enumerated but unreachable.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListBinCallback_WriteAtResizeThreshold_OuterInsertStaysReachable(bool compute)
    {
        var map = new ConcurrentHashMap<int, string>();
        for (int key = 0; key < 11; key++) map.Put(key, "retained");
        string Mapping(int _)
        {
            map.Put(11, "trigger resize");
            return "outer";
        }

        // Key 16 shares bin 0 with key 0 in the 16-bin table but belongs to bin 16 of the 32-bin one.
        Assert.Equal("outer", compute ? map.Compute(16, (key, _) => Mapping(key)) : map.ComputeIfAbsent(16, Mapping));
        Assert.Equal(32, TableLength(map));
        AssertNoResizeInProgress(map);
        Assert.Equal("outer", map.GetOrDefault(16));
        Assert.Equal("retained", map.GetOrDefault(0));
        Assert.Equal(13, map.Count);
        Assert.Equal(map.Count, map.AsEnumerable().LongCount());
        Assert.Equal("outer", map.Put(16, "again"));
        Assert.Equal(13, map.Count);
    }

    // The guard is per map and nests: a callback of map A that runs a callback of map B, which writes
    // to A, must still defer A's resize, while B stays free to resize from A's callback.
    [Fact]
    public void NestedCallbacks_DeferResizeOfEveryMapWhoseCallbackIsRunning()
    {
        var outer = new ConcurrentHashMap<int, string>();
        var inner = new ConcurrentHashMap<int, string>();
        for (int key = 0; key < 11; key++)
        {
            outer.Put(key, "retained");
            inner.Put(key, "retained");
        }

        Assert.Equal("outer", outer.ComputeIfAbsent(15, _ =>
        {
            inner.Put(11, "grows inner");
            Assert.Equal(32, TableLength(inner)); // not inside inner's callback: resizes at once
            Assert.Equal("inner", inner.Compute(15, (_, _) =>
            {
                outer.Put(11, "trigger resize");
                return "inner";
            }));
            Assert.Equal(16, TableLength(outer));
            return "outer";
        }));

        Assert.Equal(32, TableLength(outer));
        AssertNoResizeInProgress(outer);
        AssertNoResizeInProgress(inner);
        Assert.Equal("trigger resize", outer.GetOrDefault(11));
        Assert.Equal("outer", outer.GetOrDefault(15));
        Assert.Equal(13, outer.Count);
        Assert.Equal(13, inner.Count);
    }

    // Loaders that populate other keys of the same cache while other threads write and resize the map.
    // A loader whose write lands in the bin it holds is rejected; everything else must stay consistent.
    [Fact]
    public void ConcurrentCallbacksWritingOtherKeys_KeepTheMapConsistent()
    {
        for (int round = 0; round < 10; round++)
        {
            var map = new ConcurrentHashMap<int, string>();
            int threads = Math.Max(4, Environment.ProcessorCount);
            const int perThread = 2_000;
            var loaded = new bool[threads * perThread];
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    int key = (t * perThread + i) * 2; // even keys are loaded, odd keys are written by loaders
                    try
                    {
                        string? value = (i & 1) == 0
                            ? map.ComputeIfAbsent(key, k => { map.Put(k + 1, "side"); return "loaded"; })
                            : map.Compute(key, (k, _) => { map.Put(k + 1, "side"); return "loaded"; });
                        Assert.Equal("loaded", value);
                        loaded[key / 2] = true;
                    }
                    catch (InvalidOperationException e) when (e.Message == "Recursive update")
                    {
                        // The loader's write hit the bin its own computation holds.
                    }
                }
            });

            AssertNoResizeInProgress(map);
            long entries = map.AsEnumerable().LongCount();
            Assert.Equal(entries, map.Count);
            for (int i = 0; i < loaded.Length; i++)
            {
                Assert.Equal(loaded[i] ? "loaded" : null, map.GetOrDefault(i * 2));
                Assert.Equal("side", map.GetOrDefault(i * 2 + 1));
            }
            Assert.True(TableLength(map) >= entries / 2, $"table {TableLength(map)} for {entries} entries");
        }
    }

    // A loader that populates a neighbouring key while the cache's map is at its resize threshold must
    // not leave the map unable to grow (it used to throw "Recursive update" and strand the resize).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CacheGet_LoaderWritingAnotherKeyAtResizeThreshold_KeepsMapGrowing(bool bounded)
    {
        // InitialCapacity(8) gives a 16-bin table that resizes at 12 entries.
        var builder = Cache.NewBuilder<int, string>().InitialCapacity(8).Executor(DirectExecutor.Instance);
        if (bounded) builder.MaximumSize(1_000_000);
        using var cache = builder.Build();
        object data = cache.GetType().GetField("_data", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache)!;
        Assert.Equal(16, DataTableLength(data));
        for (int key = 0; key < 11; key++) cache.Put(key, "v");

        Assert.Equal("outer", cache.Get(15, _ =>
        {
            cache.Put(11, "inner");
            return "outer";
        }));

        cache.CleanUp();
        Assert.Equal("inner", cache.GetIfPresent(11));
        Assert.Equal("outer", cache.GetIfPresent(15));
        Assert.Equal(13, cache.EstimatedSize());
        Assert.Equal(32, DataTableLength(data));
        for (int key = 100; key < 20_100; key++) cache.Put(key, "v");
        Assert.Equal(32_768, DataTableLength(data));
    }

    private static int DataTableLength(object data)
        => ((Array)data.GetType().GetField("_table", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(data)!).Length;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CacheGet_RecursivePutThrowsAndPreservesAllCompletedWrites(bool bounded, bool append)
    {
        // 64 bins: keys 0, 64 and 128 share bin 0.
        var builder = Cache.NewBuilder<int, string>().InitialCapacity(32).Executor(DirectExecutor.Instance);
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

    private sealed class ZeroHash : IEqualityComparer<int>
    {
        public bool Equals(int a, int b) => a == b;
        public int GetHashCode(int _) => 0;
    }

    private static int TableLength<TKey, TValue>(ConcurrentHashMap<TKey, TValue> map)
        where TKey : notnull where TValue : class
        => ((Array)Field(map, "_table")!).Length;

    private static void AssertNoResizeInProgress<TKey, TValue>(ConcurrentHashMap<TKey, TValue> map)
        where TKey : notnull where TValue : class
    {
        Assert.Null(Field(map, "_nextTable"));
        Assert.True((int)Field(map, "_sizeCtl")! > 0, $"sizeCtl {Field(map, "_sizeCtl")}");
    }

    private static string HeadTypeName<TKey, TValue>(ConcurrentHashMap<TKey, TValue> map, int bin)
        where TKey : notnull where TValue : class
        => ((Array)Field(map, "_table")!).GetValue(bin)?.GetType().Name ?? "null";

    private static object? Field<TKey, TValue>(ConcurrentHashMap<TKey, TValue> map, string name)
        where TKey : notnull where TValue : class
        => typeof(ConcurrentHashMap<TKey, TValue>).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(map);

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
