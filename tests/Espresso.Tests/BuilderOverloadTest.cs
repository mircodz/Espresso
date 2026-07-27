using System;
using System.Threading.Tasks;
using Xunit;
namespace Espresso.Tests;
public sealed class BuilderOverloadTest
{
    [Fact]
    public void AllDelegateOverloadsResolve()
    {
        // Every one of these must compile & resolve to the Func/Action overload from a bare lambda.
        var loading = Espresso.NewBuilder<int,string>().Build(k => "v" + k);
        Assert.Equal("v1", loading.Get(1));

        var weighted = Espresso.NewBuilder<int,string>()
            .MaximumWeight(100).Weigher((k, v) => v.Length).Build();
        weighted.Put(1, "abc");

        RemovalCause? seen = null;
        var watched = Espresso.NewBuilder<int,string>()
            .RemovalListener((k, v, c) => seen = c).Build();
        watched.Put(1, "a"); watched.Invalidate(1);
        Assert.Equal(RemovalCause.Explicit, seen);

        var expiring = Espresso.NewBuilder<int,string>()
            .ExpireAfter((k, v) => TimeSpan.FromMinutes(5))
            .Ticker(() => 0L)
            .Executor(a => a())
            .Build();
        expiring.Put(1, "a");
        Assert.Equal("a", expiring.GetIfPresent(1));

        var asyncCache = Espresso.NewBuilder<int,string>().BuildAsync((k, ct) => Task.FromResult<string?>("v"));
        Assert.Equal("v", asyncCache.Get(1).GetAwaiter().GetResult());

        var asyncCache2 = Espresso.NewBuilder<int,string>().BuildAsync(k => Task.FromResult<string?>("w"));
        Assert.Equal("w", asyncCache2.Get(1).GetAwaiter().GetResult());
    }
}
