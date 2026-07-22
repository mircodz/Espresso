namespace Espresso.Stats;

/// <summary>
/// Accumulates statistics during the operation of a cache. Implementations must be thread-safe;
/// they may be called on the cache's hot paths, so recording should be cheap.
/// </summary>
public interface IStatsCounter
{
    /// <summary>Records cache hits (a lookup returned a cached value).</summary>
    void RecordHits(int count);

    /// <summary>Records cache misses (a lookup returned an uncached value or null).</summary>
    void RecordMisses(int count);

    /// <summary>Records a successful load, given the load time in nanoseconds.</summary>
    void RecordLoadSuccess(long loadTimeNanos);

    /// <summary>Records a failed load, given the load time in nanoseconds.</summary>
    void RecordLoadFailure(long loadTimeNanos);

    /// <summary>Records the eviction of an entry of the given weight and cause.</summary>
    void RecordEviction(int weight, RemovalCause cause);

    /// <summary>Returns a snapshot of the accumulated statistics.</summary>
    CacheStats Snapshot();
}
