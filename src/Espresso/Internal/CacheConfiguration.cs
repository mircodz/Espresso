using Espresso.Stats;

namespace Espresso.Internal;

/// <summary>
/// The resolved, defaults-applied configuration a builder hands to a cache implementation. The builder
/// carries raw, possibly-unset fields; this carries the final values (bare-noun properties, no fluent
/// setters to collide with), computed once by <see cref="EspressoBuilder{K,V}.ToConfiguration"/>.
/// </summary>
internal readonly struct CacheConfiguration<K, V>
    where K : notnull
    where V : class
{
    public int InitialCapacity { get; init; }
    public StatsCounter StatsCounter { get; init; }
    public IRemovalListener<K, V>? RemovalListener { get; init; }
    public IExecutor Executor { get; init; }
    public Ticker Ticker { get; init; }
    public IWeigher<K, V> Weigher { get; init; }
    public IExpiry<K, V>? Expiry { get; init; }
    public IScheduler? Scheduler { get; init; }

    public long Maximum { get; init; }

    public bool Evicts { get; init; }
    public bool IsWeighted { get; init; }
    public bool IsAsync { get; init; }
    public bool ExpiresAfterWrite { get; init; }
    public bool ExpiresAfterAccess { get; init; }
    public bool ExpiresVariable { get; init; }
    public bool RefreshesAfterWrite { get; init; }

    public long ExpiresAfterWriteNanos { get; init; }
    public long ExpiresAfterAccessNanos { get; init; }
    public long RefreshAfterWriteNanos { get; init; }
}
