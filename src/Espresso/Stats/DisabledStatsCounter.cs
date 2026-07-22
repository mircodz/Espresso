namespace Espresso.Stats;

/// <summary>A no-op <see cref="IStatsCounter"/> that records nothing and always reports empty stats.</summary>
public sealed class DisabledStatsCounter : IStatsCounter
{
    /// <summary>The shared singleton instance.</summary>
    public static readonly DisabledStatsCounter Instance = new();

    private DisabledStatsCounter() { }

    /// <inheritdoc/>
    public void RecordHits(int count) { }
    /// <inheritdoc/>
    public void RecordMisses(int count) { }
    /// <inheritdoc/>
    public void RecordLoadSuccess(long loadTimeNanos) { }
    /// <inheritdoc/>
    public void RecordLoadFailure(long loadTimeNanos) { }
    /// <inheritdoc/>
    public void RecordEviction(int weight, RemovalCause cause) { }
    /// <inheritdoc/>
    public CacheStats Snapshot() => CacheStats.Empty;
}
