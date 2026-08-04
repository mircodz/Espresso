using System.Threading;
using System.Runtime.CompilerServices;

namespace Espresso.Stats;

/// <summary>
/// Thread-safe cache statistics backed by <see cref="Interlocked"/> counters. A single sealed type
/// (rather than an interface with separate enabled/disabled implementations) so the field that holds it
/// is concrete: the JIT devirtualizes and inlines the record calls on the hot path. When disabled every
/// record method is an inlined no-op the JIT elides after the <c>_enabled</c> branch.
/// </summary>
public sealed class StatsCounter
{
    private readonly bool _enabled;
    private long _hitCount;
    private long _missCount;
    private long _loadSuccessCount;
    private long _loadFailureCount;
    private long _totalLoadTime;
    private long _evictionCount;
    private long _evictionWeight;

    private StatsCounter(bool enabled) => _enabled = enabled;

    /// <summary>A shared no-op counter that records nothing and reports empty stats.</summary>
    public static readonly StatsCounter Disabled = new(enabled: false);

    /// <summary>Creates an active counter that accumulates statistics.</summary>
    public static StatsCounter CreateEnabled() => new(enabled: true);

    /// <summary>Whether this counter records (false for the shared disabled instance).</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Records cache hits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordHits(int count)
    {
        if (_enabled) Interlocked.Add(ref _hitCount, count);
    }

    /// <summary>Records cache misses.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordMisses(int count)
    {
        if (_enabled) Interlocked.Add(ref _missCount, count);
    }

    /// <summary>Records a successful load, given the load time in nanoseconds.</summary>
    public void RecordLoadSuccess(long loadTimeNanos)
    {
        if (!_enabled) return;
        Interlocked.Increment(ref _loadSuccessCount);
        Interlocked.Add(ref _totalLoadTime, loadTimeNanos);
    }

    /// <summary>Records a failed load, given the load time in nanoseconds.</summary>
    public void RecordLoadFailure(long loadTimeNanos)
    {
        if (!_enabled) return;
        Interlocked.Increment(ref _loadFailureCount);
        Interlocked.Add(ref _totalLoadTime, loadTimeNanos);
    }

    /// <summary>Records the eviction of an entry of the given weight and cause.</summary>
    public void RecordEviction(int weight, RemovalCause cause)
    {
        if (!_enabled) return;
        Interlocked.Increment(ref _evictionCount);
        Interlocked.Add(ref _evictionWeight, weight);
    }

    /// <summary>Returns a snapshot of the accumulated statistics.</summary>
    public CacheStats Snapshot() => _enabled
        ? new CacheStats(
            NonNegative(Interlocked.Read(ref _hitCount)),
            NonNegative(Interlocked.Read(ref _missCount)),
            NonNegative(Interlocked.Read(ref _loadSuccessCount)),
            NonNegative(Interlocked.Read(ref _loadFailureCount)),
            NonNegative(Interlocked.Read(ref _totalLoadTime)),
            NonNegative(Interlocked.Read(ref _evictionCount)),
            NonNegative(Interlocked.Read(ref _evictionWeight)))
        : CacheStats.Empty;

    // Guard against a counter overflowing into negative territory (undefined-but-safe semantics).
    private static long NonNegative(long value) => value < 0 ? 0 : value;

    /// <summary>Returns a string describing a snapshot of these statistics.</summary>
    public override string ToString() => Snapshot().ToString();
}
