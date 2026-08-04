using System;
using System.Collections.Generic;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Unit tests for the internal Func-based adapters that back the builder's delegate overloads
/// (weigher/expiry/loader/removal-listener/executor).
/// </summary>
public sealed class FuncAdaptersTest
{
    private const long NanosPerTick = 100L;

    [Fact]
    public void FuncExpiry_SingleFunction_UsedForCreateUpdateAndRead()
    {
        var expiry = new FuncExpiry<int, string>((_, v) => TimeSpan.FromTicks(v.Length));

        long expected = 5 * NanosPerTick;
        Assert.Equal(expected, expiry.ExpireAfterCreate(1, "hello", 0));
        // With the single-function ctor, update/read leave the current duration unchanged.
        Assert.Equal(999, expiry.ExpireAfterUpdate(1, "hello", 0, 999));
        Assert.Equal(999, expiry.ExpireAfterRead(1, "hello", 0, 999));
    }

    [Fact]
    public void FuncExpiry_IndependentFunctions_EachEventUsesItsOwn()
    {
        var expiry = new FuncExpiry<int, string>(
            afterCreate: (_, _) => TimeSpan.FromTicks(1),
            afterUpdate: (_, _) => TimeSpan.FromTicks(2),
            afterRead: (_, _) => TimeSpan.FromTicks(3));

        Assert.Equal(1 * NanosPerTick, expiry.ExpireAfterCreate(1, "a", 0));
        Assert.Equal(2 * NanosPerTick, expiry.ExpireAfterUpdate(1, "a", 0, 999));
        Assert.Equal(3 * NanosPerTick, expiry.ExpireAfterRead(1, "a", 0, 999));
    }

    [Fact]
    public void FuncExpiry_NullUpdateOrRead_KeepsCurrentDuration()
    {
        var expiry = new FuncExpiry<int, string>(
            afterCreate: (_, _) => TimeSpan.FromTicks(1),
            afterUpdate: null,
            afterRead: null);

        Assert.Equal(42, expiry.ExpireAfterUpdate(1, "a", 0, 42));
        Assert.Equal(7, expiry.ExpireAfterRead(1, "a", 0, 7));
    }

    [Fact]
    public void FuncExpiry_NullCreateFunction_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FuncExpiry<int, string>(null!));
    }

    [Fact]
    public void FuncWeigher_ReturnsFunctionResult()
    {
        var weigher = new FuncWeigher<int, string>((_, v) => v.Length);

        Assert.Equal(4, weigher.Weigh(1, "abcd"));
    }

    [Fact]
    public void FuncCacheLoader_Load_InvokesFunction()
    {
        var loader = new FuncCacheLoader<int, string>(k => "v" + k);

        Assert.Equal("v7", loader.Load(7));
    }

    [Fact]
    public void FuncRemovalListener_ForwardsKeyValueAndCause()
    {
        (int? Key, string? Value, RemovalCause Cause)? captured = null;
        var listener = new FuncRemovalListener<int, string>((k, v, c) => captured = (k, v, c));

        listener.OnRemoval(3, "x", RemovalCause.Size);

        Assert.Equal((3, "x", RemovalCause.Size), captured);
    }

    [Fact]
    public void FuncExecutor_RunsTheCommand()
    {
        int runs = 0;
        var executor = new FuncExecutor(action => action());

        executor.Execute(() => runs++);

        Assert.Equal(1, runs);
    }

    [Fact]
    public void Builder_DelegateOverloads_UseFuncAdapters_EndToEnd()
    {
        var evicted = new List<int>();
        var cache = Cache.NewBuilder<int, string>()
            .MaximumWeight(100)
            .Weigher((_, v) => v.Length)
            .RemovalListener((k, _, _) => { if (k is int i) evicted.Add(i); })
            .Executor(DirectExecutor.Instance)
            .Build();

        for (int i = 0; i < 50; i++)
        {
            cache.Put(i, new string('x', 10));
        }
        cache.CleanUp();

        Assert.True(cache.EstimatedSize() <= (100 / 10) + 1);
        Assert.NotEmpty(evicted);
    }
}
