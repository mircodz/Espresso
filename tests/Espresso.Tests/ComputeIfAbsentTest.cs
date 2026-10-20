using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Espresso.Tests;

/// <summary>Tests for the <c>BoundedLocalCache.ComputeIfAbsent</c> (<c>Get(key, fn)</c>) hit path.</summary>
public class ComputeIfAbsentTest
{
    private const long Hour = 3_600_000_000_000L;

    /// <summary>Blocks <see cref="ExpireAfterRead"/> once armed, so a test can interleave a concurrent write.</summary>
    private sealed class GatedReadExpiry : IExpiry<int, string>
    {
        public volatile bool Armed;
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();

        public long ExpireAfterCreate(int key, string value, long currentTime) => Hour;
        public long ExpireAfterUpdate(int key, string value, long currentTime, long currentDuration) => Hour;
        public long ExpireAfterRead(int key, string value, long currentTime, long currentDuration)
        {
            if (Armed)
            {
                Armed = false;
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            return currentDuration;
        }
    }

    [Fact]
    public async Task Get_HitRacingInvalidate_NeverReturnsNullForNonNullLoader()
    {
        for (int i = 0; i < 200; i++)
        {
            var expiry = new GatedReadExpiry();
            ICache<int, string> cache = Cache.NewBuilder<int, string>()
                .ExpireAfter(expiry)
                .Executor(DirectExecutor.Instance)
                .Build();
            cache.Put(1, "old");

            expiry.Armed = true;
            Task<string?> reader = DedicatedThread.Run(() => cache.Get(1, _ => "loaded"));
            Assert.True(expiry.Entered.Wait(TimeSpan.FromSeconds(10)));

            cache.Invalidate(1); // removal task drains inline and kills the node
            expiry.Release.Set();

            string? result = await reader;
            Assert.True(result is "old" or "loaded", $"iteration {i}: Get returned '{result ?? "null"}'");
        }
    }
}
