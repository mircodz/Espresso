using System;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Espresso.Stats;

namespace Espresso;

/// <summary>Entry point for building caches.</summary>
public static class Espresso
{
    /// <summary>Creates a new cache builder for the given key and value types.</summary>
    public static EspressoBuilder<K, V> NewBuilder<K, V>()
        where K : notnull
        where V : class
        => new();
}

/// <summary>
/// A builder of caches, allowing a combination of features. Configuration is validated eagerly; the
/// cache is created by <see cref="Build()"/> or <see cref="Build(ICacheLoader{K,V})"/>.
/// </summary>
/// <typeparam name="K">the key type</typeparam>
/// <typeparam name="V">the value type (reference types only)</typeparam>
public sealed class EspressoBuilder<K, V>
    where K : notnull
    where V : class
{
    private const int Unset = -1;
    private const int DefaultInitialCapacity = 16;

    private int _initialCapacity = Unset;
    private long _maximumSize = Unset;
    private long _maximumWeight = Unset;
    private TimeSpan? _expireAfterWrite;
    private TimeSpan? _expireAfterAccess;
    private TimeSpan? _refreshAfterWrite;
    private IExpiry<K, V>? _expiry;
    private IRemovalListener<K, V>? _removalListener;
    private ITicker? _ticker;
    private IExecutor? _executor;
    private IWeigher<K, V>? _weigher;
    private IScheduler? _scheduler;
    private bool _recordStats;
    private bool _isAsync;

    internal EspressoBuilder() { }

    /// <summary>Sets the minimum total size for the internal data structures.</summary>
    public EspressoBuilder<K, V> InitialCapacity(int initialCapacity)
    {
        Common.RequireState(_initialCapacity == Unset);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _initialCapacity = initialCapacity;
        return this;
    }

    /// <summary>Sets the maximum number of entries the cache may contain.</summary>
    public EspressoBuilder<K, V> MaximumSize(long maximumSize)
    {
        Common.RequireState(_maximumSize == Unset);
        Common.RequireState(_maximumWeight == Unset);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumSize);
        _maximumSize = maximumSize;
        return this;
    }

    /// <summary>Sets the maximum total weight of entries the cache may contain.</summary>
    public EspressoBuilder<K, V> MaximumWeight(long maximumWeight)
    {
        Common.RequireState(_maximumWeight == Unset);
        Common.RequireState(_maximumSize == Unset);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumWeight);
        _maximumWeight = maximumWeight;
        return this;
    }

    /// <summary>Sets the duration after a write when an entry should expire.</summary>
    public EspressoBuilder<K, V> ExpireAfterWrite(TimeSpan duration)
    {
        Common.RequireState(_expireAfterWrite == null);
        Common.RequireState(_expiry == null, "expireAfterWrite cannot be combined with expireAfter");
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        _expireAfterWrite = duration;
        return this;
    }

    /// <summary>Sets the duration after last access when an entry should expire.</summary>
    public EspressoBuilder<K, V> ExpireAfterAccess(TimeSpan duration)
    {
        Common.RequireState(_expireAfterAccess == null);
        Common.RequireState(_expiry == null, "expireAfterAccess cannot be combined with expireAfter");
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        _expireAfterAccess = duration;
        return this;
    }

    /// <summary>Sets the duration after a write when an entry becomes eligible for refresh.</summary>
    public EspressoBuilder<K, V> RefreshAfterWrite(TimeSpan duration)
    {
        Common.RequireState(_refreshAfterWrite == null);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        _refreshAfterWrite = duration;
        return this;
    }

    /// <summary>
    /// Sets a per-entry expiration calculator. Each entry's lifetime is computed on create, update,
    /// and read via <paramref name="expiry"/> and tracked with a hierarchical timer wheel. Cannot be
    /// combined with <see cref="ExpireAfterWrite"/> or <see cref="ExpireAfterAccess"/>.
    /// </summary>
    public EspressoBuilder<K, V> ExpireAfter(IExpiry<K, V> expiry)
    {
        Common.RequireState(_expiry == null, "expireAfter was already set");
        Common.RequireState(_expireAfterWrite == null, "expireAfter cannot be combined with expireAfterWrite");
        Common.RequireState(_expireAfterAccess == null, "expireAfter cannot be combined with expireAfterAccess");
        ArgumentNullException.ThrowIfNull(expiry);
        _expiry = expiry;
        return this;
    }

    /// <summary>Sets a per-entry expiration calculator via a function returning each entry's lifetime.</summary>
    public EspressoBuilder<K, V> ExpireAfter(Func<K, V, TimeSpan> expiry)
    {
        ArgumentNullException.ThrowIfNull(expiry);
        return ExpireAfter(new FuncExpiry<K, V>(expiry));
    }

    /// <summary>Sets a listener notified when an entry is removed.</summary>
    public EspressoBuilder<K, V> RemovalListener(IRemovalListener<K, V> removalListener)
    {
        Common.RequireState(_removalListener == null);
        ArgumentNullException.ThrowIfNull(removalListener);
        _removalListener = removalListener;
        return this;
    }

    /// <summary>Sets a removal-notification callback.</summary>
    public EspressoBuilder<K, V> RemovalListener(Action<K?, V?, RemovalCause> onRemoval)
    {
        ArgumentNullException.ThrowIfNull(onRemoval);
        return RemovalListener(new FuncRemovalListener<K, V>(onRemoval));
    }

    /// <summary>Sets the ticker source used for expiration and load timing.</summary>
    public EspressoBuilder<K, V> Ticker(ITicker ticker)
    {
        Common.RequireState(_ticker == null);
        ArgumentNullException.ThrowIfNull(ticker);
        _ticker = ticker;
        return this;
    }

    /// <summary>Sets the ticker source via a function returning the current time in nanoseconds.</summary>
    public EspressoBuilder<K, V> Ticker(Func<long> ticker)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        return Ticker(new FuncTicker(ticker));
    }

    /// <summary>Enables the accumulation of cache statistics.</summary>
    public EspressoBuilder<K, V> RecordStats()
    {
        _recordStats = true;
        return this;
    }

    /// <summary>Sets the weigher used to determine entry weights (requires <see cref="MaximumWeight"/>).</summary>
    public EspressoBuilder<K, V> Weigher(IWeigher<K, V> weigher)
    {
        Common.RequireState(_weigher == null);
        Common.RequireState(_maximumSize == Unset, "weigher cannot be combined with maximum size");
        ArgumentNullException.ThrowIfNull(weigher);
        _weigher = weigher;
        return this;
    }

    /// <summary>Sets the weigher via a function computing each entry's weight (requires <see cref="MaximumWeight"/>).</summary>
    public EspressoBuilder<K, V> Weigher(Func<K, V, int> weigher)
    {
        ArgumentNullException.ThrowIfNull(weigher);
        return Weigher(new FuncWeigher<K, V>(weigher));
    }

    /// <summary>
    /// Sets the scheduler used to proactively run maintenance so entries expire close to their
    /// deadline instead of only when the cache is next accessed. Optional — expiration is fully correct
    /// without it (lazy on read + eager during any maintenance).
    /// </summary>
    public EspressoBuilder<K, V> Scheduler(IScheduler scheduler)
    {
        Common.RequireState(_scheduler == null);
        ArgumentNullException.ThrowIfNull(scheduler);
        _scheduler = scheduler;
        return this;
    }

    /// <summary>Sets the executor used for background maintenance and notifications.</summary>
    public EspressoBuilder<K, V> Executor(IExecutor executor)
    {
        Common.RequireState(_executor == null);
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
        return this;
    }

    /// <summary>Sets the executor via a function that runs a submitted action.</summary>
    public EspressoBuilder<K, V> Executor(Action<Action> executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return Executor(new FuncExecutor(executor));
    }

    // ----- resolved configuration consumed by the cache implementations -----

    private bool Evicts => _maximumSize != Unset || _maximumWeight != Unset;
    private bool IsWeighted => _weigher != null || _maximumWeight != Unset;
    private bool ExpiresAfterWrite => _expireAfterWrite != null;
    private bool ExpiresAfterAccess => _expireAfterAccess != null;
    private bool ExpiresVariable => _expiry != null;
    private bool RefreshesAfterWrite => _refreshAfterWrite != null;

    private bool UsesTiming => _expireAfterWrite != null || _expireAfterAccess != null
        || _refreshAfterWrite != null || _expiry != null;

    private static long ToNanos(TimeSpan? duration)
        => duration is { } d ? d.Ticks * 100L : 0L; // 1 tick = 100 ns

    /// <summary>Resolves the raw builder fields into a defaults-applied configuration.</summary>
    internal CacheConfiguration<K, V> ToConfiguration() => new()
    {
        InitialCapacity = _initialCapacity == Unset ? DefaultInitialCapacity : _initialCapacity,
        StatsCounter = _recordStats ? new ConcurrentStatsCounter() : DisabledStatsCounter.Instance,
        RemovalListener = _removalListener,
        Executor = _executor ?? ThreadPoolExecutor.Instance,
        Ticker = _ticker ?? (UsesTiming ? SystemTicker.Instance : DisabledTicker.Instance),
        Weigher = _weigher ?? SingletonWeigher<K, V>.Instance,
        Expiry = _expiry,
        Scheduler = _scheduler == null || ReferenceEquals(_scheduler, Schedulers.Disabled) ? null : _scheduler,
        Maximum = _maximumWeight != Unset ? _maximumWeight : _maximumSize != Unset ? _maximumSize : 0,
        Evicts = Evicts,
        IsWeighted = IsWeighted,
        IsAsync = _isAsync,
        ExpiresAfterWrite = ExpiresAfterWrite,
        ExpiresAfterAccess = ExpiresAfterAccess,
        ExpiresVariable = ExpiresVariable,
        RefreshesAfterWrite = RefreshesAfterWrite,
        ExpiresAfterWriteNanos = ToNanos(_expireAfterWrite),
        ExpiresAfterAccessNanos = ToNanos(_expireAfterAccess),
        RefreshAfterWriteNanos = ToNanos(_refreshAfterWrite),
    };

    // A bounded cache is needed whenever any size or time-based feature is configured.
    private bool IsBounded => Evicts || ExpiresAfterWrite || ExpiresAfterAccess || RefreshesAfterWrite
        || ExpiresVariable;

    /// <summary>
    /// Validates the weigher/maximum-weight pairing: a weigher requires <see cref="MaximumWeight"/>,
    /// and <see cref="MaximumWeight"/> requires a weigher.
    /// </summary>
    private void RequireWeightWithWeigher()
    {
        if (_weigher == null)
        {
            Common.RequireState(_maximumWeight == Unset, "maximumWeight requires a weigher");
        }
        else
        {
            Common.RequireState(_maximumWeight != Unset, "weigher requires maximumWeight");
        }
    }

    /// <summary>Validates that no loading-only feature (refresh) is configured on a non-loading cache.</summary>
    private void RequireNonLoadingCache()
    {
        Common.RequireState(_refreshAfterWrite == null, "refreshAfterWrite requires a loading cache");
    }

    // ----- build -----

    /// <summary>Builds a cache without automatic loading.</summary>
    public ICache<K, V> Build()
    {
        RequireWeightWithWeigher();
        RequireNonLoadingCache();
        var config = ToConfiguration();
        return IsBounded
            ? new BoundedLocalCache<K, V>(config, loader: null)
            : new UnboundedLocalCache<K, V>(config, loader: null);
    }

    /// <summary>Builds a cache that loads values via <paramref name="loader"/>.</summary>
    public ILoadingCache<K, V> Build(ICacheLoader<K, V> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        RequireWeightWithWeigher();
        var config = ToConfiguration();
        return IsBounded
            ? new BoundedLocalCache<K, V>(config, loader)
            : new UnboundedLocalCache<K, V>(config, loader);
    }

    /// <summary>Builds a cache that loads values via the <paramref name="loader"/> function.</summary>
    public ILoadingCache<K, V> Build(Func<K, V?> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        return Build(new FuncCacheLoader<K, V>(loader));
    }

    /// <summary>Builds an asynchronous cache without automatic loading.</summary>
    public IAsyncCache<K, V> BuildAsync()
    {
        RequireWeightWithWeigher();
        RequireNonLoadingCache();
        return new LocalAsyncCache<K, V>(BuildAsyncStore());
    }

    /// <summary>Builds an asynchronous cache that loads values via <paramref name="loader"/>.</summary>
    public IAsyncLoadingCache<K, V> BuildAsync(IAsyncCacheLoader<K, V> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        RequireWeightWithWeigher();
        return new LocalAsyncLoadingCache<K, V>(BuildAsyncStore(), loader);
    }

    /// <summary>Builds an asynchronous cache that loads values via the <paramref name="loader"/> function.</summary>
    public IAsyncLoadingCache<K, V> BuildAsync(Func<K, CancellationToken, Task<V?>> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        return BuildAsync(new FuncAsyncCacheLoader<K, V>(loader));
    }

    /// <summary>Builds an asynchronous cache that loads values via the <paramref name="loader"/> function.</summary>
    public IAsyncLoadingCache<K, V> BuildAsync(Func<K, Task<V?>> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        return BuildAsync(new FuncAsyncCacheLoader<K, V>((key, _) => loader(key)));
    }

    /// <summary>Constructs the future-typed backing store for an async cache.</summary>
    private ILocalCache<K, Task<V>> BuildAsyncStore()
    {
        var inner = new EspressoBuilder<K, Task<V>>
        {
            _initialCapacity = _initialCapacity,
            _maximumSize = _maximumSize,
            _maximumWeight = _maximumWeight,
            _expireAfterWrite = _expireAfterWrite,
            _expireAfterAccess = _expireAfterAccess,
            _refreshAfterWrite = _refreshAfterWrite,
            _expiry = _expiry == null ? null : new AsyncExpiry<K, V>(_expiry),
            _ticker = _ticker,
            _executor = _executor,
            _scheduler = _scheduler,
            _recordStats = _recordStats,
            _isAsync = true,
            // Wrap so an in-flight future weighs 0 and is pinned (not size-evicted) until it resolves.
            _weigher = new AsyncWeigher<K, V>(_weigher ?? SingletonWeigher<K, V>.Instance),
            _removalListener = _removalListener == null
                ? null
                : new AsyncRemovalListener<K, V>(_removalListener, _executor ?? ThreadPoolExecutor.Instance),
        };

        return new BoundedLocalCache<K, Task<V>>(inner.ToConfiguration(), loader: null);
    }
}
