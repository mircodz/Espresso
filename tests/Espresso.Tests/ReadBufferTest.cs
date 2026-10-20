using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

/// <summary>Regression tests for read-buffer bookkeeping on caches whose policy does not track access.</summary>
public class ReadBufferTest
{
    private sealed class CountingExecutor : IExecutor
    {
        public int Runs;
        public void Execute(Action command)
        {
            Interlocked.Increment(ref Runs);
            command();
        }
    }

    public static TheoryData<string> NonAccessTrackingConfigs() => new()
    {
        "async-unbounded", "expireAfterWrite", "refreshAfterWrite",
    };

    [Theory]
    [MemberData(nameof(NonAccessTrackingConfigs))]
    public void Hits_OnCacheThatDoesNotTrackAccess_DoNotScheduleMaintenance(string config)
    {
        var executor = new CountingExecutor();
        Func<int> hit;
        switch (config)
        {
            case "async-unbounded":
            {
                IAsyncCache<int, string> cache = Cache.NewBuilder<int, string>().Executor(executor).BuildAsync();
                cache.Put(1, Task.FromResult("a"));
                hit = () => cache.GetIfPresent(1) == null ? 0 : 1;
                break;
            }
            case "expireAfterWrite":
            {
                ICache<int, string> cache = Cache.NewBuilder<int, string>()
                    .ExpireAfterWrite(TimeSpan.FromHours(1)).Executor(executor).Build();
                cache.Put(1, "a");
                hit = () => cache.GetIfPresent(1) == null ? 0 : 1;
                break;
            }
            default:
            {
                ILoadingCache<int, string> cache = Cache.NewBuilder<int, string>()
                    .RefreshAfterWrite(TimeSpan.FromHours(1)).Executor(executor).Build(k => "L" + k);
                cache.Put(1, "a");
                hit = () => cache.GetIfPresent(1) == null ? 0 : 1;
                break;
            }
        }

        int before = Volatile.Read(ref executor.Runs);
        int hits = 0;
        for (int i = 0; i < 100_000; i++)
        {
            hits += hit();
        }

        Assert.Equal(100_000, hits);
        Assert.True(Volatile.Read(ref executor.Runs) - before < 10,
            $"{config}: {executor.Runs - before} maintenance runs for 100k hits");
    }
}
