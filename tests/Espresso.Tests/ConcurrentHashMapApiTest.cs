using System.Collections.Generic;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Targeted coverage for ConcurrentHashMap API branches that the cache exercises only rarely: the
/// dictionary-style surface (TryGetValue, Merge) and the tree-bin code paths of the compute/replace
/// operations (reached by forcing every key into one hash bucket so it treeifies).
/// </summary>
public sealed class ConcurrentHashMapApiTest
{
    /// <summary>Hashes every key to the same bucket, so a bin grows and treeifies.</summary>
    private sealed class CollidingComparer : IEqualityComparer<int>
    {
        public bool Equals(int a, int b) => a == b;
        public int GetHashCode(int _) => 0;
    }

    private static ConcurrentHashMap<int, string> Treeified()
    {
        // Small table + all-colliding hashes => one bin of >= TreeifyThreshold nodes => a TreeBin.
        var map = new ConcurrentHashMap<int, string>(64, 1, new CollidingComparer());
        for (int i = 0; i < 20; i++)
        {
            map.Put(i, "v" + i);
        }
        return map;
    }

    // ----- TryGetValue -----

    [Fact]
    public void TryGetValue_Present_ReturnsTrueAndValue()
    {
        var map = new ConcurrentHashMap<int, string>(16);
        map.Put(1, "a");

        bool found = map.TryGetValue(1, out string value);

        Assert.True(found);
        Assert.Equal("a", value);
    }

    [Fact]
    public void TryGetValue_Absent_ReturnsFalse()
    {
        var map = new ConcurrentHashMap<int, string>(16);

        bool found = map.TryGetValue(99, out string value);

        Assert.False(found);
        Assert.Null(value);
    }

    // ----- Merge -----

    [Fact]
    public void Merge_AbsentKey_InsertsValue()
    {
        var map = new ConcurrentHashMap<int, string>(16);

        string? result = map.Merge(1, "a", (_, _) => "unused");

        Assert.Equal("a", result);
        Assert.Equal("a", map.GetOrDefault(1));
    }

    [Fact]
    public void Merge_PresentKey_CombinesValues()
    {
        var map = new ConcurrentHashMap<int, string>(16);
        map.Put(1, "a");

        string? result = map.Merge(1, "b", (old, add) => old + add);

        Assert.Equal("ab", result);
        Assert.Equal("ab", map.GetOrDefault(1));
    }

    [Fact]
    public void Merge_RemappingToNull_RemovesEntry()
    {
        var map = new ConcurrentHashMap<int, string>(16);
        map.Put(1, "a");

        string? result = map.Merge(1, "b", (_, _) => null);

        Assert.Null(result);
        Assert.Null(map.GetOrDefault(1));
    }

    // ----- Tree-bin variants of the compute/replace surface -----

    [Fact]
    public void TreeBin_GetAndContains_Work()
    {
        var map = Treeified();

        Assert.Equal("v7", map.GetOrDefault(7));
        Assert.True(map.ContainsKey(19));
        Assert.Null(map.GetOrDefault(100));
    }

    [Fact]
    public void TreeBin_Compute_ReplacesExistingValue()
    {
        var map = Treeified();

        string? result = map.Compute(7, (_, old) => old + "!");

        Assert.Equal("v7!", result);
        Assert.Equal("v7!", map.GetOrDefault(7));
    }

    [Fact]
    public void TreeBin_Compute_RemovesWhenNull()
    {
        var map = Treeified();

        string? result = map.Compute(7, (_, _) => null);

        Assert.Null(result);
        Assert.Null(map.GetOrDefault(7));
        // Other tree entries remain intact.
        Assert.Equal("v8", map.GetOrDefault(8));
    }

    [Fact]
    public void TreeBin_ComputeIfAbsent_InsertsIntoTree()
    {
        var map = Treeified();

        string? result = map.ComputeIfAbsent(999, k => "new" + k);

        Assert.Equal("new999", result);
        Assert.Equal("new999", map.GetOrDefault(999));
    }

    [Fact]
    public void TreeBin_ComputeIfAbsent_PresentKey_DoesNotRecompute()
    {
        var map = Treeified();
        int calls = 0;

        string? result = map.ComputeIfAbsent(7, _ => { calls++; return "other"; });

        Assert.Equal("v7", result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void TreeBin_ComputeIfPresent_UpdatesExisting()
    {
        var map = Treeified();

        string? result = map.ComputeIfPresent(7, (_, old) => old + "*");

        Assert.Equal("v7*", result);
        Assert.Equal("v7*", map.GetOrDefault(7));
    }

    [Fact]
    public void TreeBin_ComputeIfPresent_AbsentKey_NoOp()
    {
        var map = Treeified();

        string? result = map.ComputeIfPresent(999, (_, old) => old + "*");

        Assert.Null(result);
        Assert.Null(map.GetOrDefault(999));
    }

    [Fact]
    public void TreeBin_Remove_UnlinksFromTree()
    {
        var map = Treeified();

        string? removed = map.Remove(7);

        Assert.Equal("v7", removed);
        Assert.Null(map.GetOrDefault(7));
        Assert.Equal("v6", map.GetOrDefault(6));
    }

    [Fact]
    public void TreeBin_ConditionalReplace_OnlyWhenValueMatches()
    {
        var map = Treeified();

        Assert.False(map.Remove(7, "wrong"));
        Assert.Equal("v7", map.GetOrDefault(7));

        Assert.True(map.Remove(7, "v7"));
        Assert.Null(map.GetOrDefault(7));
    }
}
