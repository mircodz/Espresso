using System.Threading;

namespace Espresso.Stats;

/// <summary>
/// A thread-safe <see cref="IStatsCounter"/> backed by <see cref="Interlocked"/> counters. Recording
/// is a single atomic add per event, cheap enough for the cache's hot paths.
/// </summary>
public sealed class ConcurrentStatsCounter : IStatsCounter
{
    private long _hitCount;
    private long _missCount;
    private long _loadSuccessCount;
    private long _loadFailureCount;
    private long _totalLoadTime;
    private long _evictionCount;
    private long _evictionWeight;

    /// <inheritdoc/>
    public void RecordHits(int count) => Interlocked.Add(ref _hitCount, count);

    /// <inheritdoc/>
    public void RecordMisses(int count) => Interlocked.Add(ref _missCount, count);

    /// <inheritdoc/>
    public void RecordLoadSuccess(long loadTimeNanos)
    {
        Interlocked.Increment(ref _loadSuccessCount);
        Interlocked.Add(ref _totalLoadTime, loadTimeNanos);
    }

    /// <inheritdoc/>
    public void RecordLoadFailure(long loadTimeNanos)
    {
        Interlocked.Increment(ref _loadFailureCount);
        Interlocked.Add(ref _totalLoadTime, loadTimeNanos);
    }

    /// <inheritdoc/>
    public void RecordEviction(int weight, RemovalCause cause)
    {
        Interlocked.Increment(ref _evictionCount);
        Interlocked.Add(ref _evictionWeight, weight);
    }

    /// <inheritdoc/>
    public CacheStats Snapshot() => new(
        NonNegative(Interlocked.Read(ref _hitCount)),
        NonNegative(Interlocked.Read(ref _missCount)),
        NonNegative(Interlocked.Read(ref _loadSuccessCount)),
        NonNegative(Interlocked.Read(ref _loadFailureCount)),
        NonNegative(Interlocked.Read(ref _totalLoadTime)),
        NonNegative(Interlocked.Read(ref _evictionCount)),
        NonNegative(Interlocked.Read(ref _evictionWeight)));

    // Guard against a counter overflowing into negative territory (undefined-but-safe semantics).
    private static long NonNegative(long value) => value < 0 ? 0 : value;

    /// <summary>Returns a string describing a snapshot of these statistics.</summary>
    public override string ToString() => Snapshot().ToString();
}
