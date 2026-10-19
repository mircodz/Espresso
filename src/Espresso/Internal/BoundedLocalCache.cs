using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Stats;

namespace Espresso.Internal;

/// <summary>
/// A cache bounded by a maximum size or weight, implementing the W-TinyLFU eviction policy driven by
/// a buffered, single-threaded maintenance pump.
/// <para>
/// Reads and writes are recorded into lossy/lossless buffers and applied later under an eviction
/// lock by <see cref="Maintenance"/>, which drains the buffers, then expires, evicts, and adapts.
/// Size/weight eviction, access/write/variable expiration, refresh-after-write, and the adaptive
/// window climber are all enabled; each is gated behind a predicate so a configuration only pays for
/// the features it uses.
/// </para>
/// </summary>
internal sealed class BoundedLocalCache<K, V> : ILocalCache<K, V>, ILoadingCache<K, V>, ITimerWheelCache<K, V>
    where K : notnull
    where V : class
{
    // Eviction tuning constants.
    private const double PercentMain = 0.99;
    private const double PercentMainProtected = 0.80;
    private const int AdmitHashDosThreshold = 6;
    private const int WriteBufferMin = 4;
    // Per-CPU cap so writers rarely fill the buffer; also bounds tasks drained per maintenance cycle.
    private static readonly int WriteBufferMax = 128 * Common.CeilingPowerOfTwo(Environment.ProcessorCount);
    private const int MaxPutSpinWaitAttempts = 1024 - 1;
    private const long MaximumCapacity = long.MaxValue - int.MaxValue;

    // Expiration constants.
    private const int ExpirationThreshold = 1_000;      // max entries expired per maintenance cycle
    private const long ExpireTolerance = 1_000_000_000; // 1 second, in nanoseconds

    // Adaptive hill-climber constants.
    private const double HillClimberRestartThreshold = 0.05;
    private const double HillClimberStepPercent = 0.0625;
    private const double HillClimberStepDecayRate = 0.98;
    private const double HillClimberMinInitialStep = 2.0;
    private const long SmallCacheThreshold = 512L;
    private const double SmallCacheSampleRatioCap = 4.0;
    private const double SmallCacheStepDecayRate = 0.995;
    private const int QueueTransferThreshold = 1_000;

    // Async future-value sentinels. An in-flight future's expiry timestamp is pushed ~220 years out
    // so it cannot expire while loading; on completion the real timestamps are restored. Detection of
    // "still in-flight" uses the range check `duration > MaximumExpiry`.
    internal const long MaximumExpiry = long.MaxValue >> 1;
    internal const long AsyncExpiry = (long.MaxValue >> 1) + (long.MaxValue >> 2);

    // Drain status state machine.
    private const int Idle = 0;
    private const int Required = 1;
    private const int ProcessingToIdle = 2;
    private const int ProcessingToRequired = 3;

    private readonly ConcurrentHashMap<K, Node<K, V>> _data;
    private readonly StatsCounter _statsCounter;
    private readonly IRemovalListener<K, V>? _removalListener;
    private readonly IExecutor _executor;
    private readonly Ticker _ticker;
    private readonly Ticker _statsTicker;
    private readonly IWeigher<K, V> _weigher;
    private readonly ICacheLoader<K, V>? _loader;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowIfKeyNull(K key, [CallerArgumentExpression(nameof(key))] string? param = null)
    {
        if (default(K) is null && key is null)
        {
            throw new ArgumentNullException(param);
        }
    }

    /// <summary>Weighs an entry and validates the weigher's contract (weight must be non-negative).</summary>
    private int WeighEntry(K key, V value)
    {
        // Unweighted caches use the singleton weigher (always 1); skip the interface dispatch entirely so
        // the common case does not pay a vtable call on every insert/update.
        if (!_isWeighted)
        {
            return 1;
        }
        int weight = _weigher.Weigh(key, value);
        if (weight < 0)
        {
            throw new ArgumentException($"The weigher returned a negative weight ({weight}).");
        }
        return weight;
    }

    // For an async cache, produces a NEW in-flight future to store on refresh, given the key and the
    // current (ready) stored future. Installed by the async loading wrapper; null for sync caches.
    // Returning null means "not eligible" (e.g. the current future is not ready). The wrapper is
    // responsible for readiness checks, adapting the loader result, and attaching completion handling.
    private volatile Func<K, V, V?>? _asyncReload;

    internal void SetAsyncReload(Func<K, V, V?> asyncReload) => _asyncReload = asyncReload;

    // Feature predicates: each gates the code for an enabled feature so a configuration only pays for
    // what it uses.
    private readonly bool _evicts;
    private readonly bool _isWeighted;
    private readonly bool _isAsync;
    private readonly bool _expiresAfterAccess;
    private readonly bool _expiresAfterWrite;
    private readonly bool _refreshAfterWrite;
    private readonly bool _expiresVariable;
    // Whether the policy consumes recorded reads; when false, nothing drains the read buffer, so reads
    // are not offered to it (otherwise it fills and every hit schedules a no-op maintenance cycle).
    private readonly bool _tracksAccess;
    private readonly IExpiry<K, V>? _expiry;
    private readonly TimerWheel<K, V>? _timerWheel;
    private readonly Pacer? _pacer;
    private readonly long _expiresAfterAccessNanos;
    private readonly long _expiresAfterWriteNanos;
    private readonly long _refreshAfterWriteNanos;
    private readonly NodeFeature _nodeFeatures;

    // In-flight refresh registrations, keyed by the node's key reference (debounces concurrent reloads).
    private readonly ConcurrentHashMap<object, object> _refreshes = new();

    // Eviction policy state (guarded by _evictionLock).
    private readonly object _evictionLock = new();
    private readonly FrequencySketch _sketch = new();
    private readonly BoundedBuffer<Node<K, V>> _readBuffer = new();
    private readonly MpscGrowableArrayQueue<IWriteTask> _writeBuffer = new(WriteBufferMin, WriteBufferMax);
    private readonly AccessOrderDeque<Node<K, V>> _windowDeque = new();
    private readonly AccessOrderDeque<Node<K, V>> _probationDeque = new();
    private readonly AccessOrderDeque<Node<K, V>> _protectedDeque = new();
    private readonly WriteOrderDeque<Node<K, V>> _writeOrderDeque = new();

    private long _maximum;
    private long _weightedSize;
    private long _windowMaximum;
    private long _windowWeightedSize;
    private long _mainProtectedMaximum;
    private long _mainProtectedWeightedSize;
    private long _hitsInSample;
    private long _missesInSample;
    private double _stepSize;
    private long _adjustment;
    private double _previousSampleHitRate;

    private int _drainStatus = Idle;

    // A single reusable maintenance delegate, so scheduling a drain never allocates a fresh closure.
    // It is bound to a PerformCleanupTask that only weakly references this cache, so a pending
    // scheduler timer or queued work item does not keep a dropped cache alive.
    private readonly Action _drainBuffersTask;

    // Cached read-buffer consumer so draining never allocates a fresh Action<Node> per cycle.
    private readonly Action<Node<K, V>> _onAccess;

    internal BoundedLocalCache(in CacheConfiguration<K, V> config, ICacheLoader<K, V>? loader)
    {
        _drainBuffersTask = new PerformCleanupTask(this).Run;
        _onAccess = OnAccess;
        _data = new ConcurrentHashMap<K, Node<K, V>>(config.InitialCapacity);
        _statsCounter = config.StatsCounter;
        _removalListener = config.RemovalListener;
        _executor = config.Executor;
        _ticker = config.Ticker;
        _statsTicker = config.StatsTicker;
        _weigher = config.Weigher;
        _loader = loader;

        _evicts = config.Evicts;
        _isWeighted = config.IsWeighted;
        _isAsync = config.IsAsync;
        _expiresAfterAccess = config.ExpiresAfterAccess;
        _expiresAfterWrite = config.ExpiresAfterWrite;
        _refreshAfterWrite = config.RefreshesAfterWrite;
        _expiresVariable = config.ExpiresVariable;
        _tracksAccess = _evicts || _expiresAfterAccess || _expiresVariable;
        _expiry = config.Expiry;
        _timerWheel = _expiresVariable ? new TimerWheel<K, V>() : null;
        IScheduler? scheduler = config.Scheduler;
        _pacer = scheduler != null ? new Pacer(scheduler) : null;
        _expiresAfterAccessNanos = config.ExpiresAfterAccessNanos;
        _expiresAfterWriteNanos = config.ExpiresAfterWriteNanos;
        _refreshAfterWriteNanos = config.RefreshAfterWriteNanos;

        // Select the smallest node variant that carries the fields this configuration needs.
        NodeFeature features = _isWeighted ? NodeFeature.MaximumWeight
            : _evicts ? NodeFeature.MaximumSize : NodeFeature.None;
        if (_expiresAfterAccess) features |= NodeFeature.ExpireAccess;
        if (_expiresAfterWrite) features |= NodeFeature.ExpireWrite;
        if (_refreshAfterWrite) features |= NodeFeature.RefreshWrite;
        if (_expiresVariable) features |= NodeFeature.ExpireVariable;
        _nodeFeatures = features;

        SetMaximum(config.Maximum);
    }

    // ----- feature predicates -----

    private bool Evicts => _evicts;
    private bool ExpiresAfterAccess => _expiresAfterAccess;
    private bool ExpiresAfterWrite => _expiresAfterWrite;
    private bool ExpiresVariable => _expiresVariable;
    private bool RefreshAfterWrite => _refreshAfterWrite;
    private bool CollectKeys => false;
    private bool CollectValues => false;

    private bool Expires => _expiresAfterAccess || _expiresAfterWrite || _expiresVariable;

    // ----- ILocalCache collaborators -----

    public StatsCounter StatsCounter => _statsCounter;
    public IExecutor Executor => _executor;
    public Ticker Ticker => _ticker;
    public Ticker StatsTicker => _statsTicker;
    public bool IsRecordingStats => _statsCounter.IsEnabled;

    // ----- sizing -----

    private void SetMaximum(long maximum)
    {
        if (!_evicts)
        {
            return; // an expiration-only cache has no size bound
        }
        long max = Math.Min(maximum, MaximumCapacity);
        long window = max - (long)(PercentMain * max);
        long mainProtected = (long)(PercentMainProtected * (max - window));

        _maximum = max;
        _windowMaximum = window;
        _mainProtectedMaximum = mainProtected;
        _hitsInSample = 0;
        _missesInSample = 0;

        double stepSize = Math.Max(HillClimberStepPercent * max, HillClimberMinInitialStep);
        _stepSize = max <= SmallCacheThreshold ? stepSize : -stepSize;

        if (!_isWeighted && _weightedSize >= (max >>> 1))
        {
            _sketch.EnsureCapacity(max);
        }
    }

    // ----- read path -----

    public V? GetIfPresent(K key, bool recordStats)
    {
        ThrowIfKeyNull(key);
        Node<K, V>? node = _data.GetOrDefault(key);
        if (node == null)
        {
            if (recordStats) _statsCounter.RecordMisses(1);
            return null;
        }
        V? value = node.Value;
        long now = _ticker.Read();
        if (value == null || HasExpired(node, now))
        {
            if (recordStats) _statsCounter.RecordMisses(1);
            return null; // absent or logically expired (physical eviction happens in maintenance)
        }
        if (recordStats) _statsCounter.RecordHits(1);
        SetAccessTime(node, now);
        TryExpireAfterRead(node, key, value, now);
        AfterRead(node, now);
        return value;
    }

    /// <summary>
    /// Asynchronously reloads the entry if it is past its refresh-after-write threshold and no
    /// refresh is already in flight. The stale value keeps being served until the reload completes.
    /// The write-time low bit is used as an in-progress flag.
    /// </summary>
    private void RefreshIfNeeded(Node<K, V> node, long now)
    {
        // The eligibility check is split into a separate, non-inlined method so its closure is only
        // allocated when a refresh is actually configured, keeping the read-hit path allocation-free.
        if (!_refreshAfterWrite)
        {
            return;
        }
        // Async caches need the async-reload delegate installed; without it (e.g. a manual async
        // cache with no loader) there is nothing to refresh with.
        if (_isAsync && _asyncReload == null)
        {
            return;
        }
        RefreshTryStart(node, now);
    }

    /// <summary>Attempts to start a background refresh for an overdue entry.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RefreshTryStart(Node<K, V> node, long now)
    {
        long writeTime = node.WriteTime;
        long refreshWriteTime = writeTime | 1L;
        K? key = node.Key;
        V? oldValue = node.Value;

        // Eligible only if overdue, still alive, not already refreshing (low bit clear / not registered),
        // and we win the CAS that sets the in-progress flag.
        if ((now - writeTime) <= _refreshAfterWriteNanos
            || key == null || oldValue == null
            || (writeTime & 1L) != 0L
            || !node.IsAlive)
        {
            return;
        }
        object keyRef = node.KeyReference;
        if (_refreshes.ContainsKey(keyRef))
        {
            return;
        }
        if (!node.CasWriteTime(writeTime, refreshWriteTime))
        {
            return;
        }

        // Register a placeholder token so concurrent readers debounce, then reload in the background.
        var token = new object();
        try
        {
            object? registered = _refreshes.ComputeIfAbsent(keyRef, _ =>
                (node.IsAlive && node.WriteTime == refreshWriteTime) ? token : null);
            if (!ReferenceEquals(registered, token))
            {
                return; // another refresh owns the registration
            }
        }
        finally
        {
            node.CasWriteTime(refreshWriteTime, writeTime); // clear the in-progress flag
        }

        if (_isAsync)
        {
            RefreshAsync(key, oldValue, keyRef, token, writeTime);
        }
        else
        {
            RefreshSync(key, oldValue, keyRef, token, writeTime);
        }
    }

    /// <summary>
    /// Async refresh: the delegate synchronously starts a reload and returns a NEW in-flight future,
    /// which becomes the stored value (replacing the old future) if the entry was not modified in
    /// flight. The wrapper attaches completion handling to that new future. A failure to start or
    /// install the reload never reaches the reader and releases only this refresh's registration; a
    /// reload that is not installed is reported like a discarded value.
    /// </summary>
    private void RefreshAsync(K key, V oldValue, object keyRef, object token, long writeTime)
    {
        V? newFuture;
        try
        {
            newFuture = _asyncReload!(key, oldValue);
        }
        catch
        {
            _refreshes.Remove(keyRef, token);
            return;
        }
        if (newFuture == null)
        {
            _refreshes.Remove(keyRef, token); // not eligible (e.g. current future not ready)
            return;
        }

        Node<K, V>? updated = null;
        int weightDifference = 0;
        RemovalCause? discardCause = null;
        try
        {
            _data.Compute(key, (_, current) =>
            {
                if (current == null)
                {
                    discardCause = RemovalCause.Explicit; // the entry is gone; drop the reload
                    return null;
                }
                lock (current)
                {
                    if (!IsUnmodifiedSinceRefresh(current, oldValue, keyRef, token, writeTime))
                    {
                        // Superseded by a concurrent write: the reload is discarded (its future is orphaned).
                        if (!ReferenceEquals(current.Value, newFuture))
                        {
                            discardCause = RemovalCause.Replaced;
                        }
                        return current;
                    }
                    // Weigh before mutating so a throwing weigher leaves the entry untouched.
                    long now = _ticker.Read();
                    int newWeight = WeighEntry(key, newFuture);
                    weightDifference = newWeight - current.Weight;
                    current.Value = newFuture;
                    current.Weight = newWeight;
                    // Reset the write time to now so a reload in flight is no longer "overdue" (prevents a
                    // second refresh from being triggered on the next access while this one is still
                    // loading), then apply the async sentinel if the cache also expires.
                    if (_expiresAfterWrite || _refreshAfterWrite) current.WriteTime = now;
                    StampAsyncExpiryIfComputing(current, now);
                    updated = current;
                    return current;
                }
            });
        }
        catch
        {
            // A user callback threw before anything was installed: the best-effort refresh is dropped
            // without failing the reader.
            discardCause = null;
        }

        if (updated == null)
        {
            _refreshes.Remove(keyRef, token); // nothing installed; release the registration now
            if (discardCause != null)
            {
                NotifyRemoval(key, newFuture, discardCause.Value);
            }
            return;
        }

        // Propagate the weight change to the policy bookkeeping (the in-flight future weighs 0 via
        // AsyncWeigher), matching how Replace/UpdateTask keep the weighted-size telescoping.
        AfterWrite(new UpdateTask(this, updated, weightDifference));

        // Keep the debounce token registered until the new future completes; Java clears it on
        // completion so no fresh refresh is triggered while this reload is in flight.
        ((Task)(object)newFuture).ContinueWith(_ => _refreshes.Remove(keyRef, token),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Synchronous refresh: submits the reload to the executor. A rejected submission leaves nothing
    /// in flight, so it releases this refresh's registration and lets the reader proceed.
    /// </summary>
    private void RefreshSync(K key, V oldValue, object keyRef, object token, long writeTime)
    {
        try
        {
            _executor.Execute(() => RefreshSyncTask(key, oldValue, keyRef, token, writeTime));
        }
        catch
        {
            _refreshes.Remove(keyRef, token);
        }
    }

    /// <summary>
    /// Loads a resolved value and commits it if the entry was not modified while in flight. The
    /// ownership/ABA check and every mutation happen under the entry's lock (which optimized puts use),
    /// and the expiry and weight are computed before the entry is mutated. A null reload removes the
    /// entry; a discarded reload is reported. Nothing escapes the work item, and the registration is
    /// always released (by identity, so a successor's registration is never taken).
    /// </summary>
    private void RefreshSyncTask(K key, V oldValue, object keyRef, object token, long writeTime)
    {
        try
        {
            long start = _statsTicker.Read();
            V? newValue;
            try
            {
                newValue = _loader!.Load(key);
            }
            catch
            {
                _statsCounter.RecordLoadFailure(_statsTicker.Read() - start);
                return;
            }
            long loadTime = _statsTicker.Read() - start;

            Node<K, V>? updated = null;
            Node<K, V>? removed = null;
            int weightDifference = 0;
            RemovalCause? discardCause = null;
            try
            {
                _data.Compute(key, (_, current) =>
                {
                    if (current == null)
                    {
                        if (newValue != null)
                        {
                            discardCause = RemovalCause.Explicit; // the entry is gone; drop the reload
                        }
                        return null;
                    }
                    lock (current)
                    {
                        if (!IsUnmodifiedSinceRefresh(current, oldValue, keyRef, token, writeTime))
                        {
                            // Superseded by a concurrent write: discard the reload, preserving the entry.
                            if (newValue != null && !ReferenceEquals(current.Value, newValue))
                            {
                                discardCause = RemovalCause.Replaced;
                            }
                            return current;
                        }
                        if (newValue == null)
                        {
                            removed = current; // a null reload removes the entry
                            current.Retire();
                            return null;
                        }
                        // Evaluate the user callbacks before mutating so a throw leaves the node untouched.
                        long now = _ticker.Read();
                        long varTime = ExpireAfterUpdate(current, key, newValue, now);
                        int newWeight = WeighEntry(key, newValue);
                        weightDifference = newWeight - current.Weight;
                        current.Value = newValue;
                        current.Weight = newWeight;
                        if (_expiresAfterAccess) current.AccessTime = now;
                        if (_expiresAfterWrite || _refreshAfterWrite) current.WriteTime = now;
                        SetVariableTime(current, varTime);
                        updated = current;
                        return current;
                    }
                });
            }
            catch
            {
                // A user callback (expiry, weigher, key equality) threw before the entry was mutated.
                _statsCounter.RecordLoadFailure(loadTime);
                return;
            }

            if (updated != null)
            {
                // Propagate the weight change and wheel reschedule through the policy.
                AfterWrite(new UpdateTask(this, updated, weightDifference));
                if (!ReferenceEquals(newValue, oldValue))
                {
                    NotifyRemoval(key, oldValue, RemovalCause.Replaced);
                }
            }
            else if (removed != null)
            {
                AfterWrite(new RemovalTask(this, removed));
                NotifyRemoval(key, oldValue, RemovalCause.Explicit);
            }
            if (discardCause != null)
            {
                NotifyRemoval(key, newValue, discardCause.Value);
            }

            if (newValue == null)
            {
                _statsCounter.RecordLoadFailure(loadTime);
            }
            else
            {
                _statsCounter.RecordLoadSuccess(loadTime);
            }
        }
        catch
        {
            // A best-effort refresh must not fail the executor's thread (no logger is available).
        }
        finally
        {
            _refreshes.Remove(keyRef, token);
        }
    }

    /// <summary>
    /// Returns whether a refresh still owns its registration and the entry is unchanged since the
    /// reload started (same value instance and write time, ignoring the in-progress flag). Must be
    /// called under the entry's lock.
    /// </summary>
    private bool IsUnmodifiedSinceRefresh(Node<K, V> current, V oldValue, object keyRef, object token, long writeTime)
        => current.IsAlive
            && ReferenceEquals(_refreshes.GetOrDefault(keyRef), token)
            && ReferenceEquals(current.Value, oldValue)
            && (current.WriteTime & ~1L) == writeTime;

    public V? GetIfPresentQuietly(K key)
    {
        Node<K, V>? node = _data.GetOrDefault(key);
        return node?.Value;
    }

    public IReadOnlyDictionary<K, V> GetAllPresent(IEnumerable<K> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        // Seed with the unique requested keys (de-duplicated), then
        // resolve each: drop absent/expired keys, and record live values with the read-path bookkeeping.
        var result = new Dictionary<K, V?>();
        foreach (K key in keys)
        {
            result[key] = null;
        }
        int uniqueKeys = result.Count;

        bool drain = false;
        long now = _ticker.Read();
        var toRemove = new List<K>();
        foreach (K key in result.Keys)
        {
            Node<K, V>? node = _data.GetOrDefault(key);
            V? value = node?.Value;
            if (value == null || HasExpired(node!, now))
            {
                // Absent, or logically expired: treat as a miss (do not leak the stale value), and
                // request a drain so an expired entry is reclaimed in the next maintenance cycle.
                if (node != null && value != null) drain = true;
                toRemove.Add(key);
                continue;
            }
            SetAccessTime(node!, now);
            TryExpireAfterRead(node!, key, value, now);
            AfterRead(node!, now);
            result[key] = value;
        }
        foreach (K key in toRemove)
        {
            result.Remove(key);
        }
        if (drain)
        {
            ScheduleDrainBuffers();
        }
        _statsCounter.RecordHits(result.Count);
        _statsCounter.RecordMisses(uniqueKeys - result.Count);

        var final = new Dictionary<K, V>(result.Count);
        foreach (KeyValuePair<K, V?> kv in result)
        {
            final[kv.Key] = kv.Value!;
        }
        return final;
    }

    /// <summary>
    /// Records a read into the (lossy) read buffer, schedules a maintenance drain per the drain-status
    /// state machine, and triggers an async refresh if the entry is overdue. A dropped read (buffer
    /// FULL) makes the drain non-delayable. Performs the
    /// refresh check on every read path (not just <see cref="GetIfPresent"/>).
    /// </summary>
    private void AfterRead(Node<K, V> node, long now)
    {
        bool delayable = !_tracksAccess || _readBuffer.Offer(node) != BufferResult.Full;
        if (ShouldDrainBuffers(delayable))
        {
            ScheduleDrainBuffers();
        }
        RefreshIfNeeded(node, now);
    }

    /// <summary>
    /// Decides whether to schedule a drain given the current drain status. When IDLE, a drain is
    /// scheduled only if it is not delayable (i.e. the read buffer just overflowed); when REQUIRED it
    /// always schedules; while already processing it never does.
    /// </summary>
    private bool ShouldDrainBuffers(bool delayable)
    {
        return Volatile.Read(ref _drainStatus) switch
        {
            Idle => !delayable,
            Required => true,
            _ => false, // ProcessingToIdle / ProcessingToRequired: a drain is already running
        };
    }

    // ----- write path -----

    private void AfterWrite(IWriteTask task)
    {
        if (_writeBuffer.Offer(task))
        {
            ScheduleAfterWrite();
            return;
        }

        // The buffer is full, so the writer performs the maintenance itself rather than dropping the
        // task. This keeps writers making progress when the scheduled drain is stalled — the executor's
        // threads are all busy (perhaps writing into this cache), the write rate outpaces the drain, or
        // the maintenance task was silently discarded.
        lock (_evictionLock)
        {
            try { Maintenance(task); }
            catch { /* maintenance must not surface to the writer */ }
        }
        RescheduleDrainIfIncomplete();
    }

    /// <summary>
    /// If a concurrent writer left the drain status at <see cref="Required"/> (e.g. it enqueued a
    /// task while an inline maintenance cycle was finishing), schedule a drain so that task is not
    /// stranded in the buffer until the next unrelated read/write.
    /// </summary>
    private void RescheduleDrainIfIncomplete()
    {
        if (Volatile.Read(ref _drainStatus) != Required)
        {
            return;
        }

        // An immediate scheduling cannot be performed on a custom executor because it may use a
        // caller-runs policy (e.g. DirectExecutor). This could cause the caller's penalty to exceed the
        // amortized threshold, e.g. repeated concurrent writes could result in a retry loop.
        if (ReferenceEquals(_executor, ThreadPoolExecutor.Instance))
        {
            ScheduleDrainBuffers();
            return;
        }

        // If a scheduler was configured then the maintenance can be deferred onto the custom executor
        // and run in the near future via the pacer; otherwise it will be handled by other cache activity.
        if (_pacer != null && !_pacer.IsScheduled && Monitor.TryEnter(_evictionLock))
        {
            try
            {
                if (Volatile.Read(ref _drainStatus) == Required && !_pacer.IsScheduled)
                {
                    _pacer.Schedule(_executor, _drainBuffersTask, _ticker.Read(), Pacer.Tolerance);
                }
            }
            finally
            {
                Monitor.Exit(_evictionLock);
            }
        }
    }

    private void ScheduleAfterWrite()
    {
        while (true)
        {
            int status = Volatile.Read(ref _drainStatus);
            switch (status)
            {
                case Idle:
                    if (Interlocked.CompareExchange(ref _drainStatus, Required, Idle) == Idle)
                    {
                        ScheduleDrainBuffers();
                    }
                    return;
                case Required:
                    ScheduleDrainBuffers();
                    return;
                case ProcessingToIdle:
                    if (Interlocked.CompareExchange(ref _drainStatus, ProcessingToRequired, ProcessingToIdle) == ProcessingToIdle)
                    {
                        return;
                    }
                    continue; // lost race, re-read
                default: // ProcessingToRequired
                    return;
            }
        }
    }

    private void ScheduleDrainBuffers()
    {
        if (Volatile.Read(ref _drainStatus) >= ProcessingToIdle)
        {
            return;
        }
        if (Monitor.TryEnter(_evictionLock))
        {
            try
            {
                int status = Volatile.Read(ref _drainStatus);
                if (status >= ProcessingToIdle)
                {
                    return;
                }
                Volatile.Write(ref _drainStatus, ProcessingToIdle);
                try
                {
                    _executor.Execute(_drainBuffersTask);
                }
                catch
                {
                    // The executor rejected the task (saturated/shutdown). Run maintenance inline so
                    // the drain status is reset by Maintenance's finally block; otherwise the state
                    // machine would stay stuck at ProcessingToIdle forever and never schedule again.
                    try { Maintenance(null); }
                    catch { /* maintenance must not surface to the caller */ }
                }
            }
            finally
            {
                Monitor.Exit(_evictionLock);
            }
        }
    }

    // ----- maintenance pump -----

    /// <summary>The single-threaded maintenance cycle. Caller holds <see cref="_evictionLock"/>.</summary>
    private void Maintenance(IWriteTask? task)
    {
        Volatile.Write(ref _drainStatus, ProcessingToIdle);
        try
        {
            try
            {
                DrainReadBuffer();
                DrainWriteBuffer();
            }
            finally
            {
                task?.Run();
            }

            ExpireEntries();        // access / write / variable expiration
            EvictEntries();         // size / weight eviction
            Climb();                // adaptive window climbing
        }
        finally
        {
            if (Volatile.Read(ref _drainStatus) != ProcessingToIdle
                || Interlocked.CompareExchange(ref _drainStatus, Idle, ProcessingToIdle) != ProcessingToIdle)
            {
                Volatile.Write(ref _drainStatus, Required);
            }
        }
    }

    private void DrainReadBuffer()
    {
        if (_tracksAccess)
        {
            _readBuffer.DrainTo(_onAccess);
        }
    }

    private void DrainWriteBuffer()
    {
        for (int i = 0; i <= WriteBufferMax; i++)
        {
            IWriteTask? task = _writeBuffer.Poll();
            if (task == null)
            {
                return;
            }
            task.Run();
        }
        Volatile.Write(ref _drainStatus, ProcessingToRequired);
    }

    private void ExpireEntries()
    {
        long now = _ticker.Read();
        ExpireAfterAccessEntries(now);
        ExpireAfterWriteEntries(now);
        ExpireVariableEntries(now);

        if (_pacer != null)
        {
            long delay = GetExpirationDelay(now);
            if (delay == long.MaxValue)
            {
                _pacer.Cancel();
            }
            else
            {
                _pacer.Schedule(_executor, _drainBuffersTask, now, delay);
            }
        }
    }

    /// <summary>
    /// Performs the maintenance work, blocking until the eviction lock is acquired, then schedules
    /// another cycle if work remains (a concurrent write during the cycle, or a per-cycle budget hit).
    /// </summary>
    private void PerformCleanUp()
    {
        lock (_evictionLock) { Maintenance(null); }
        RescheduleDrainIfIncomplete();
    }

    /// <summary>
    /// The maintenance task submitted to the executor and the pacer. It holds the cache only weakly
    /// and never lets a maintenance failure escape onto the executor's thread (on the thread pool an
    /// unhandled exception would terminate the process); <see cref="Maintenance"/> resets the drain
    /// status in its finally block, so a failed cycle does not wedge the state machine.
    /// </summary>
    private sealed class PerformCleanupTask
    {
        private readonly WeakReference<BoundedLocalCache<K, V>> _reference;

        public PerformCleanupTask(BoundedLocalCache<K, V> cache)
            => _reference = new WeakReference<BoundedLocalCache<K, V>>(cache);

        public void Run()
        {
            try
            {
                if (_reference.TryGetTarget(out BoundedLocalCache<K, V>? cache))
                {
                    cache.PerformCleanUp();
                }
            }
            catch
            {
                // Maintenance must not surface to the executor (no logger is available).
            }
        }
    }

    /// <summary>
    /// Returns the delay until the next entry expires across all expiration policies, or
    /// <see cref="long.MaxValue"/> if nothing is scheduled to expire.
    /// </summary>
    private long GetExpirationDelay(long now)
    {
        long delay = long.MaxValue;
        if (_expiresAfterAccess)
        {
            Node<K, V>? node = _windowDeque.PeekFirst;
            if (node != null)
            {
                long age = Math.Max(0, now - node.AccessTime);
                delay = Math.Min(delay, _expiresAfterAccessNanos - age);
            }
            if (_evicts)
            {
                node = _probationDeque.PeekFirst;
                if (node != null)
                {
                    long age = Math.Max(0, now - node.AccessTime);
                    delay = Math.Min(delay, _expiresAfterAccessNanos - age);
                }
                node = _protectedDeque.PeekFirst;
                if (node != null)
                {
                    long age = Math.Max(0, now - node.AccessTime);
                    delay = Math.Min(delay, _expiresAfterAccessNanos - age);
                }
            }
        }
        if (_expiresAfterWrite)
        {
            Node<K, V>? node = _writeOrderDeque.PeekFirst;
            if (node != null)
            {
                long age = Math.Max(0, now - node.WriteTime);
                delay = Math.Min(delay, _expiresAfterWriteNanos - age);
            }
        }
        if (_expiresVariable)
        {
            delay = Math.Min(delay, _timerWheel!.GetExpirationDelay());
        }
        return delay;
    }

    /// <summary>
    /// Advances the timer wheel, expiring entries whose variable duration has elapsed. Capped per
    /// cycle; a full budget re-arms the drain so the backlog is processed across cycles.
    /// </summary>
    private void ExpireVariableEntries(long now)
    {
        if (_expiresVariable && _timerWheel!.Advance(this, now, ExpirationThreshold) == 0)
        {
            Volatile.Write(ref _drainStatus, ProcessingToRequired);
        }
    }

    private void ExpireAfterAccessEntries(long now)
    {
        if (!_expiresAfterAccess)
        {
            return;
        }
        int remaining = ExpirationThreshold;
        remaining = ExpireAfterAccessEntries(_windowDeque, Node<K, V>.Window, now, remaining);
        if (_evicts)
        {
            remaining = ExpireAfterAccessEntries(_probationDeque, Node<K, V>.Probation, now, remaining);
            remaining = ExpireAfterAccessEntries(_protectedDeque, Node<K, V>.Protected, now, remaining);
        }
        if (remaining == 0)
        {
            Volatile.Write(ref _drainStatus, ProcessingToRequired); // re-arm to drain the backlog
        }
    }

    private int ExpireAfterAccessEntries(AccessOrderDeque<Node<K, V>> deque, int queueType, long now, int remaining)
    {
        Node<K, V>? head = deque.PeekFirst;
        if (head == null)
        {
            return remaining;
        }
        long duration = _expiresAfterAccessNanos;
        Node<K, V> last = deque.PeekLast!;
        for (Node<K, V>? node = head; node != null && remaining > 0;)
        {
            Node<K, V>? next = ReferenceEquals(node, last) ? null : node.GetNextInAccessOrder();
            if ((now - node.AccessTime) < duration)
            {
                // A stale position can arise from lock-free access-time updates; re-sort and continue.
                bool stalePosition = (last.AccessTime - node.AccessTime) < 0;
                if (stalePosition || IsComputingAsync(node))
                {
                    deque.MoveToBack(node);
                    node = next;
                    continue;
                }
                return remaining;
            }
            EvictEntry(node, RemovalCause.Expired, now);
            remaining--;

            // Inline callbacks can unlink successors or transfer the captured tail to another queue.
            node = ReferenceEquals(node, last) ? null : deque.PeekFirst;
            bool bounded = last.QueueType == queueType && deque.Contains(last);
            if (node != null && !bounded)
            {
                return 0;
            }
        }
        return remaining;
    }

    private void ExpireAfterWriteEntries(long now)
    {
        if (!_expiresAfterWrite)
        {
            return;
        }
        Node<K, V>? head = _writeOrderDeque.PeekFirst;
        if (head == null)
        {
            return;
        }
        long duration = _expiresAfterWriteNanos;
        int remaining = ExpirationThreshold;
        Node<K, V> last = _writeOrderDeque.PeekLast!;
        for (Node<K, V>? node = head; node != null && remaining > 0;)
        {
            Node<K, V>? next = ReferenceEquals(node, last) ? null : node.GetNextInWriteOrder();
            if ((now - node.WriteTime) < duration)
            {
                bool stalePosition = (last.WriteTime - node.WriteTime) < 0;
                if (stalePosition || IsComputingAsync(node))
                {
                    _writeOrderDeque.MoveToBack(node);
                    node = next;
                    continue;
                }
                return;
            }
            EvictEntry(node, RemovalCause.Expired, now);
            remaining--;

            // Resume from live links and stop if reentrant maintenance removed the scan's bound.
            node = ReferenceEquals(node, last) ? null : _writeOrderDeque.PeekFirst;
            if (node != null && !_writeOrderDeque.Contains(last))
            {
                remaining = 0;
                break;
            }
        }
        if (remaining == 0)
        {
            Volatile.Write(ref _drainStatus, ProcessingToRequired);
        }
    }

    /// <summary>
    /// Adapts the eviction policy toward the optimal recency/frequency configuration by resizing the
    /// admission window (the hill-climbing step of the maintenance cycle).
    /// </summary>
    private void Climb()
    {
        if (!_evicts)
        {
            return;
        }
        DetermineAdjustment();
        DemoteFromMainProtected();
        long amount = _adjustment;
        if (amount == 0)
        {
            return;
        }
        if (amount > 0)
        {
            IncreaseWindow();
        }
        else
        {
            DecreaseWindow();
        }
    }

    /// <summary>Calculates how much to adapt the window by and stores it in <see cref="_adjustment"/>.</summary>
    private void DetermineAdjustment()
    {
        if (_sketch.IsNotInitialized)
        {
            _previousSampleHitRate = 0.0;
            _missesInSample = 0;
            _hitsInSample = 0;
            return;
        }

        long requestCount = _hitsInSample + _missesInSample;
        double stepDecayRate = HillClimberStepDecayRate;
        long effectiveSampleSize = _sketch.sampleSize;
        if (_maximum <= SmallCacheThreshold)
        {
            // Grow the sample period as the step size decays to avoid converging near the initial ratio.
            double initialStep = HillClimberStepPercent * _maximum;
            double magnitude = Math.Max(initialStep / SmallCacheSampleRatioCap, Math.Abs(_stepSize));
            double ratio = (magnitude == 0.0)
                ? 1.0
                : Math.Max(1.0, Math.Min(SmallCacheSampleRatioCap, initialStep / magnitude));
            effectiveSampleSize = (long)(effectiveSampleSize * ratio);
            stepDecayRate = SmallCacheStepDecayRate;
        }
        if (requestCount < effectiveSampleSize)
        {
            return;
        }

        double hitRate = (double)_hitsInSample / requestCount;
        double hitRateChange = hitRate - _previousSampleHitRate;
        double amount = (hitRateChange >= 0) ? _stepSize : -_stepSize;
        double nextStepSize = (Math.Abs(hitRateChange) >= HillClimberRestartThreshold)
            ? Math.CopySign(Math.Max(HillClimberStepPercent * _maximum, HillClimberMinInitialStep), amount)
            : (stepDecayRate * amount);
        _previousSampleHitRate = hitRate;
        _adjustment = (long)amount;
        _stepSize = nextStepSize;
        _missesInSample = 0;
        _hitsInSample = 0;
    }

    /// <summary>
    /// Increases the admission window by shrinking the main space's protected region, transferring
    /// nodes from probation/protected into the window (up to the per-cycle transfer cap), and carries
    /// any unfulfilled remainder over to the next cycle via <see cref="_adjustment"/>.
    /// </summary>
    private void IncreaseWindow()
    {
        if (_mainProtectedMaximum == 0)
        {
            return;
        }

        long quota = Math.Min(_adjustment, _mainProtectedMaximum);
        _mainProtectedMaximum -= quota;
        _windowMaximum += quota;
        DemoteFromMainProtected();

        for (int i = 0; i < QueueTransferThreshold; i++)
        {
            Node<K, V>? candidate = _probationDeque.PeekFirst;
            bool probation = true;
            if (candidate == null || quota < candidate.PolicyWeight)
            {
                candidate = _protectedDeque.PeekFirst;
                probation = false;
            }
            if (candidate == null)
            {
                break;
            }

            long weight = candidate.PolicyWeight;
            if (quota < weight)
            {
                break;
            }

            quota -= weight;
            if (probation)
            {
                _probationDeque.Remove(candidate);
            }
            else
            {
                _mainProtectedWeightedSize -= weight;
                _protectedDeque.Remove(candidate);
            }
            _windowWeightedSize += weight;
            _windowDeque.OfferLast(candidate);
            candidate.MakeWindow();
        }

        _mainProtectedMaximum += quota;
        _windowMaximum -= quota;
        _adjustment = quota;
    }

    /// <summary>Decreases the admission window and grows the main space's protected region.</summary>
    private void DecreaseWindow()
    {
        if (_windowMaximum <= 1)
        {
            return;
        }

        long quota = Math.Min(-_adjustment, Math.Max(0, _windowMaximum - 1));
        _mainProtectedMaximum += quota;
        _windowMaximum -= quota;

        for (int i = 0; i < QueueTransferThreshold; i++)
        {
            Node<K, V>? candidate = _windowDeque.PeekFirst;
            if (candidate == null)
            {
                break;
            }

            long weight = candidate.PolicyWeight;
            if (quota < weight)
            {
                break;
            }

            quota -= weight;
            _windowWeightedSize -= weight;
            _windowDeque.Remove(candidate);
            _probationDeque.OfferLast(candidate);
            candidate.MakeMainProbation();
        }

        _mainProtectedMaximum -= quota;
        _windowMaximum += quota;
        _adjustment = -quota;
    }

    /// <summary>Transfers nodes from the protected to the probation region when it exceeds its maximum.</summary>
    private void DemoteFromMainProtected()
    {
        long mainProtectedMaximum = _mainProtectedMaximum;
        long mainProtectedWeightedSize = _mainProtectedWeightedSize;
        if (mainProtectedWeightedSize <= mainProtectedMaximum)
        {
            return;
        }

        for (int i = 0; i < QueueTransferThreshold; i++)
        {
            if (mainProtectedWeightedSize <= mainProtectedMaximum)
            {
                break;
            }

            Node<K, V>? demoted = _protectedDeque.PollFirst();
            if (demoted == null)
            {
                break;
            }
            demoted.MakeMainProbation();
            _probationDeque.OfferLast(demoted);
            mainProtectedWeightedSize -= demoted.PolicyWeight;
        }
        _mainProtectedWeightedSize = mainProtectedWeightedSize;
    }

    /// <summary>
    /// Returns whether the node has expired by access, write, or variable time. An in-flight async
    /// future never expires while it is still loading.
    /// </summary>
    private bool HasExpired(Node<K, V> node, long now)
    {
        if (IsComputingAsync(node))
        {
            return false; // an in-flight future never expires while it is still loading
        }
        return (_expiresAfterAccess && (now - node.AccessTime >= _expiresAfterAccessNanos))
            || (_expiresAfterWrite && (now - node.WriteTime >= _expiresAfterWriteNanos))
            || (_expiresVariable && (now - node.VariableTime >= 0));
    }

    /// <summary>Returns whether the node holds an async future that has not yet completed successfully.</summary>
    private bool IsComputingAsync(Node<K, V> node)
        => IsComputingAsync(node.Value);

    /// <summary>Returns whether the value is an async future that has not yet completed successfully.</summary>
    private bool IsComputingAsync(V? value)
        => _isAsync && value is Task task && !AsyncValue.IsReady(task);

    /// <summary>
    /// For an async cache, pushes a freshly-inserted in-flight future's expiry timestamps ~220 years
    /// out (the async sentinel) so it cannot expire while loading. The real timers are restored by
    /// <see cref="Replace"/> when the future completes. This keeps the design uniform for the future
    /// variable-expiration (timer-wheel) path, where the sentinel is the sole in-flight signal.
    /// </summary>
    private void StampAsyncExpiryIfComputing(Node<K, V> node, long now)
    {
        if (_isAsync && (_expiresAfterWrite || _refreshAfterWrite || _expiresAfterAccess)
            && IsComputingAsync(node))
        {
            long sentinel = now + AsyncExpiry;
            if (_expiresAfterWrite || _refreshAfterWrite) node.WriteTime = sentinel;
            if (_expiresAfterAccess) node.AccessTime = sentinel;
        }
    }

    /// <summary>Updates a node's access time on the read path, subject to the reorder tolerance.</summary>
    private void SetAccessTime(Node<K, V> node, long now)
    {
        if (!_expiresAfterAccess)
        {
            return;
        }
        long accessTime = node.AccessTime;
        // Skip updates within the tolerance window to avoid cache-line contention on hot entries.
        if (_expiresAfterAccessNanos <= ExpireTolerance || Math.Abs(now - accessTime) > ExpireTolerance)
        {
            node.AccessTime = now;
        }
    }

    /// <summary>Returns the variable expiration time for a newly created entry (0 if not configured).</summary>
    private long ExpireAfterCreate(K key, V value, long now)
    {
        if (_expiresVariable)
        {
            long duration = Math.Max(0L, _expiry!.ExpireAfterCreate(key, value, now));
            return _isAsync ? (now + duration) : (now + Math.Min(duration, MaximumExpiry));
        }
        return 0L;
    }

    /// <summary>Returns the variable expiration time for an updated entry (0 if not configured).</summary>
    private long ExpireAfterUpdate(Node<K, V> node, K key, V value, long now)
    {
        if (_expiresVariable)
        {
            long currentDuration = Math.Max(1, node.VariableTime - now);
            long duration = Math.Max(0L, _expiry!.ExpireAfterUpdate(key, value, now, currentDuration));
            return _isAsync ? (now + duration) : (now + Math.Min(duration, MaximumExpiry));
        }
        return 0L;
    }

    /// <summary>Returns the variable expiration time for a read entry (0 if not configured).</summary>
    private long ExpireAfterRead(Node<K, V> node, K key, V value, long now)
    {
        if (_expiresVariable)
        {
            long currentDuration = Math.Max(0L, node.VariableTime - now);
            long duration = Math.Max(0L, _expiry!.ExpireAfterRead(key, value, now, currentDuration));
            return _isAsync ? (now + duration) : (now + Math.Min(duration, MaximumExpiry));
        }
        return 0L;
    }

    /// <summary>
    /// Attempts to extend the entry's variable expiration on the read path via a lock-free CAS, subject
    /// to the reorder tolerance and a value-identity guard (so a read duration is never rebound onto a
    /// replaced value).
    /// </summary>
    private void TryExpireAfterRead(Node<K, V> node, K key, V value, long now)
    {
        if (!_expiresVariable)
        {
            return;
        }
        long variableTime = node.VariableTime;
        long currentDuration = Math.Max(1, variableTime - now);
        if (_isAsync && currentDuration > MaximumExpiry)
        {
            // ExpireAfterCreate has not yet stamped the real duration after completion.
            return;
        }
        long duration = Math.Max(0L, _expiry!.ExpireAfterRead(key, value, now, currentDuration));
        long expirationTime = _isAsync ? (now + duration) : (now + Math.Min(duration, MaximumExpiry));
        if ((duration <= ExpireTolerance || Math.Abs(expirationTime - variableTime) > ExpireTolerance)
            && ReferenceEquals(node.Value, value))
        {
            node.CasVariableTime(variableTime, expirationTime);
        }
    }

    /// <summary>Stamps a node's variable-expiration time when variable expiry is configured.</summary>
    private void SetVariableTime(Node<K, V> node, long expirationTime)
    {
        if (_expiresVariable)
        {
            node.VariableTime = expirationTime;
        }
    }

    /// <summary>
    /// Returns whether a write would move the entry far enough in its write/refresh ordering to be
    /// worth reordering the policy deque (i.e. exceeds <see cref="ExpireTolerance"/>).
    /// </summary>
    private bool ExceedsWriteTimeTolerance(Node<K, V> node, long now)
    {
        long writeTime = node.WriteTime;
        return (_expiresAfterWrite
                && (_expiresAfterWriteNanos <= ExpireTolerance || Math.Abs(now - writeTime) > ExpireTolerance))
            || (_refreshAfterWrite
                && (_refreshAfterWriteNanos <= ExpireTolerance || Math.Abs(now - writeTime) > ExpireTolerance));
    }

    // ----- access policy -----

    private void OnAccess(Node<K, V> node)
    {
        if (Evicts)
        {
            if (!node.IsAlive)
            {
                return;
            }
            object keyRef = node.KeyReference;
            _sketch.Increment(keyRef);
            if (node.InWindow)
            {
                Reorder(_windowDeque, node);
            }
            else if (node.InMainProbation)
            {
                ReorderProbation(node);
            }
            else
            {
                Reorder(_protectedDeque, node);
            }
            _hitsInSample++;
        }
        else if (ExpiresAfterAccess)
        {
            Reorder(_windowDeque, node);
        }
        if (ExpiresVariable)
        {
            _timerWheel!.Reschedule(node);
        }
    }

    private void ReorderProbation(Node<K, V> node)
    {
        if (!_probationDeque.Contains(node))
        {
            return; // stale access for a no-longer-present entry
        }
        if (node.PolicyWeight > _mainProtectedMaximum)
        {
            Reorder(_probationDeque, node);
            return;
        }
        _mainProtectedWeightedSize += node.PolicyWeight;
        _probationDeque.Remove(node);
        _protectedDeque.OfferLast(node);
        node.MakeMainProtected();
    }

    // Generic over the concrete deque type so the JIT specializes each call site instead of dispatching
    // through ILinkedDeque on every buffered read during a maintenance drain.
    private static void Reorder<TDeque>(TDeque deque, Node<K, V> node)
        where TDeque : ILinkedDeque<Node<K, V>>
    {
        if (deque.Contains(node))
        {
            deque.MoveToBack(node);
        }
    }

    // ----- eviction -----

    private void EvictEntries()
    {
        if (!Evicts)
        {
            return;
        }
        Node<K, V>? candidate = EvictFromWindow();
        EvictFromMain(candidate);
    }

    private Node<K, V>? EvictFromWindow()
    {
        Node<K, V>? first = null;
        Node<K, V>? node = _windowDeque.PeekFirst;
        while (_windowWeightedSize > _windowMaximum)
        {
            if (node == null)
            {
                break;
            }
            Node<K, V>? next = node.GetNextInAccessOrder();
            if (node.PolicyWeight != 0)
            {
                node.MakeMainProbation();
                _windowDeque.Remove(node);
                _probationDeque.OfferLast(node);
                first ??= node;
                _windowWeightedSize -= node.PolicyWeight;
            }
            node = next;
        }
        return first;
    }

    private void EvictFromMain(Node<K, V>? candidate)
    {
        int victimQueue = Node<K, V>.Probation;
        int candidateQueue = Node<K, V>.Probation;
        Node<K, V>? victim = _probationDeque.PeekFirst;
        while (_weightedSize > _maximum)
        {
            if (candidate == null && candidateQueue == Node<K, V>.Probation)
            {
                candidate = _windowDeque.PeekFirst;
                candidateQueue = Node<K, V>.Window;
            }

            if (candidate == null && victim == null)
            {
                if (victimQueue == Node<K, V>.Probation)
                {
                    victim = _protectedDeque.PeekFirst;
                    victimQueue = Node<K, V>.Protected;
                    continue;
                }
                if (victimQueue == Node<K, V>.Protected)
                {
                    victim = _windowDeque.PeekFirst;
                    victimQueue = Node<K, V>.Window;
                    continue;
                }
                break;
            }

            // Skip zero-weight (pinned) entries.
            if (victim != null && victim.PolicyWeight == 0)
            {
                victim = victim.GetNextInAccessOrder();
                continue;
            }
            if (candidate != null && candidate.PolicyWeight == 0)
            {
                candidate = candidate.GetNextInAccessOrder();
                continue;
            }

            // Only one side present.
            if (victim == null)
            {
                Node<K, V> evict = candidate!;
                candidate = candidate!.GetNextInAccessOrder();
                EvictEntry(evict, RemovalCause.Size);
                continue;
            }
            if (candidate == null)
            {
                Node<K, V> evict = victim;
                victim = victim.GetNextInAccessOrder();
                EvictEntry(evict, RemovalCause.Size);
                continue;
            }

            // Both selected the same node.
            if (ReferenceEquals(candidate, victim))
            {
                victim = victim.GetNextInAccessOrder();
                EvictEntry(candidate, RemovalCause.Size);
                candidate = null;
                continue;
            }

            // Dead entries evict immediately.
            if (!victim.IsAlive)
            {
                Node<K, V> evict = victim;
                victim = victim.GetNextInAccessOrder();
                EvictEntry(evict, RemovalCause.Size);
                continue;
            }
            if (!candidate.IsAlive)
            {
                Node<K, V> evict = candidate;
                candidate = candidate.GetNextInAccessOrder();
                EvictEntry(evict, RemovalCause.Size);
                continue;
            }

            // Oversized candidate evicts immediately.
            if (candidate.PolicyWeight > _maximum)
            {
                Node<K, V> evict = candidate;
                candidate = candidate.GetNextInAccessOrder();
                EvictEntry(evict, RemovalCause.Size);
                continue;
            }

            // Evict the lower-frequency entry.
            if (Admit(candidate.KeyReference, victim.KeyReference))
            {
                Node<K, V> evict = victim;
                victim = victim.GetNextInAccessOrder();
                EvictEntry(evict, RemovalCause.Size);
                candidate = candidate.GetNextInAccessOrder();
            }
            else
            {
                Node<K, V> evict = candidate;
                candidate = candidate.GetNextInAccessOrder();
                EvictEntry(evict, RemovalCause.Size);
            }
        }
    }

    private bool Admit(object candidateKeyRef, object victimKeyRef)
    {
        int candidateFreq = _sketch.Frequency(candidateKeyRef);
        int victimFreq = _sketch.Frequency(victimKeyRef);
        if (candidateFreq > victimFreq)
        {
            return true;
        }
        if (candidateFreq >= AdmitHashDosThreshold)
        {
            // Random tie-break to resist hash-collision attacks on the frequency filter.
            int random = ThreadLocalRandomNext();
            return (random & 127) == 0;
        }
        return false;
    }

    [ThreadStatic] private static Random? _rng;
    private static int ThreadLocalRandomNext() => (_rng ??= new Random()).Next();

    /// <summary>Removes a victim from the map and policy. Caller holds the eviction lock.</summary>
    public bool EvictEntry(Node<K, V> node, RemovalCause cause, long now = 0L)
    {
        // Capture the key reference before the remap can retire the node (retiring swaps it for a
        // sentinel). A node already retired by a concurrent removal is no longer in the map under its
        // key, so the remap is skipped and only the policy unlink below runs.
        object keyRef = node.KeyReference;
        bool hasKey = !ReferenceEquals(keyRef, Node<K, V>.RetiredSentinel)
            && !ReferenceEquals(keyRef, Node<K, V>.DeadSentinel);
        K? key = hasKey ? (K)keyRef : default;

        // State-passing remap so the eviction path allocates no closure (it fires once per insert past
        // capacity and dominated the write-path allocation profile).
        var s = new EvictState { Cache = this, Node = node, Cause = cause, Now = now, ActualCause = cause };
        if (hasKey)
        {
            RemapForEviction(key!, ref s);
        }

        V? value = s.Value;
        bool removed = s.Removed;
        RemovalCause actualCause = s.ActualCause;

        if (s.Resurrect)
        {
            return false;
        }

        // Unlink from the policy deques (eagerly, before finalizing).
        if (node.InWindow && (Evicts || ExpiresAfterAccess))
        {
            _windowDeque.Remove(node);
        }
        else if (Evicts)
        {
            if (node.InMainProbation)
            {
                _probationDeque.Remove(node);
            }
            else
            {
                _protectedDeque.Remove(node);
            }
        }
        if (ExpiresAfterWrite)
        {
            _writeOrderDeque.Remove(node);
        }
        else if (ExpiresVariable)
        {
            _timerWheel!.Deschedule(node);
        }

        lock (node)
        {
            MakeDead(node);
        }

        if (removed)
        {
            DiscardRefresh(keyRef); // cancel any in-flight reload for the evicted key
            _statsCounter.RecordEviction(node.Weight, actualCause);
            NotifyRemoval(key, value, actualCause);
        }
        return true;
    }

    private void RemapForEviction(K key, ref EvictState s)
    {
        _data.ComputeIfPresent(key, static (Node<K, V> current, ref EvictState st) =>
        {
            if (!ReferenceEquals(current, st.Node))
            {
                return current; // a different node now occupies the key; leave it
            }
            var n = st.Node;
            lock (n)
            {
                st.Value = n.Value;
                st.ActualCause = (n.Key == null || st.Value == null) ? RemovalCause.Collected : st.Cause;

                if (st.ActualCause == RemovalCause.Expired)
                {
                    // Re-verify: the entry may have been touched since it was queued for expiration.
                    if (!st.Cache.HasExpired(n, st.Now))
                    {
                        st.Resurrect = true;
                        return n;
                    }
                }
                else if (st.ActualCause == RemovalCause.Size)
                {
                    if (n.Weight == 0)
                    {
                        st.Resurrect = true;
                        return n; // pinned entry, keep it
                    }
                }

                st.Removed = true;
                n.Retire();
                return null; // remove from the map
            }
        }, ref s);
    }

    // State threaded through the eviction remap: the caller sets the inputs, the remap writes outputs back.
    private struct EvictState
    {
        public BoundedLocalCache<K, V> Cache;
        public Node<K, V> Node;
        public RemovalCause Cause;
        public long Now;
        public V? Value;
        public bool Removed;
        public bool Resurrect;
        public RemovalCause ActualCause;
    }

    private void MakeDead(Node<K, V> node)
    {
        if (node.IsDead)
        {
            return;
        }
        if (Evicts)
        {
            // Node weight is finalized here; adjust the region sizes using the entry's own weight.
            if (node.InWindow)
            {
                _windowWeightedSize -= node.Weight;
            }
            else if (node.InMainProtected)
            {
                _mainProtectedWeightedSize -= node.Weight;
            }
            _weightedSize -= node.Weight;
        }
        node.Die();
    }

    // ----- write tasks -----

    private interface IWriteTask
    {
        void Run();
    }

    /// <summary>Adds a newly inserted node to the eviction policy. Runs under the eviction lock.</summary>
    private sealed class AddTask : IWriteTask
    {
        private readonly BoundedLocalCache<K, V> _cache;
        private readonly Node<K, V> _node;
        private readonly int _weight;

        public AddTask(BoundedLocalCache<K, V> cache, Node<K, V> node, int weight)
        {
            _cache = cache;
            _node = node;
            _weight = weight;
        }

        public void Run()
        {
            var c = _cache;
            if (c.Evicts)
            {
                c._weightedSize += _weight;
                c._windowWeightedSize += _weight;
                _node.PolicyWeight += _weight;

                long maximum = c._maximum;
                if (c._weightedSize >= (maximum >>> 1))
                {
                    if (c._weightedSize > MaximumCapacity)
                    {
                        c.EvictEntries();
                    }
                    else
                    {
                        long capacity = c._isWeighted ? c._data.Count : maximum;
                        c._sketch.EnsureCapacity(capacity);
                    }
                }
                c._missesInSample++;
            }

            bool isAlive;
            lock (_node) { isAlive = _node.IsAlive; }
            if (!isAlive)
            {
                return;
            }

            if (c.ExpiresAfterWrite)
            {
                c._writeOrderDeque.OfferLast(_node);
            }
            if (c.ExpiresVariable)
            {
                c._timerWheel!.Schedule(_node);
            }
            if (c.Evicts)
            {
                if (_node.IsAlive)
                {
                    c._sketch.Increment(_node.KeyReference);
                }
                // Link first so that a declined eviction (e.g. the node's weight dropped to zero
                // before this task ran) leaves the live entry tracked by the policy.
                if (_weight > c._windowMaximum)
                {
                    c._windowDeque.OfferFirst(_node);
                }
                else
                {
                    c._windowDeque.OfferLast(_node);
                }
                if (_weight > c._maximum)
                {
                    c.EvictEntry(_node, RemovalCause.Size);
                }
            }
            else if (c.ExpiresAfterAccess)
            {
                c._windowDeque.OfferLast(_node);
            }
        }
    }

    /// <summary>Removes a node from the eviction policy. Runs under the eviction lock.</summary>
    private sealed class RemovalTask : IWriteTask
    {
        private readonly BoundedLocalCache<K, V> _cache;
        private readonly Node<K, V> _node;

        public RemovalTask(BoundedLocalCache<K, V> cache, Node<K, V> node)
        {
            _cache = cache;
            _node = node;
        }

        public void Run()
        {
            var c = _cache;
            if (_node.InWindow && (c.Evicts || c.ExpiresAfterAccess))
            {
                c._windowDeque.Remove(_node);
            }
            else if (c.Evicts)
            {
                if (_node.InMainProbation)
                {
                    c._probationDeque.Remove(_node);
                }
                else
                {
                    c._protectedDeque.Remove(_node);
                }
            }
            if (c.ExpiresAfterWrite)
            {
                c._writeOrderDeque.Remove(_node);
            }
            else if (c.ExpiresVariable)
            {
                c._timerWheel!.Deschedule(_node);
            }
            lock (_node) { c.MakeDead(_node); }
        }
    }

    /// <summary>Applies a weight change from an update. Runs under the eviction lock.</summary>
    private sealed class UpdateTask : IWriteTask
    {
        private readonly BoundedLocalCache<K, V> _cache;
        private readonly Node<K, V> _node;
        private readonly int _weightDifference;

        public UpdateTask(BoundedLocalCache<K, V> cache, Node<K, V> node, int weightDifference)
        {
            _cache = cache;
            _node = node;
            _weightDifference = weightDifference;
        }

        // Deliberately no dead-guard: pairs with MakeDead's telescoping-sum subtraction.
        public void Run()
        {
            var c = _cache;
            if (c.ExpiresAfterWrite)
            {
                Reorder(c._writeOrderDeque, _node);
            }
            else if (c.ExpiresVariable)
            {
                c._timerWheel!.Reschedule(_node);
            }
            if (!c.Evicts)
            {
                if (c.ExpiresAfterAccess) { c.OnAccess(_node); }
                return;
            }

            _node.PolicyWeight += _weightDifference;
            if (_node.InWindow)
            {
                c._windowWeightedSize += _weightDifference;
                if (_node.PolicyWeight > c._maximum)
                {
                    c.EvictEntry(_node, RemovalCause.Size);
                }
                else if (_node.PolicyWeight <= c._windowMaximum)
                {
                    c.OnAccess(_node);
                }
                else if (c._windowDeque.Contains(_node))
                {
                    c._windowDeque.MoveToFront(_node);
                }
            }
            else if (_node.InMainProbation)
            {
                if (_node.PolicyWeight <= c._maximum)
                {
                    c.OnAccess(_node);
                }
                else
                {
                    c.EvictEntry(_node, RemovalCause.Size);
                }
            }
            else
            {
                c._mainProtectedWeightedSize += _weightDifference;
                if (_node.PolicyWeight <= c._maximum)
                {
                    c.OnAccess(_node);
                }
                else
                {
                    c.EvictEntry(_node, RemovalCause.Size);
                }
            }

            c._weightedSize += _weightDifference;
            if (c._weightedSize > MaximumCapacity)
            {
                c.EvictEntries();
            }
        }
    }

    // ----- mutations (public/internal write surface) -----

    public V? Put(K key, V value) => PutInternal(key, value, onlyIfAbsent: false);

    public V? PutIfAbsent(K key, V value) => PutInternal(key, value, onlyIfAbsent: true);

    private V? PutInternal(K key, V value, bool onlyIfAbsent)
    {
        ThrowIfKeyNull(key);
        ArgumentNullException.ThrowIfNull(value);

        int newWeight = -1;
        Node<K, V>? node = null;

        // A get / putIfAbsent + lock(node) retry loop, so the
        // mutation runs inline with local variables — no per-write closure allocation. The node is
        // built lazily and only when the bin is empty, so an overwrite never allocates one.
        for (int attempts = 1; ; attempts++)
        {
            Node<K, V>? prior = _data.GetOrDefault(key);
            if (prior == null)
            {
                if (node == null)
                {
                    if (newWeight < 0)
                    {
                        newWeight = WeighEntry(key, value);
                    }
                    long createNow = _ticker.Read();
                    node = NodeFactory.Create(_nodeFeatures, key, value, newWeight, createNow);
                    SetVariableTime(node, ExpireAfterCreate(key, value, createNow));
                    StampAsyncExpiryIfComputing(node, createNow);
                }
                Node<K, V> newNode = node;
                prior = _data.PutIfAbsent(key, newNode);
                if (prior == null || ReferenceEquals(prior, node))
                {
                    AfterWrite(new AddTask(this, node, newWeight));
                    return null;
                }
                if (onlyIfAbsent)
                {
                    // A live existing value short-circuits without locking.
                    V? currentValue = prior.Value;
                    long readNow = _ticker.Read();
                    if (currentValue != null && !HasExpired(prior, readNow))
                    {
                        if (!IsComputingAsync(prior))
                        {
                            TryExpireAfterRead(prior, key, currentValue, readNow);
                            SetAccessTime(prior, readNow);
                        }
                        AfterRead(prior, readNow);
                        return currentValue;
                    }
                }
            }
            else if (onlyIfAbsent)
            {
                V? currentValue = prior.Value;
                long readNow = _ticker.Read();
                if (currentValue != null && !HasExpired(prior, readNow))
                {
                    if (!IsComputingAsync(prior))
                    {
                        TryExpireAfterRead(prior, key, currentValue, readNow);
                        SetAccessTime(prior, readNow);
                    }
                    AfterRead(prior, readNow);
                    return currentValue;
                }
            }

            // The entry may have been removed between the read and the lock; spin briefly, then fall
            // back to a map computation to wait out an in-progress removal.
            if (!prior.IsAlive)
            {
                if ((attempts & MaxPutSpinWaitAttempts) != 0)
                {
                    Thread.SpinWait(1);
                    continue;
                }
                _data.ComputeIfPresent(key, static (_, n) => n);
                continue;
            }

            if (newWeight < 0)
            {
                newWeight = WeighEntry(key, value);
            }

            V? oldValue;
            long varTime;
            int oldWeight;
            long now;
            bool expired = false;
            bool mayUpdate = true;
            bool exceedsTolerance = false;
            lock (prior)
            {
                if (!prior.IsAlive)
                {
                    continue;
                }
                oldValue = prior.Value;
                oldWeight = prior.Weight;
                now = _ticker.Read();
                if (oldValue == null || HasExpired(prior, now))
                {
                    // The entry is expired (or being collected): treat this put as a re-creation.
                    expired = oldValue != null;
                    varTime = ExpireAfterCreate(key, value, now);
                }
                else if (onlyIfAbsent)
                {
                    mayUpdate = false;
                    varTime = ExpireAfterRead(prior, key, oldValue, now);
                }
                else
                {
                    varTime = ExpireAfterUpdate(prior, key, value, now);
                }

                long oldVarTime = prior.VariableTime;
                if (mayUpdate)
                {
                    bool writeExceeds = ExceedsWriteTimeTolerance(prior, now);
                    bool varExceeds = _expiresVariable && Math.Abs(varTime - oldVarTime) > ExpireTolerance;
                    exceedsTolerance = writeExceeds || varExceeds;
                    if (expired || exceedsTolerance)
                    {
                        if (_expiresAfterWrite || _refreshAfterWrite) prior.WriteTime = now;
                    }
                    prior.Value = value;
                    prior.Weight = newWeight;
                    DiscardRefresh(prior.KeyReference);
                }

                SetVariableTime(prior, varTime);
                if (_expiresAfterAccess) prior.AccessTime = now;
            }

            if (expired)
            {
                _statsCounter.RecordEviction(oldWeight, RemovalCause.Expired);
                NotifyRemoval(key, oldValue, RemovalCause.Expired);
            }
            else if (mayUpdate && !ReferenceEquals(oldValue, value))
            {
                NotifyRemoval(key, oldValue, RemovalCause.Replaced);
            }

            int weightedDifference = mayUpdate ? (newWeight - oldWeight) : 0;
            if (weightedDifference != 0 || expired)
            {
                AfterWrite(new UpdateTask(this, prior, weightedDifference));
            }
            else if (!onlyIfAbsent && exceedsTolerance)
            {
                AfterWrite(new UpdateTask(this, prior, weightedDifference));
            }
            else
            {
                AfterRead(prior, now);
            }

            return expired ? null : oldValue;
        }
    }

    public V? ComputeIfAbsent(K key, Func<K, V?> mappingFunction, bool recordStats, bool recordLoad = true)
    {
        ThrowIfKeyNull(key);
        ArgumentNullException.ThrowIfNull(mappingFunction);

        Node<K, V>? existing = _data.GetOrDefault(key);
        long readNow = _ticker.Read();
        if (existing != null)
        {
            // Read the value once: a concurrent removal may kill the node (clearing its value) at any
            // point, and a hit must return the value it established rather than a later re-read.
            V? hitValue = existing.Value;
            if (hitValue != null && !HasExpired(existing, readNow))
            {
                if (recordStats) _statsCounter.RecordHits(1);
                if (!IsComputingAsync(hitValue))
                {
                    SetAccessTime(existing, readNow);
                    TryExpireAfterRead(existing, key, hitValue, readNow);
                }
                AfterRead(existing, readNow);
                return hitValue;
            }
        }

        return DoComputeIfAbsent(key, mappingFunction, recordStats, recordLoad, readNow);
    }

    /// <summary>
    /// The miss path of <see cref="ComputeIfAbsent"/> (Caffeine's <c>doComputeIfAbsent</c>). An absent
    /// key is loaded under the bin lock. A present node is re-validated under its own monitor (which
    /// optimized puts use), and if it has expired it is recomputed while that monitor is held: a
    /// replacement is weighed and timed before the node is mutated, while a null or throwing
    /// recomputation retires the node and enqueues its removal. The expired value is reported as an
    /// eviction either way.
    /// </summary>
    private V? DoComputeIfAbsent(K key, Func<K, V?> mappingFunction, bool recordStats, bool recordLoad, long now)
    {
        V? oldValue = null;
        V? newValue = null;
        int oldWeight = 0;
        int newWeight = 0;
        RemovalCause? cause = null;
        Node<K, V>? removed = null;
        ExceptionDispatchInfo? failure = null;

        Node<K, V>? node = _data.Compute(key, (k, n) =>
        {
            if (n == null)
            {
                newValue = recordStats ? LoadWithStats(key, mappingFunction, recordLoad) : mappingFunction(key);
                if (newValue == null)
                {
                    DiscardRefreshForKey(k);
                    return null;
                }
                now = _ticker.Read();
                newWeight = WeighEntry(key, newValue);
                Node<K, V> created = NodeFactory.Create(_nodeFeatures, k, newValue, newWeight, now);
                SetVariableTime(created, ExpireAfterCreate(key, newValue, now));
                StampAsyncExpiryIfComputing(created, now);
                DiscardRefreshForKey(k);
                return created;
            }

            lock (n)
            {
                oldValue = n.Value;
                oldWeight = n.Weight;
                if (oldValue == null)
                {
                    cause = RemovalCause.Collected;
                }
                else if (HasExpired(n, now = _ticker.Read()))
                {
                    cause = RemovalCause.Expired;
                }
                else
                {
                    return n; // a live entry won the race: this is a hit
                }

                object keyRef = n.KeyReference;
                try
                {
                    newValue = recordStats ? LoadWithStats(key, mappingFunction, recordLoad) : mappingFunction(key);
                    if (newValue == null)
                    {
                        DiscardRefresh(keyRef);
                        removed = n;
                        n.Retire();
                        return null;
                    }
                    now = _ticker.Read();
                    newWeight = WeighEntry(key, newValue);
                    long varTime = ExpireAfterCreate(key, newValue, now);

                    n.Value = newValue;
                    n.Weight = newWeight;
                    long expirationTime = IsComputingAsync(newValue) ? (now + AsyncExpiry) : now;
                    if (_expiresAfterAccess) n.AccessTime = expirationTime;
                    if (_expiresAfterWrite || _refreshAfterWrite) n.WriteTime = expirationTime;
                    SetVariableTime(n, varTime);
                    DiscardRefresh(keyRef);
                    return n;
                }
                catch (Exception e)
                {
                    newValue = null;
                    DiscardRefresh(keyRef);
                    failure = ExceptionDispatchInfo.Capture(e);
                    removed = n;
                    n.Retire();
                    return null;
                }
            }
        });

        if (cause != null)
        {
            _statsCounter.RecordEviction(oldWeight, cause.Value);
            NotifyRemoval(key, oldValue, cause.Value);
        }
        if (node == null)
        {
            if (removed != null)
            {
                AfterWrite(new RemovalTask(this, removed));
            }
            failure?.Throw();
            return null;
        }
        if (oldValue != null && newValue == null)
        {
            // Contended hit: another writer installed a live value before this thread's computation.
            if (!IsComputingAsync(oldValue))
            {
                TryExpireAfterRead(node, key, oldValue, now);
                SetAccessTime(node, now);
            }
            if (recordStats) _statsCounter.RecordHits(1);
            AfterRead(node, now);
            return oldValue;
        }
        if (oldValue == null && cause == null)
        {
            AfterWrite(new AddTask(this, node, newWeight));
        }
        else
        {
            AfterWrite(new UpdateTask(this, node, newWeight - oldWeight));
        }
        return newValue;
    }

    /// <summary>
    /// Invokes a loading function with Caffeine's <c>statsAware</c> accounting: the miss is recorded
    /// before the call, a throw is recorded as a load failure, and (when <paramref name="recordLoad"/>)
    /// a returned value as a load success or, if null, a load failure.
    /// </summary>
    private V? LoadWithStats(K key, Func<K, V?> mappingFunction, bool recordLoad)
    {
        _statsCounter.RecordMisses(1);
        long start = _statsTicker.Read();
        V? value;
        try
        {
            value = mappingFunction(key);
        }
        catch
        {
            _statsCounter.RecordLoadFailure(_statsTicker.Read() - start);
            throw;
        }
        if (recordLoad)
        {
            long loadTime = _statsTicker.Read() - start;
            if (value == null)
            {
                _statsCounter.RecordLoadFailure(loadTime);
            }
            else
            {
                _statsCounter.RecordLoadSuccess(loadTime);
            }
        }
        return value;
    }

    public V? Remove(K key)
    {
        ThrowIfKeyNull(key);
        Node<K, V>? removed = null;
        V? oldValue = null;
        int oldWeight = 0;
        RemovalCause? cause = null;
        _data.Compute(key, (k, node) =>
        {
            if (node == null)
            {
                DiscardRefreshForKey(k);
                return null;
            }
            lock (node)
            {
                oldValue = node.Value;
                oldWeight = node.Weight;
                if (oldValue == null)
                {
                    cause = RemovalCause.Collected;
                }
                else if (HasExpired(node, _ticker.Read()))
                {
                    cause = RemovalCause.Expired;
                }
                else
                {
                    cause = RemovalCause.Explicit;
                }
                DiscardRefresh(node.KeyReference);
                removed = node;
                node.Retire();
            }
            return null;
        });

        if (cause == null)
        {
            return null;
        }
        AfterWrite(new RemovalTask(this, removed!));
        if (cause != RemovalCause.Explicit)
        {
            // An already-expired entry is an eviction, and its stale value is not returned.
            _statsCounter.RecordEviction(oldWeight, cause.Value);
        }
        NotifyRemoval(key, oldValue, cause.Value);
        return (cause == RemovalCause.Explicit) ? oldValue : null;
    }

    /// <summary>
    /// Removes the key only if it currently maps to <paramref name="value"/>. An expired entry is
    /// removed as <see cref="RemovalCause.Expired"/> regardless of its value, and the call returns
    /// <c>false</c> because no live mapping was removed.
    /// </summary>
    public bool Remove(K key, V value)
    {
        ThrowIfKeyNull(key);
        ArgumentNullException.ThrowIfNull(value);
        Node<K, V>? removed = null;
        V? oldValue = null;
        int oldWeight = 0;
        RemovalCause cause = RemovalCause.Explicit;
        _data.Compute(key, (_, node) =>
        {
            if (node == null)
            {
                return null;
            }
            lock (node)
            {
                oldValue = node.Value;
                oldWeight = node.Weight;
                if (oldValue == null)
                {
                    cause = RemovalCause.Collected;
                }
                else if (HasExpired(node, _ticker.Read()))
                {
                    cause = RemovalCause.Expired;
                }
                else if (ReferenceEquals(oldValue, value) || value.Equals(oldValue))
                {
                    cause = RemovalCause.Explicit;
                }
                else
                {
                    return node; // value no longer matches; leave it
                }
                DiscardRefresh(node.KeyReference);
                removed = node;
                node.Retire();
            }
            return null;
        });

        if (removed == null)
        {
            return false;
        }
        AfterWrite(new RemovalTask(this, removed));
        if (cause != RemovalCause.Explicit)
        {
            _statsCounter.RecordEviction(oldWeight, cause);
        }
        NotifyRemoval(key, oldValue, cause);
        return cause == RemovalCause.Explicit;
    }

    /// <summary>
    /// Replaces the value only if the key currently maps to a live (unexpired) <paramref name="oldValue"/>.
    /// When the new value is the same instance (the async completion case: replacing a future with
    /// itself), this still re-weighs the entry and resets its expiry timestamps, so the real timers start
    /// once the in-flight future has resolved; only a different instance is reported as replaced.
    /// </summary>
    public bool Replace(K key, V oldValue, V newValue)
    {
        ThrowIfKeyNull(key);
        ArgumentNullException.ThrowIfNull(oldValue);
        ArgumentNullException.ThrowIfNull(newValue);
        int weight = WeighEntry(key, newValue);

        V? replaced = null;
        int oldWeight = 0;
        long now = 0L;
        bool exceedsTolerance = false;
        Node<K, V>? node = _data.Compute(key, (_, n) =>
        {
            if (n == null)
            {
                return null;
            }
            lock (n)
            {
                V? current = n.Value;
                if (current == null
                    || (!ReferenceEquals(current, oldValue) && !oldValue.Equals(current))
                    || HasExpired(n, now = _ticker.Read()))
                {
                    return n; // old value no longer present (or expired); leave it
                }
                oldWeight = n.Weight;
                long oldVarTime = n.VariableTime;
                long varTime = ExpireAfterUpdate(n, key, newValue, now);
                n.Value = newValue;
                n.Weight = weight;

                long expirationTime = IsComputingAsync(newValue) ? (now + AsyncExpiry) : now;
                bool writeExceeds = ExceedsWriteTimeTolerance(n, expirationTime);
                bool varExceeds = _expiresVariable && Math.Abs(varTime - oldVarTime) > ExpireTolerance;
                exceedsTolerance = writeExceeds || varExceeds;
                // Reset the write timers only when the reorder is worthwhile; for a completed async
                // future the variable delta always exceeds tolerance, so its wheel timer is rescheduled.
                if (exceedsTolerance)
                {
                    if (_expiresAfterWrite || _refreshAfterWrite) n.WriteTime = expirationTime;
                }
                if (_expiresAfterAccess) n.AccessTime = expirationTime;
                SetVariableTime(n, varTime);
                DiscardRefresh(n.KeyReference);
                replaced = current;
            }
            return n;
        });

        if (replaced == null)
        {
            if (node != null)
            {
                ScheduleDrainBuffers(); // e.g. an expired entry is left for maintenance to evict
            }
            return false;
        }

        int weightDifference = weight - oldWeight;
        if (exceedsTolerance || weightDifference != 0)
        {
            AfterWrite(new UpdateTask(this, node!, weightDifference));
        }
        else
        {
            AfterRead(node!, now);
        }
        if (!ReferenceEquals(replaced, newValue))
        {
            NotifyRemoval(key, replaced, RemovalCause.Replaced);
        }
        return true;
    }

    public void Clear()
    {
        foreach (KeyValuePair<K, V> entry in SnapshotEntries())
        {
            Remove(entry.Key);
        }
    }

    // ----- reads / size / stats (ILocalCache) -----

    public long EstimatedSize => _data.Count;

    public void CleanUp()
    {
        try
        {
            PerformCleanUp();
        }
        catch
        {
            // Maintenance must not surface to the caller (no logger is available).
        }
    }

    /// <summary>
    /// Releases background resources. Cancels any pending proactive-maintenance timer scheduled via a
    /// configured <see cref="IScheduler"/>. Idempotent; safe to call more than once. The cache's data
    /// is left intact and remains readable, but no further proactive maintenance is scheduled.
    /// </summary>
    public void Dispose()
    {
        if (_pacer != null)
        {
            lock (_evictionLock)
            {
                _pacer.Cancel();
            }
        }
    }

    public CacheStats StatsSnapshot() => _statsCounter.Snapshot();

    private IEnumerable<KeyValuePair<K, V>> SnapshotEntries()
    {
        var list = new List<KeyValuePair<K, V>>();
        var e = _data.GetEnumerator();
        while (e.MoveNext())
        {
            V? v = e.Current.Value.Value;
            if (v != null)
            {
                list.Add(new KeyValuePair<K, V>(e.Current.Key, v));
            }
        }
        return list;
    }

    private void NotifyRemoval(K? key, V? value, RemovalCause cause)
    {
        IRemovalListener<K, V>? listener = _removalListener;
        if (listener == null)
        {
            return;
        }
        // Default executor: dispatch via a state-passing work item so no capturing closure is allocated
        // per eviction/replacement.
        if (ReferenceEquals(_executor, ThreadPoolExecutor.Instance))
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static s =>
                {
                    try { s.listener.OnRemoval(s.key, s.value, s.cause); }
                    catch { /* a misbehaving listener must not disrupt the cache */ }
                },
                (listener, key, value, cause),
                preferLocal: false);
            return;
        }

        // Custom executor: the closure lives in a separate method so it is not allocated on entry to the
        // hot NotifyRemoval body (a captured local would hoist the display class ahead of the guards above).
        NotifyRemovalViaExecutor(listener, key, value, cause);
    }

    private void NotifyRemovalViaExecutor(IRemovalListener<K, V> listener, K? key, V? value, RemovalCause cause)
    {
        void Run()
        {
            try { listener.OnRemoval(key, value, cause); }
            catch { /* a misbehaving listener must not disrupt the cache */ }
        }
        try
        {
            _executor.Execute(Run);
        }
        catch
        {
            Run(); // executor rejected the task; run inline so the notification is not lost
        }
    }

    /// <summary>
    /// Clears any in-flight refresh registration for the key. Called on every mutation so a
    /// concurrent write is not shadowed by a stale reload, and so a dropped executor task cannot
    /// leak a token that permanently suppresses future refreshes.
    /// </summary>
    private void DiscardRefresh(object keyReference)
    {
        if (_refreshAfterWrite)
        {
            _refreshes.Remove(keyReference);
        }
    }

    /// <summary>
    /// <see cref="DiscardRefresh(object)"/> for a key that has no node to supply its key reference;
    /// checks the feature before boxing a value-type key.
    /// </summary>
    private void DiscardRefreshForKey(K key)
    {
        if (_refreshAfterWrite)
        {
            _refreshes.Remove(key);
        }
    }

    // ===== public ICache<K,V> surface =====

    V? ICache<K, V>.GetIfPresent(K key) => GetIfPresent(key, recordStats: true);

    V? ICache<K, V>.Get(K key, Func<K, V?> mappingFunction) => ComputeIfAbsent(key, mappingFunction, recordStats: true);

    IReadOnlyDictionary<K, V> ICache<K, V>.GetAllPresent(IEnumerable<K> keys) => GetAllPresent(keys);

    void ICache<K, V>.Put(K key, V value) => Put(key, value);

    void ICache<K, V>.PutAll(IReadOnlyDictionary<K, V> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        foreach (KeyValuePair<K, V> entry in map)
        {
            Put(entry.Key, entry.Value);
        }
    }

    void ICache<K, V>.Invalidate(K key) => Remove(key);

    void ICache<K, V>.InvalidateAll(IEnumerable<K> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (K key in keys)
        {
            Remove(key);
        }
    }

    void ICache<K, V>.InvalidateAll() => Clear();

    long ICache<K, V>.EstimatedSize() => EstimatedSize;

    CacheStats ICache<K, V>.Stats() => StatsSnapshot();

    void ICache<K, V>.CleanUp() => CleanUp();

    // ===== public ILoadingCache<K,V> surface =====

    V? ILoadingCache<K, V>.Get(K key)
    {
        RequireLoader();
        return ComputeIfAbsent(key, k => _loader!.Load(k), recordStats: true);
    }

    IReadOnlyDictionary<K, V> ILoadingCache<K, V>.GetAll(IEnumerable<K> keys)
    {
        RequireLoader();
        ArgumentNullException.ThrowIfNull(keys);
        var result = new Dictionary<K, V>();
        foreach (K key in keys)
        {
            if (result.ContainsKey(key)) continue;
            V? value = ComputeIfAbsent(key, k => _loader!.Load(k), recordStats: true);
            if (value != null)
            {
                result[key] = value;
            }
        }
        return result;
    }

    void ILoadingCache<K, V>.Refresh(K key)
    {
        RequireLoader();
        long start = _statsTicker.Read();
        V? loaded;
        try
        {
            loaded = _loader!.Load(key);
        }
        catch
        {
            _statsCounter.RecordLoadFailure(_statsTicker.Read() - start);
            throw;
        }
        long elapsed = _statsTicker.Read() - start;
        if (loaded == null)
        {
            _statsCounter.RecordLoadFailure(elapsed);
            return;
        }
        _statsCounter.RecordLoadSuccess(elapsed);
        Put(key, loaded);
    }

    private void RequireLoader()
    {
        if (_loader == null)
        {
            throw new InvalidOperationException("this cache was built without a loader");
        }
    }
}
