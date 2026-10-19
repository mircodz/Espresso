using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class ConcurrentHashMapStringOrderingTest
{
    [Theory]
    [InlineData("en-US", "sv-SE")]
    [InlineData("sv-SE", "en-US")]
    public async Task StringTree_UsesOrdinalOrderingAcrossCultures(string writerCulture, string readerCulture)
    {
        (string first, string second) = FindHashCollision();
        int bin = Spread(first.GetHashCode()) & 63;
        var keys = new List<string> { first, second };
        for (int i = 0; keys.Count < 30; i++)
        {
            string key = "filler" + i.ToString(CultureInfo.InvariantCulture);
            if ((Spread(key.GetHashCode()) & 63) == bin)
            {
                keys.Add(key);
            }
        }

        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(writerCulture);
            var map = new ConcurrentHashMap<string, string>(64);
            foreach (string key in keys)
            {
                map.Put(key, key);
            }

            var table = (Array)typeof(ConcurrentHashMap<string, string>)
                .GetField("_table", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(map)!;
            Assert.Equal("TreeBin", table.GetValue(bin)!.GetType().Name);
            Assert.Equal(first, map.GetOrDefault(first));
            Assert.Equal(second, map.GetOrDefault(second));

            await Task.Run(() =>
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(readerCulture);
                foreach (string key in keys)
                {
                    Assert.Equal(key, map.GetOrDefault(key));
                }

                Assert.Equal(first, map.Put(first, "updated-first"));
                Assert.Equal(second, map.Put(second, "updated-second"));
                Assert.Equal(30, map.Count);
            });

            Assert.Equal("updated-first", map.GetOrDefault(first));
            Assert.Equal("updated-second", map.GetOrDefault(second));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static (string First, string Second) FindHashCollision()
    {
        // String hashes are randomized per process, so discover a real equal-hash pair at runtime.
        var hashes = new Dictionary<int, string>(500_000);
        for (int i = 0; i < 500_000; i++)
        {
            string key = "ä" + i.ToString("D6", CultureInfo.InvariantCulture);
            hashes.TryAdd(key.GetHashCode(), key);
        }
        for (int i = 0; i < 500_000; i++)
        {
            string key = "z" + i.ToString("D6", CultureInfo.InvariantCulture);
            if (hashes.TryGetValue(key.GetHashCode(), out string? first))
            {
                return (first, key);
            }
        }
        throw new InvalidOperationException("No string hash collision found in the test key ranges.");
    }

    private static int Spread(int hash) => (hash ^ (hash >>> 16)) & int.MaxValue;
}
