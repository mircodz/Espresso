using System;
using System.Collections.Generic;
using System.Reflection;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>Regression tests for how <c>AddTask</c> links a new node into the eviction policy.</summary>
public class AddTaskTest
{
    private sealed class ManualExecutor : IExecutor
    {
        private readonly Queue<Action> _tasks = new();
        public void Execute(Action action) => _tasks.Enqueue(action);
        public void RunAll()
        {
            while (_tasks.Count != 0) _tasks.Dequeue()();
        }
    }

    private static T Field<T>(object o, string name)
        => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    [Fact]
    public void OversizedAddition_ChangedToZeroBeforeDrain_StaysLinked()
    {
        var executor = new ManualExecutor();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumWeight(10)
            .Weigher((_, v) => int.Parse(v))
            .Executor(executor)
            .Build();
        cache.Put(1, "100"); // the AddTask captures weight 100 (> maximum)
        cache.Put(1, "0");   // the same node now weighs 0, so the size eviction declines
        executor.RunAll();
        cache.CleanUp();

        Assert.Equal("0", cache.GetIfPresent(1));
        var window = Field<AccessOrderDeque<Node<int, string>>>(cache, "_windowDeque");
        var probation = Field<AccessOrderDeque<Node<int, string>>>(cache, "_probationDeque");
        var prot = Field<AccessOrderDeque<Node<int, string>>>(cache, "_protectedDeque");
        Assert.Equal(1, window.Count + probation.Count + prot.Count);

        cache.Put(1, "5");
        cache.CleanUp();
        long policySum = 0;
        foreach (var n in window) policySum += n.PolicyWeight;
        foreach (var n in probation) policySum += n.PolicyWeight;
        foreach (var n in prot) policySum += n.PolicyWeight;
        Assert.Equal(Field<long>(cache, "_weightedSize"), policySum);
        Assert.Equal(5L, policySum);
    }

    [Fact]
    public void OversizedAddition_ChangedToZeroBeforeDrain_StillExpires()
    {
        long now = 0;
        var executor = new ManualExecutor();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumWeight(1)
            .Weigher((_, v) => v.Length)
            .ExpireAfterAccess(TimeSpan.FromSeconds(1))
            .Ticker(() => now)
            .Executor(executor)
            .Build();
        cache.Put(1, "xx"); // AddTask captures 2 (> maximum)
        cache.Put(1, "");   // the same node now has Weight == 0
        executor.RunAll();  // AddTask's size eviction declines
        now += TimeSpan.FromSeconds(2).Ticks * 100L;
        cache.CleanUp();
        Assert.Equal(0, cache.EstimatedSize());
    }
}
