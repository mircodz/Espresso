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

    // High-churn put/remove from a tiny table stresses the striped size counter under contention:
    // the exact live count must survive probe collisions, rehashing, and resizes.
    [Fact]
    public void ConcurrentChurn_CountMatchesLiveEntries()
    {
        var map = new ConcurrentHashMap<int, string>(2);
        const int threads = 16;
        const int perThread = 40_000;

        // Each thread owns a disjoint key range and ends with exactly the even keys still present.
        Parallel.For(0, threads, t =>
        {
            int baseKey = t * perThread;
            for (int i = 0; i < perThread; i++)
            {
                map.Put(baseKey + i, "v");
            }
            for (int i = 0; i < perThread; i += 2)
            {
                map.Remove(baseKey + i + 1);
            }
        });

        long expected = (long)threads * (perThread / 2);
        Assert.Equal(expected, map.Count);
        Assert.Equal(expected, map.AsEnumerable().LongCount());
    }

    // Deterministic proof that treeify/untreeify actually happen. Uses reflection on the internal
    // table to assert a bin becomes a TreeBin past the threshold and reverts to a plain Node list once
    // it shrinks below UNTREEIFY_THRESHOLD. Capacity 64 clears MIN_TREEIFY_CAPACITY so treeify (not a
    // resize) fires; the single-bucket comparer forces every key into one bin.
    [Fact]
    public void Treeify_And_Untreeify_SingleBucket_Deterministic()
    {
        var map = new ConcurrentHashMap<int, string>(64, 1, new CollidingComparer());

        // Insert 20 colliding keys — well past TREEIFY_THRESHOLD (8).
        for (int k = 0; k < 20; k++)
        {
            map.Put(k, "v" + k);
        }

        int bin = BinIndexOf(map, 0);
        Assert.Equal("TreeBin", HeadTypeName(map, bin));
        for (int k = 0; k < 20; k++)
        {
            Assert.Equal("v" + k, map.GetOrDefault(k));
        }
        Assert.Equal(20, map.Count);

        // Remove down to 5 nodes (< UNTREEIFY_THRESHOLD 6): the bin must revert to a plain Node list.
        for (int k = 0; k < 15; k++)
        {
            Assert.Equal("v" + k, map.Remove(k));
        }

        string head = HeadTypeName(map, bin);
        Assert.Equal("Node", head); // reverted, not a TreeBin
        Assert.Equal(5, map.Count);
        for (int k = 15; k < 20; k++)
        {
            Assert.Equal("v" + k, map.GetOrDefault(k));
        }
        for (int k = 0; k < 15; k++)
        {
            Assert.Null(map.GetOrDefault(k));
        }
    }

    private static Array GetTable<TKey, TValue>(ConcurrentHashMap<TKey, TValue> map)
        where TKey : notnull where TValue : class
    {
        var f = typeof(ConcurrentHashMap<TKey, TValue>).GetField(
            "_table", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Array)f.GetValue(map)!;
    }

    private static int BinIndexOf<TKey, TValue>(ConcurrentHashMap<TKey, TValue> map, TKey key)
        where TKey : notnull where TValue : class
    {
        // Spread(hash) & (len-1); CollidingComparer hashes everything to 0 so the bin is index 0.
        _ = key;
        return 0;
    }

    private static string HeadTypeName<TKey, TValue>(ConcurrentHashMap<TKey, TValue> map, int bin)
        where TKey : notnull where TValue : class
    {
        Array tab = GetTable(map);
        object? head = tab.GetValue(bin);
        return head?.GetType().Name ?? "null";
    }

    // Forces every key into a single bin so collision-chain handling is exercised.
    private sealed class CollidingComparer : IEqualityComparer<int>
    {
        public bool Equals(int a, int b) => a == b;
        public int GetHashCode(int _) => 0;
    }

    // Buckets keys into a few hash slots so bins grow long enough to treeify (and shrink to untreeify),
    // while still spanning multiple bins so a resize must split tree bins into lo/hi.
    private sealed class FewBucketComparer : IEqualityComparer<int>
    {
        public bool Equals(int a, int b) => a == b;
        public int GetHashCode(int k) => k & 3; // 4 hash buckets
    }

    // Treeification gate: adversarial hashing drives bins well past the treeify threshold, under
    // concurrent put/get/remove and repeated resizes (so tree bins must split). Every key inserted and
    // not removed must be findable, the count exact, and enumeration duplicate-free. This test passes
    // trivially on a list-only map and becomes a real red-black-tree stress once TreeBin lands.
    [Fact]
    public void Treeification_DeepBins_ConcurrentMutationAndResize()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var map = new ConcurrentHashMap<int, string>(4, 1, new FewBucketComparer());
            const int threads = 8;
            const int perThread = 3_000;
            string? failure = null;

            Parallel.For(0, threads, t =>
            {
                int baseKey = t * perThread;
                for (int i = 0; i < perThread; i++)
                {
                    int key = baseKey + i;
                    map.Put(key, "v");
                    if (map.GetOrDefault(key) is null)
                    {
                        Interlocked.CompareExchange(ref failure, $"attempt {attempt}: key {key} missing after put", null);
                    }
                    if ((i & 1) == 0)
                    {
                        map.Remove(key); // even keys removed -> bins grow then shrink (treeify/untreeify)
                    }
                }
            });

            Assert.Null(failure);
            long expected = (long)threads * (perThread / 2); // odd keys survive
            Assert.Equal(expected, map.Count);
            Assert.Equal(expected, map.AsEnumerable().LongCount());
            // Every surviving (odd) key must be present with its value.
            for (int t = 0; t < threads; t++)
            {
                Assert.Equal("v", map.GetOrDefault(t * perThread + 1));
            }
        }
    }

    // Put-then-remove from a tiny table forces many resizes concurrent with removals. Repeated many
    // times because a lost-entry resize race is probabilistic per trial. A previous cooperative-resize
    // attempt failed here (~1 trial in 8): Remove returned null for a key the same thread had inserted,
    // and Count inflated. This is the permanent gate any resize rewrite must pass.
    [Fact]
    public void ConcurrentRemoveDuringResize_CountExact()
    {
        const int threads = 8;
        const int perThread = 8_000;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var map = new ConcurrentHashMap<int, string>(2);
            string? failure = null;

            Parallel.For(0, threads, t =>
            {
                int baseKey = t * perThread;
                for (int i = 0; i < perThread; i++)
                {
                    map.Put(baseKey + i, "v");
                }
                for (int i = 1; i < perThread; i += 2)
                {
                    if (map.Remove(baseKey + i) is null)
                    {
                        Interlocked.CompareExchange(ref failure, $"attempt {attempt}: Remove({baseKey + i}) returned null", null);
                    }
                }
            });

            Assert.Null(failure);
            long expected = (long)threads * ((perThread + 1) / 2);
            Assert.Equal(expected, map.Count);
            Assert.Equal(expected, map.AsEnumerable().LongCount());
            for (int t = 0; t < threads; t++)
            {
                Assert.Equal("v", map.GetOrDefault(t * perThread));       // even -> present
                Assert.Null(map.GetOrDefault(t * perThread + 1));         // odd  -> removed
            }
        }
    }

    // All keys collide into one bin; concurrent put/remove/compute walk and splice the same chain.
    [Fact]
    public void DeepCollisionChain_ConcurrentMutation()
    {
        var map = new ConcurrentHashMap<int, string>(16, 1, new CollidingComparer());
        const int threads = 8;
        const int perThread = 4_000;

        Parallel.For(0, threads, t =>
        {
            int baseKey = t * perThread;
            for (int i = 0; i < perThread; i++)
            {
                int key = baseKey + i;
                map.Put(key, "v");
                map.ComputeIfAbsent(key, _ => "w");
                if ((i & 1) == 0)
                {
                    map.Remove(key);
                }
            }
        });

        long expected = (long)threads * (perThread / 2);
        Assert.Equal(expected, map.Count);
        Assert.Equal(expected, map.AsEnumerable().LongCount());
    }

    // Colliding keys route every ComputeIfAbsent through the same bin, exercising the locked
    // null-placeholder handshake (which the distinct-key atomicity test never hits).
    [Fact]
    public void ComputeIfAbsent_SingleInvocation_UnderCollision()
    {
        var map = new ConcurrentHashMap<int, string>(16, 1, new CollidingComparer());
        int factoryCalls = 0;
        const int threads = 16;
        const int keys = 500;
        using var start = new Barrier(threads);

        Parallel.For(0, threads, _ =>
        {
            start.SignalAndWait();
            for (int k = 0; k < keys; k++)
            {
                map.ComputeIfAbsent(k, key =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    return "v" + key;
                });
            }
        });

        Assert.Equal(keys, factoryCalls);
        Assert.Equal(keys, map.Count);
    }

    // Many threads race Replace(key, old, new) on one key; exactly one wins each generation, so the
    // final value is well-defined and no update is lost.
    [Fact]
    public void ConditionalReplace_ExactlyOneWinnerPerGeneration()
    {
        var map = new ConcurrentHashMap<int, string>(16);
        map.Put(0, "gen0");
        const int gens = 2_000;

        for (int g = 0; g < gens; g++)
        {
            string from = "gen" + g;
            string to = "gen" + (g + 1);
            int winners = 0;
            Parallel.For(0, 8, _ =>
            {
                if (map.Replace(0, from, to))
                {
                    Interlocked.Increment(ref winners);
                }
            });
            Assert.Equal(1, winners);
            Assert.Equal(to, map.GetOrDefault(0));
        }
    }

    // Remove(key, value) and Replace(key, old, new) compare by equality, not reference identity.
    [Fact]
    public void ConditionalOps_UseValueEquality_NotIdentity()
    {
        var map = new ConcurrentHashMap<int, string>(16);
        map.Put(1, new string("abc".ToCharArray())); // distinct instance, equal value

        Assert.True(map.Replace(1, "abc", "xyz"));    // matched by equality
        Assert.Equal("xyz", map.GetOrDefault(1));
        Assert.True(map.Remove(1, new string("xyz".ToCharArray())));
        Assert.Null(map.GetOrDefault(1));
    }

    // Clear() interleaved with concurrent writers and resizes must leave a consistent, non-negative
    // count that agrees with enumeration.
    [Fact]
    public void Clear_ConcurrentWithPutsAndResize()
    {
        var map = new ConcurrentHashMap<int, string>(2);
        const int writers = 8;
        const int perThread = 20_000;
        using var start = new Barrier(writers + 1);

        var writerTasks = Enumerable.Range(0, writers).Select(t => Task.Run(() =>
        {
            start.SignalAndWait();
            int baseKey = t * perThread;
            for (int i = 0; i < perThread; i++)
            {
                map.Put(baseKey + i, "v");
            }
        })).ToArray();

        var clearer = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 50; i++)
            {
                map.Clear();
            }
        });

        Task.WaitAll(writerTasks.Append(clearer).ToArray());

        // Whatever remains, the counter must be non-negative and match a fresh enumeration.
        long count = map.Count;
        Assert.True(count >= 0);
        Assert.Equal(map.AsEnumerable().LongCount(), count);
    }

    // .NET monitors are reentrant, so a mapping function that recurses into ComputeIfAbsent for the
    // same key does not deadlock. Document the resulting behavior so a future change can't regress it
    // silently: the inner call observes the bin mid-computation.
    [Fact]
    public void ComputeIfAbsent_ReentrantSameKey_DoesNotDeadlock()
    {
        var map = new ConcurrentHashMap<int, string>(16);
        string? result = map.ComputeIfAbsent(1, k =>
        {
            map.ComputeIfAbsent(1, _ => "inner");
            return "outer";
        });
        Assert.NotNull(result);
        Assert.NotNull(map.GetOrDefault(1));
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
