using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>Tests for the 64-bit policy weight.</summary>
public sealed class PolicyWeightWidthTest
{
    private static object Field(object cache, string name) =>
        cache.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;

    private static long SumPolicyWeights(object cache, string dequeField) =>
        ((IEnumerable)Field(cache, dequeField)).Cast<Node<int, string>>().Sum(n => n.PolicyWeight);

    // A removal listener on a direct executor runs between the node update and its queued
    // UpdateTask, so a second writer's positive delta is applied before the first writer's negative
    // one and the transient sum leaves the int range.
    [Fact]
    public async Task ReorderedUpdates_LeaveNoRegionResidue()
    {
        const int big = int.MaxValue - 1;
        string original = big.ToString();
        using var arrived = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumWeight(10_000_000_000L)
            .Weigher(new FuncWeigher<int, string>((_, v) => int.Parse(v)))
            .RemovalListener(new FuncRemovalListener<int, string>((_, v, _) =>
            {
                if (ReferenceEquals(v, original))
                {
                    arrived.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            }))
            .Executor(DirectExecutor.Instance).Build();
        cache.Put(1, original);

        // The writer blocks inside the listener, so it runs on its own thread.
        Task writer = DedicatedThread.Run(() => cache.Put(1, "1"));
        try
        {
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(10)));
            cache.Put(1, new string(big.ToString().AsSpan())); // positive delta replays first
        }
        finally
        {
            release.Set();
            await writer;
        }
        cache.CleanUp();

        Node<int, string> node = ((IEnumerable)Field(cache, "_windowDeque")).Cast<Node<int, string>>()
            .Concat(((IEnumerable)Field(cache, "_probationDeque")).Cast<Node<int, string>>())
            .Concat(((IEnumerable)Field(cache, "_protectedDeque")).Cast<Node<int, string>>())
            .Single();
        Assert.Equal((long)big, node.PolicyWeight);
        Assert.Equal((long)big, (long)Field(cache, "_weightedSize"));
        Assert.Equal((long)Field(cache, "_windowWeightedSize"), SumPolicyWeights(cache, "_windowDeque"));
        Assert.Equal((long)Field(cache, "_mainProtectedWeightedSize"), SumPolicyWeights(cache, "_protectedDeque"));
    }
}
