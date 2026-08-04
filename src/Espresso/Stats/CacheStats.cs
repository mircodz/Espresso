using System;

namespace Espresso.Stats;

/// <summary>
/// An immutable snapshot of a cache's cumulative statistics. All counts are monotonically increasing
/// over the cache's lifetime. Metric values are undefined (but never throw) on overflow.
/// </summary>
public sealed class CacheStats
{
    private readonly long _hitCount;
    private readonly long _missCount;
    private readonly long _loadSuccessCount;
    private readonly long _loadFailureCount;
    private readonly long _totalLoadTime;
    private readonly long _evictionCount;
    private readonly long _evictionWeight;

    /// <summary>A statistics instance with all counts set to zero.</summary>
    public static CacheStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>Creates a statistics snapshot from raw counts. All counts must be non-negative.</summary>
    public CacheStats(
        long hitCount,
        long missCount,
        long loadSuccessCount,
        long loadFailureCount,
        long totalLoadTime,
        long evictionCount,
        long evictionWeight)
    {
        if (hitCount < 0 || missCount < 0 || loadSuccessCount < 0 || loadFailureCount < 0
            || totalLoadTime < 0 || evictionCount < 0 || evictionWeight < 0)
        {
            throw new ArgumentException("statistics counts must not be negative");
        }
        _hitCount = hitCount;
        _missCount = missCount;
        _loadSuccessCount = loadSuccessCount;
        _loadFailureCount = loadFailureCount;
        _totalLoadTime = totalLoadTime;
        _evictionCount = evictionCount;
        _evictionWeight = evictionWeight;
    }

    /// <summary>Number of lookups that returned either a cached or uncached value (hits + misses).</summary>
    public long RequestCount => SaturatedAdd(_hitCount, _missCount);

    /// <summary>Number of lookups that returned a cached value.</summary>
    public long HitCount => _hitCount;

    /// <summary>Ratio of requests that were hits, or 1.0 when there were no requests.</summary>
    public double HitRate
    {
        get
        {
            long requestCount = RequestCount;
            return requestCount == 0 ? 1.0 : (double)_hitCount / requestCount;
        }
    }

    /// <summary>Number of lookups that returned an uncached (newly loaded) value, or null.</summary>
    public long MissCount => _missCount;

    /// <summary>Ratio of requests that were misses, or 0.0 when there were no requests.</summary>
    public double MissRate
    {
        get
        {
            long requestCount = RequestCount;
            return requestCount == 0 ? 0.0 : (double)_missCount / requestCount;
        }
    }

    /// <summary>Total load attempts (successes + failures).</summary>
    public long LoadCount => SaturatedAdd(_loadSuccessCount, _loadFailureCount);

    /// <summary>Number of loads that successfully produced a new value.</summary>
    public long LoadSuccessCount => _loadSuccessCount;

    /// <summary>Number of loads that failed (no value found or an exception was thrown).</summary>
    public long LoadFailureCount => _loadFailureCount;

    /// <summary>Ratio of load attempts that failed, or 0.0 when there were no loads.</summary>
    public double LoadFailureRate
    {
        get
        {
            long total = SaturatedAdd(_loadSuccessCount, _loadFailureCount);
            return total == 0 ? 0.0 : (double)_loadFailureCount / total;
        }
    }

    /// <summary>Total nanoseconds spent loading new values.</summary>
    public long TotalLoadTime => _totalLoadTime;

    /// <summary>Average nanoseconds spent per load, or 0.0 when there were no loads.</summary>
    public double AverageLoadPenalty
    {
        get
        {
            long total = SaturatedAdd(_loadSuccessCount, _loadFailureCount);
            return total == 0 ? 0.0 : (double)_totalLoadTime / total;
        }
    }

    /// <summary>Number of entries evicted (excludes manual invalidations).</summary>
    public long EvictionCount => _evictionCount;

    /// <summary>Sum of the weights of evicted entries (excludes manual invalidations).</summary>
    public long EvictionWeight => _evictionWeight;

    /// <summary>Returns the difference between this instance and <paramref name="other"/>, floored at zero.</summary>
    public CacheStats Minus(CacheStats other) => new(
        Math.Max(0L, _hitCount - other._hitCount),
        Math.Max(0L, _missCount - other._missCount),
        Math.Max(0L, _loadSuccessCount - other._loadSuccessCount),
        Math.Max(0L, _loadFailureCount - other._loadFailureCount),
        Math.Max(0L, _totalLoadTime - other._totalLoadTime),
        Math.Max(0L, _evictionCount - other._evictionCount),
        Math.Max(0L, _evictionWeight - other._evictionWeight));

    /// <summary>Returns the sum of this instance and <paramref name="other"/> (saturating).</summary>
    public CacheStats Plus(CacheStats other) => new(
        SaturatedAdd(_hitCount, other._hitCount),
        SaturatedAdd(_missCount, other._missCount),
        SaturatedAdd(_loadSuccessCount, other._loadSuccessCount),
        SaturatedAdd(_loadFailureCount, other._loadFailureCount),
        SaturatedAdd(_totalLoadTime, other._totalLoadTime),
        SaturatedAdd(_evictionCount, other._evictionCount),
        SaturatedAdd(_evictionWeight, other._evictionWeight));

    /// <summary>Adds two longs, saturating at <see cref="long.MaxValue"/>/<see cref="long.MinValue"/>.</summary>
    private static long SaturatedAdd(long a, long b)
    {
        long result = unchecked(a + b);
        // Overflow iff the operands share a sign and the result's sign differs from theirs.
        if (((a ^ result) & (b ^ result)) < 0)
        {
            return a < 0 ? long.MinValue : long.MaxValue;
        }
        return result;
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"CacheStats{{hitCount={_hitCount}, missCount={_missCount}, "
           + $"loadSuccessCount={_loadSuccessCount}, loadFailureCount={_loadFailureCount}, "
           + $"totalLoadTime={_totalLoadTime}, evictionCount={_evictionCount}, "
           + $"evictionWeight={_evictionWeight}}}";
}
