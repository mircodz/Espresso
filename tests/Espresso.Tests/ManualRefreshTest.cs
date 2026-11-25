using System;
using System.Collections.Generic;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// <see cref="ILoadingCache{K,V}.Refresh"/> follows Caffeine's <c>LoadingCache.refresh</c>, on both the
/// bounded and the unbounded cache.
/// </summary>
public sealed class ManualRefreshTest
{
    public static IEnumerable<object[]> Both() => new[] { new object[] { false }, new object[] { true } };

    [Theory]
    [MemberData(nameof(Both))]
    public void Refresh_NullKey_ThrowsBeforeInvokingTheLoader(bool bounded)
    {
        int loads = 0;
        EspressoBuilder<string, string> builder = Cache.NewBuilder<string, string>()
            .Executor(DirectExecutor.Instance).RecordStats();
        if (bounded)
        {
            builder.MaximumSize(100);
        }
        using ILoadingCache<string, string> cache = builder.Build(_ => { loads++; return "v"; });

        var error = Assert.Throws<ArgumentNullException>(() => cache.Refresh(null!));

        Assert.Equal("key", error.ParamName);
        Assert.Equal(0, loads);
        Assert.Equal(0, cache.Stats().LoadCount);
        Assert.Equal(0, cache.EstimatedSize());
    }
}
