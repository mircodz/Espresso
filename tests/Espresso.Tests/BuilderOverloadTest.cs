using System;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

public sealed class BuilderOverloadTest
{
    [Fact]
    public void Build_LoadingLambda_ResolvesFuncOverload()
    {
        var loading = Cache.NewBuilder<int, string>().Build(k => "v" + k);

        Assert.Equal("v1", loading.Get(1));
    }

    [Fact]
    public void Build_WeigherLambda_ResolvesFuncOverload()
    {
        var weighted = Cache.NewBuilder<int, string>()
            .MaximumWeight(100).Weigher((k, v) => v.Length).Build();

        var act = () => weighted.Put(1, "abc");

        var ex = Record.Exception(act);
        Assert.Null(ex);
    }

    [Fact]
    public void Build_RemovalListenerLambda_ObservesExplicitCause()
    {
        RemovalCause? seen = null;
        var watched = Cache.NewBuilder<int, string>()
            .RemovalListener((k, v, c) => seen = c).Build();

        watched.Put(1, "a");
        watched.Invalidate(1);

        Assert.Equal(RemovalCause.Explicit, seen);
    }

    [Fact]
    public void Build_ExpireAfterTickerExecutorLambdas_ResolveOverloads()
    {
        var expiring = Cache.NewBuilder<int, string>()
            .ExpireAfter((k, v) => TimeSpan.FromMinutes(5))
            .Ticker(() => 0L)
            .Executor(a => a())
            .Build();

        expiring.Put(1, "a");

        Assert.Equal("a", expiring.GetIfPresent(1));
    }

    [Fact]
    public void BuildAsync_WithCancellationTokenLambda_ResolvesOverload()
    {
        var asyncCache = Cache.NewBuilder<int, string>()
            .BuildAsync((k, ct) => Task.FromResult<string?>("v"));

        Assert.Equal("v", asyncCache.Get(1).GetAwaiter().GetResult());
    }

    [Fact]
    public void BuildAsync_WithKeyOnlyLambda_ResolvesOverload()
    {
        var asyncCache = Cache.NewBuilder<int, string>()
            .BuildAsync(k => Task.FromResult<string?>("w"));

        Assert.Equal("w", asyncCache.Get(1).GetAwaiter().GetResult());
    }
}
