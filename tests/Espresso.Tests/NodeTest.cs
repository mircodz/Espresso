using System;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class NodeTest
{
    private sealed class Key
    {
        public readonly int Id;
        public Key(int id) => Id = id;
    }

    private sealed class Val
    {
        public readonly string S;
        public Val(string s) => S = s;
    }

    private static Node<Key, Val> Make(NodeFeature features, int weight = 1, long now = 100)
        => NodeFactory.Create(features, new Key(1), new Val("v"), weight, now);

    [Fact]
    public void Factory_ReturnsSmallestType_ForUnbounded()
    {
        var node = Make(NodeFeature.None);
        Assert.Equal("PS`2", TypeNameWithoutNamespace(node));
    }

    [Fact]
    public void Factory_MapsFeaturesToVariantNames()
    {
        Assert.Equal("PSMS`2", TypeNameWithoutNamespace(Make(NodeFeature.MaximumSize)));
        Assert.Equal("PSMW`2", TypeNameWithoutNamespace(Make(NodeFeature.MaximumWeight)));
        Assert.Equal("PSAMS`2", TypeNameWithoutNamespace(Make(NodeFeature.ExpireAccess | NodeFeature.MaximumSize)));
        Assert.Equal("PSWR`2", TypeNameWithoutNamespace(Make(NodeFeature.ExpireWrite | NodeFeature.RefreshWrite)));
        Assert.Equal("PSAWRMW`2", TypeNameWithoutNamespace(Make(
            NodeFeature.ExpireAccess | NodeFeature.ExpireWrite | NodeFeature.RefreshWrite | NodeFeature.MaximumWeight)));
    }

    [Fact]
    public void KeyAndValue_RoundTrip()
    {
        var node = Make(NodeFeature.None);
        Assert.NotNull(node.Key);
        Assert.Equal(1, node.Key!.Id);
        Assert.Equal("v", node.Value!.S);
        Assert.True(node.IsAlive);
        Assert.False(node.IsRetired);
        Assert.False(node.IsDead);
    }

    [Fact]
    public void HealthState_Transitions()
    {
        var node = Make(NodeFeature.None);
        Assert.True(node.IsAlive);

        node.Retire();
        Assert.True(node.IsRetired);
        Assert.False(node.IsAlive);
        Assert.Null(node.Key); // key reference now a sentinel

        node.Die();
        Assert.True(node.IsDead);
        Assert.False(node.IsAlive);
        Assert.Null(node.Value); // value cleared on death
    }

    [Fact]
    public void DefaultAccessors_ReturnDefaults_ForUnbounded()
    {
        var node = Make(NodeFeature.None);
        Assert.Equal(1, node.Weight);
        Assert.Equal(1, node.PolicyWeight);
        Assert.Equal(0L, node.AccessTime);
        Assert.Equal(0L, node.WriteTime);
        Assert.Equal(Node<Key, Val>.Window, node.QueueType);
        Assert.Null(node.GetPreviousInAccessOrder());
        Assert.Null(node.GetNextInAccessOrder());
    }

    [Fact]
    public void Weighted_StoresWeightAndPolicyWeight()
    {
        var node = Make(NodeFeature.MaximumWeight, weight: 42);
        Assert.Equal(42, node.Weight);
        Assert.Equal(42, node.PolicyWeight);
        node.Weight = 7;
        node.PolicyWeight = 9;
        Assert.Equal(7, node.Weight);
        Assert.Equal(9, node.PolicyWeight);
    }

    [Fact]
    public void MaximumSize_IsUnweighted_ButHasQueueType()
    {
        var node = Make(NodeFeature.MaximumSize, weight: 42);
        Assert.Equal(1, node.Weight); // unweighted variant ignores weight
        node.MakeMainProbation();
        Assert.True(node.InMainProbation);
        node.MakeMainProtected();
        Assert.True(node.InMainProtected);
    }

    [Fact]
    public void ExpireAccess_SeedsAndUpdatesAccessTime()
    {
        var node = Make(NodeFeature.ExpireAccess, now: 12345);
        Assert.Equal(12345L, node.AccessTime);
        node.AccessTime = 999;
        Assert.Equal(999L, node.AccessTime);
    }

    [Fact]
    public void WriteTime_SeededWithLowBitCleared_AndCasWorks()
    {
        var node = Make(NodeFeature.ExpireWrite, now: 0b1011);
        Assert.Equal(0b1010L, node.WriteTime); // now & ~1L

        Assert.False(node.CasWriteTime(999, 7)); // wrong expected
        Assert.True(node.CasWriteTime(0b1010L, 500));
        Assert.Equal(500L, node.WriteTime);
    }

    [Fact]
    public void AccessDeque_LinksAreUsable_WhenFeaturePresent()
    {
        var a = Make(NodeFeature.MaximumSize);
        var b = Make(NodeFeature.MaximumSize);
        a.SetNextInAccessOrder(b);
        b.SetPreviousInAccessOrder(a);
        Assert.Same(b, a.GetNextInAccessOrder());
        Assert.Same(a, b.GetPreviousInAccessOrder());
    }

    [Fact]
    public void WriteDeque_LinksAreUsable_WhenFeaturePresent()
    {
        var a = Make(NodeFeature.ExpireWrite);
        var b = Make(NodeFeature.ExpireWrite);
        a.SetNextInWriteOrder(b);
        b.SetPreviousInWriteOrder(a);
        Assert.Same(b, a.GetNextInWriteOrder());
        Assert.Same(a, b.GetPreviousInWriteOrder());
    }

    [Fact]
    public void UnsupportedAccessors_Throw_WhenFeatureAbsent()
    {
        var node = Make(NodeFeature.None);
        Assert.Throws<NotSupportedException>(() => node.SetNextInAccessOrder(null));
        Assert.Throws<NotSupportedException>(() => node.CasWriteTime(0, 1));
        Assert.Throws<NotSupportedException>(() => node.QueueType = 1);
    }

    [Fact]
    public void Node_ImplementsDequeElementInterfaces()
    {
        var node = Make(NodeFeature.MaximumSize);
        Assert.IsAssignableFrom<IAccessOrder<Node<Key, Val>>>(node);
        Assert.IsAssignableFrom<IWriteOrder<Node<Key, Val>>>(node);
    }

    private static string TypeNameWithoutNamespace(object o) => o.GetType().Name;
}
