using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class ConcurrentHashMapTest
{
    private static ConcurrentHashMap<int, string> NewMap(int cap = 16) => new(cap);

    // concurrent inserts of DISTINCT keys starting from a tiny table force many resizes.
    // A writer following a ForwardingNode must not lose its write into the half-built new table.
    // After joining, every key must be present and Count must equal the exact insert total.
    [Fact]
    public void ConcurrentInsertsDuringManyResizes_NoLostEntries()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var map = new ConcurrentHashMap<int, string>(2); // start tiny -> many resizes
            const int threads = 8;
            const int perThread = 20_000;

            Parallel.For(0, threads, t =>
            {
                int baseKey = t * perThread;
                for (int i = 0; i < perThread; i++)
                {
                    map.Put(baseKey + i, "v");
                }
            });

            int total = threads * perThread;
            Assert.Equal(total, map.Count);
            // Independent recount via enumeration must agree (no dropped/duplicated entries).
            long enumerated = map.AsEnumerable().LongCount();
            Assert.Equal(total, enumerated);
            // Spot-check that specific keys across the range survived.
            for (int t = 0; t < threads; t++)
            {
                Assert.Equal("v", map.GetOrDefault(t * perThread));               // first
                Assert.Equal("v", map.GetOrDefault(t * perThread + perThread - 1)); // last
            }
        }
    }

    [Fact]
    public void Put_Get_Remove()
    {
        var map = NewMap();
        Assert.Null(map.Put(1, "a"));
        Assert.Equal("a", map.GetOrDefault(1));
        Assert.Equal("a", map.Put(1, "b"));
        Assert.Equal("b", map.GetOrDefault(1));
        Assert.Equal(1, map.Count);
        Assert.Equal("b", map.Remove(1));
        Assert.Null(map.GetOrDefault(1));
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void PutIfAbsent()
    {
        var map = NewMap();
        Assert.Null(map.PutIfAbsent(1, "a"));
        Assert.Equal("a", map.PutIfAbsent(1, "b"));
        Assert.Equal("a", map.GetOrDefault(1));
    }

    [Fact]
    public void ConditionalRemoveAndReplace()
    {
        var map = NewMap();
        map.Put(1, "a");
        Assert.False(map.Remove(1, "x"));
        Assert.True(map.Remove(1, "a"));
        Assert.Equal(0, map.Count);

        map.Put(2, "a");
        Assert.False(map.Replace(2, "x", "y"));
        Assert.True(map.Replace(2, "a", "y"));
        Assert.Equal("y", map.GetOrDefault(2));
        Assert.Equal("y", map.Replace(2, "z"));
        Assert.Null(map.Replace(3, "z"));
    }

    [Fact]
    public void ComputeIfAbsent_RunsAtMostOnce_AndNotWhenPresent()
    {
        var map = NewMap();
        int calls = 0;
        string? v = map.ComputeIfAbsent(1, _ => { calls++; return "a"; });
        Assert.Equal("a", v);
        Assert.Equal(1, calls);

        v = map.ComputeIfAbsent(1, _ => { calls++; return "b"; });
        Assert.Equal("a", v);
        Assert.Equal(1, calls); // not called for present key
    }

    [Fact]
    public void ComputeIfAbsent_NullResult_DoesNotInsert()
    {
        var map = NewMap();
        Assert.Null(map.ComputeIfAbsent(1, _ => null));
        Assert.Equal(0, map.Count);
        Assert.False(map.ContainsKey(1));
    }

    [Fact]
    public void ComputeIfPresent()
    {
        var map = NewMap();
        Assert.Null(map.ComputeIfPresent(1, (_, _) => "a")); // absent -> no-op
        map.Put(1, "a");
        Assert.Equal("ab", map.ComputeIfPresent(1, (_, v) => v + "b"));
        Assert.Null(map.ComputeIfPresent(1, (_, _) => null)); // remove
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void Compute_InsertReplaceRemove()
    {
        var map = NewMap();
        Assert.Equal("a", map.Compute(1, (_, v) => v == null ? "a" : v + "x"));
        Assert.Equal("ax", map.Compute(1, (_, v) => v + "x"));
        Assert.Null(map.Compute(1, (_, _) => null));
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void Merge()
    {
        var map = NewMap();
        Assert.Equal("a", map.Merge(1, "a", (o, n) => o + n));
        Assert.Equal("ab", map.Merge(1, "b", (o, n) => o + n));
    }

    [Fact]
    public void Resize_PreservesAllEntries()
    {
        var map = new ConcurrentHashMap<int, string>(16);
        const int count = 10_000;
        for (int i = 0; i < count; i++)
        {
            map.Put(i, "v" + i);
        }
        Assert.Equal(count, map.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal("v" + i, map.GetOrDefault(i));
        }
    }

    [Fact]
    public void Enumerate_ReturnsAllLiveEntries()
    {
        var map = NewMap();
        for (int i = 0; i < 100; i++)
        {
            map.Put(i, "v" + i);
        }
        var seen = map.AsEnumerable().ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(100, seen.Count);
        Assert.Equal("v50", seen[50]);
    }

    // Regression: enumerating across a concurrent resize must not yield an entry twice. The traverser
    // captures the table at iteration start; when a bin has been migrated (ForwardingNode) it must
    // descend into the new table rather than restart from bin 0 (which re-yielded visited entries).
    [Fact]
    public void Enumerate_AcrossConcurrentResize_NoDuplicateKeys()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            var map = new ConcurrentHashMap<int, string>(2); // tiny -> resizes early and often
            for (int i = 0; i < 64; i++)
            {
                map.Put(i, "v" + i);
            }

            using var start = new Barrier(2);
            // Background: keep inserting distinct keys to drive repeated resizes while we iterate.
            var grower = Task.Run(() =>
            {
                start.SignalAndWait();
                for (int i = 64; i < 4_000; i++)
                {
                    map.Put(i, "v" + i);
                }
            });

            start.SignalAndWait();
            var counts = new Dictionary<int, int>();
            foreach (var kv in map.AsEnumerable())
            {
                counts.TryGetValue(kv.Key, out int c);
                counts[kv.Key] = c + 1;
            }
            grower.Wait();

            foreach (var pair in counts)
            {
                Assert.True(pair.Value == 1,
                    $"attempt {attempt}: key {pair.Key} yielded {pair.Value} times during resize");
            }
        }
    }

    // Deterministic companion: force a resize to complete *before* iteration continues, so the
    // captured old table is entirely ForwardingNodes. Every live entry must still appear exactly once.
    [Fact]
    public void Enumerate_WhenTableFullyForwarded_VisitsEachEntryOnce()
    {
        var map = new ConcurrentHashMap<int, string>(4);
        for (int i = 0; i < 8; i++)
        {
            map.Put(i, "v" + i);
        }

        var e = map.AsEnumerable().GetEnumerator();
        Assert.True(e.MoveNext()); // capture the small table, mid-iteration

        // Grow far past the load factor so the captured table gets fully migrated (ForwardingNodes).
        for (int i = 8; i < 2_000; i++)
        {
            map.Put(i, "v" + i);
        }

        var seen = new HashSet<int> { e.Current.Key };
        while (e.MoveNext())
        {
            Assert.True(seen.Add(e.Current.Key),
                $"key {e.Current.Key} was yielded twice after a full table migration");
        }
    }

    [Fact]
    public void ComputeIfAbsent_IsAtomic_UnderContention()
    {
        var map = new ConcurrentHashMap<int, string>(16);
        int factoryCalls = 0;
        const int threads = 16;
        using var start = new Barrier(threads);

        Parallel.For(0, threads, _ =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 1000; i++)
            {
                map.ComputeIfAbsent(i, k =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    return "v" + k;
                });
            }
        });

        // Exactly one factory call per distinct key — proves compute-under-lock atomicity.
        Assert.Equal(1000, factoryCalls);
        Assert.Equal(1000, map.Count);
    }

    [Fact]
    public void ConcurrentPutGetRemove_Stress()
    {
        var map = new ConcurrentHashMap<int, string>(16);
        const int threads = 8;
        const int perThread = 20_000;
        Parallel.For(0, threads, t =>
        {
            var rng = new Random(t);
            for (int i = 0; i < perThread; i++)
            {
                int key = rng.Next(2000);
                switch (rng.Next(3))
                {
                    case 0: map.Put(key, "v" + key); break;
                    case 1: map.GetOrDefault(key); break;
                    default: map.Remove(key); break;
                }
            }
        });

        // Count must match an independent recount of live entries.
        long enumerated = map.AsEnumerable().LongCount();
        Assert.Equal(enumerated, map.Count);
    }

    [Fact]
    public void ConcurrentInsertDistinctKeys_CountExact()
    {
        var map = new ConcurrentHashMap<int, string>(16);
        const int threads = 8;
        const int perThread = 10_000;
        Parallel.For(0, threads, t =>
        {
            int baseKey = t * perThread;
            for (int i = 0; i < perThread; i++)
            {
                map.Put(baseKey + i, "v");
            }
        });
        Assert.Equal(threads * perThread, map.Count);
    }
}

internal static class ChmEnumerableExtensions
{
    public static IEnumerable<KeyValuePair<TKey, TValue>> AsEnumerable<TKey, TValue>(
        this ConcurrentHashMap<TKey, TValue> map)
        where TKey : notnull
        where TValue : class
    {
        var e = map.GetEnumerator();
        while (e.MoveNext())
        {
            yield return e.Current;
        }
    }
}
