using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    private const int WriteBufferMax = 128 - 1;
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
    private readonly IStatsCounter _statsCounter;
    private readonly IRemovalListener<K, V>? _removalListener;
    private readonly IExecutor _executor;
    private readonly ITicker _ticker;
    private readonly IWeigher<K, V> _weigher;
    private readonly ICacheLoader<K, V>? _loader;

    /// <summary>Weighs an entry and validates the weigher's contract (weight must be non-negative).</summary>
    private int WeighEntry(K key, V value)
    {
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
    private readonly MpscGrowableArrayQueue<IWriteTask> _writeBuffer = new(4, 128);
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
    private readonly Action _drainBuffersTask;

    // Cached read-buffer consumer so draining never allocates a fresh Action<Node> per cycle.
    private readonly Action<Node<K, V>> _onAccess;

    internal BoundedLocalCache(in CacheConfiguration<K, V> config, ICacheLoader<K, V>? loader)
    {
        _drainBuffersTask = RunMaintenanceUnderLock;
        _onAccess = OnAccess;
        _data = new ConcurrentHashMap<K, Node<K, V>>(config.InitialCapacity);
        _statsCounter = config.StatsCounter;
        _removalListener = config.RemovalListener;
        _executor = config.Executor;
        _ticker = config.Ticker;
        _weigher = config.Weigher;
        _loader = loader;

        _evicts = config.Evicts;
        _isWeighted = config.IsWeighted;
        _isAsync = config.IsAsync;
        _expiresAfterAccess = config.ExpiresAfterAccess;
        _expiresAfterWrite = config.ExpiresAfterWrite;
        _refreshAfterWrite = config.RefreshesAfterWrite;
        _expiresVariable = config.ExpiresVariable;
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

    public IStatsCounter StatsCounter => _statsCounter;
    public IExecutor Executor => _executor;
    public ITicker Ticker => _ticker;
    public bool IsRecordingStats => !ReferenceEquals(_statsCounter, DisabledStatsCounter.Instance);

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
        ArgumentNullException.ThrowIfNull(key);
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
    /// flight. The wrapper attaches completion handling to that new future.
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

        long now = _ticker.Read();
        Node<K, V>? updated = null;
        int weightDifference = 0;
        _data.Compute(key, (_, current) =>
        {
            bool owned = ReferenceEquals(_refreshes.GetOrDefault(keyRef), token);
            if (current == null)
            {
                return null;
            }
            bool unmodified = owned
                && ReferenceEquals(current.Value, oldValue)
                && (current.WriteTime & ~1L) == writeTime;
            if (!unmodified)
            {
                return current; // superseded; discard the refresh (the new future is orphaned)
            }
            int oldWeight = current.Weight;
            current.Value = newFuture;
            current.Weight = _weigher.Weigh(key, newFuture);
            weightDifference = current.Weight - oldWeight;
            // Reset the write time to now so a reload in flight is no longer "overdue" (prevents a
            // second refresh from being triggered on the next access while this one is still loading),
            // then apply the async sentinel if the cache also expires.
            if (_expiresAfterWrite || _refreshAfterWrite) current.WriteTime = now;
            StampAsyncExpiryIfComputing(current, now);
            updated = current;
            return current;
        });

        if (updated != null)
        {
            // Propagate the weight change to the policy bookkeeping (the in-flight future weighs 0
            // via AsyncWeigher), matching how Replace/UpdateTask keep the weighted-size telescoping.
            AfterWrite(new UpdateTask(this, updated, weightDifference));
        }

        bool stored = updated != null;
        if (stored)
        {
            // Keep the debounce token registered until the new future completes; Java clears it on
            // completion so no fresh refresh is triggered while this reload is in flight.
            ((Task)(object)newFuture).ContinueWith(_ => _refreshes.Remove(keyRef, token),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else
        {
            _refreshes.Remove(keyRef, token); // discarded; release the token now
        }
    }

    /// <summary>Synchronous refresh: load a resolved value in the background and commit it (ABA-guarded).</summary>
    private void RefreshSync(K key, V oldValue, object keyRef, object token, long writeTime)
    {
        _executor.Execute(() =>
        {
            long start = _ticker.Read();
            V? newValue;
            try
            {
                newValue = _loader!.Load(key);
            }
            catch
            {
                _refreshes.Remove(keyRef, token);
                _statsCounter.RecordLoadFailure(_ticker.Read() - start);
                return;
            }
            long loadTime = _ticker.Read() - start;

            // Commit the reload only if the entry was not modified while in flight (ABA guard on
            // both the value identity AND the write-time). A null reload removes the mapping.
            bool removed = false;
            bool replaced = false;
            Node<K, V>? updated = null;
            int weightDifference = 0;
            long commitNow = _ticker.Read();
            _data.Compute(key, (_, current) =>
            {
                bool owned = ReferenceEquals(_refreshes.GetOrDefault(keyRef), token);
                if (current == null)
                {
                    return null; // entry vanished; drop the refresh
                }
                bool unmodified = owned
                    && ReferenceEquals(current.Value, oldValue)
                    && (current.WriteTime & ~1L) == writeTime;
                if (!unmodified)
                {
                    return current; // superseded by a concurrent write; discard the reload
                }
                if (newValue == null)
                {
                    removed = true;
                    return null; // a null reload removes the entry
                }
                // Compute the variable-expiration time BEFORE mutating so a throwing user IExpiry
                // leaves the node untouched.
                long varTime = ExpireAfterUpdate(current, key, newValue, commitNow);
                int oldWeight = current.Weight;
                replaced = !ReferenceEquals(newValue, oldValue);
                current.Value = newValue;
                current.Weight = WeighEntry(key, newValue);
                weightDifference = current.Weight - oldWeight;
                SetVariableTime(current, varTime);
                if (_expiresAfterWrite || _refreshAfterWrite) current.WriteTime = commitNow;
                updated = current;
                return current;
            });

            // Propagate the weight change and wheel reschedule through the policy, mirroring the async
            // refresh path (otherwise weightedSize drifts and the write-order/timer is left stale).
            if (updated != null)
            {
                AfterWrite(new UpdateTask(this, updated, weightDifference));
            }

            if (removed)
            {
                NotifyRemoval(key, oldValue, RemovalCause.Explicit);
            }
            else if (replaced)
            {
                NotifyRemoval(key, oldValue, RemovalCause.Replaced);
            }
            _refreshes.Remove(keyRef, token);
            if (newValue == null)
            {
                _statsCounter.RecordLoadFailure(loadTime);
            }
            else
            {
                _statsCounter.RecordLoadSuccess(loadTime);
            }
        });
    }

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
        bool delayable = _readBuffer.Offer(node) != BufferResult.Full;
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
        for (int i = 0; i < 100; i++)
        {
            if (_writeBuffer.Offer(task))
            {
                ScheduleAfterWrite();
                return;
            }
            // Buffer full: help drain, then retry.
            ScheduleDrainBuffers();
        }

        // Fallback: apply inline under the lock so the task is never lost.
        lock (_evictionLock)
        {
            try { Maintenance(task); }
            catch { /* maintenance must not surface to the writer */ throw; }
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

        // If a scheduler was configured then the maintenance can be deferred and run in the near
        // future via the pacer; otherwise it will be handled by other cache activity.
        if (_pacer != null)
        {
            if (!_pacer.IsScheduled && Monitor.TryEnter(_evictionLock))
            {
                try
                {
                    if (Volatile.Read(ref _drainStatus) == Required && !_pacer.IsScheduled)
                    {
                        _pacer.Schedule(_executor, DrainBuffersTask, _ticker.Read(), Pacer.Tolerance);
                    }
                }
                finally
                {
                    Monitor.Exit(_evictionLock);
                }
            }
            return;
        }

        ScheduleDrainBuffers();
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
                    Maintenance(null);
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
        if (Evicts || ExpiresAfterAccess || ExpiresVariable)
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
                _pacer.Schedule(_executor, DrainBuffersTask, now, delay);
            }
        }
    }

    /// <summary>The maintenance task the pacer submits when a proactive drain is due.</summary>
    private void DrainBuffersTask()
    {
        lock (_evictionLock) { Maintenance(null); }
        RescheduleDrainIfIncomplete();
    }

    /// <summary>
    /// The cached delegate used by <see cref="ScheduleDrainBuffers"/> to run a maintenance cycle on
    /// the executor without allocating a fresh closure per schedule.
    /// </summary>
    private void RunMaintenanceUnderLock()
    {
        lock (_evictionLock) { Maintenance(null); }
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
        remaining = ExpireAfterAccessEntries(_windowDeque, now, remaining);
        if (_evicts)
        {
            remaining = ExpireAfterAccessEntries(_probationDeque, now, remaining);
            remaining = ExpireAfterAccessEntries(_protectedDeque, now, remaining);
        }
        if (remaining == 0)
        {
            Volatile.Write(ref _drainStatus, ProcessingToRequired); // re-arm to drain the backlog
        }
    }

    private int ExpireAfterAccessEntries(AccessOrderDeque<Node<K, V>> deque, long now, int remaining)
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
                if (stalePosition)
                {
                    deque.MoveToBack(node);
                    node = next;
                    continue;
                }
                return remaining;
            }
            EvictEntry(node, RemovalCause.Expired, now);
            remaining--;
            node = next;
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
                if (stalePosition)
                {
                    _writeOrderDeque.MoveToBack(node);
                    node = next;
                    continue;
                }
                return;
            }
            EvictEntry(node, RemovalCause.Expired, now);
            remaining--;
            node = next;
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

            int weight = candidate.PolicyWeight;
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

            int weight = candidate.PolicyWeight;
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
        => _isAsync && node.Value is Task task && !AsyncValue.IsReady(task);

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

    private static void Reorder(ILinkedDeque<Node<K, V>> deque, Node<K, V> node)
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
        K? key = node.Key;
        V? value = null;
        bool removed = false;
        bool resurrect = false;
        RemovalCause actualCause = cause;

        _data.ComputeIfPresent(key!, (_, n) =>
        {
            if (!ReferenceEquals(n, node))
            {
                return n; // a different node now occupies the key; leave it
            }
            lock (node)
            {
                value = node.Value;
                if (key == null || value == null)
                {
                    actualCause = RemovalCause.Collected;
                }
                else
                {
                    actualCause = cause;
                }

                if (actualCause == RemovalCause.Expired)
                {
                    // Re-verify: the entry may have been touched since it was queued for expiration.
                    if (!HasExpired(node, now))
                    {
                        resurrect = true;
                        return node;
                    }
                }
                else if (actualCause == RemovalCause.Size)
                {
                    if (node.Weight == 0)
                    {
                        resurrect = true;
                        return node; // pinned entry, keep it
                    }
                }

                removed = true;
                node.Retire();
                return null; // remove from the map
            }
        });

        if (resurrect)
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
            DiscardRefresh(node.KeyReference); // cancel any in-flight reload for the evicted key
            _statsCounter.RecordEviction(node.Weight, actualCause);
            NotifyRemoval(key, value, actualCause);
        }
        return true;
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
                if (_weight > c._maximum)
                {
                    c.EvictEntry(_node, RemovalCause.Size);
                }
                else if (_weight > c._windowMaximum)
                {
                    c._windowDeque.OfferFirst(_node);
                }
                else
                {
                    c._windowDeque.OfferLast(_node);
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
        ArgumentNullException.ThrowIfNull(key);
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
                    // Optimistic fast path: a live existing value short-circuits without locking.
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
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(mappingFunction);

        Node<K, V>? existing = _data.GetOrDefault(key);
        long readNow = _ticker.Read();
        if (existing != null && existing.Value != null && !HasExpired(existing, readNow))
        {
            V hitValue = existing.Value!;
            if (recordStats) _statsCounter.RecordHits(1);
            SetAccessTime(existing, readNow);
            TryExpireAfterRead(existing, key, hitValue, readNow);
            AfterRead(existing, readNow);
            return existing.Value;
        }

        long now = _ticker.Read();
        Node<K, V>? newNode = null;
        Node<K, V>? updatedNode = null;
        V? computed = null;
        bool ran = false;
        int updateWeightDifference = 0;
        // When an expired node is replaced in place, its prior value/weight/cause are captured so the
        // EXPIRED removal notification and eviction stat fire after the atomic section.
        V? expiredOldValue = null;
        int expiredOldWeight = 0;

        _data.Compute(key, (k, node) =>
        {
            if (node != null && node.Value != null && !HasExpired(node, now))
            {
                computed = node.Value;
                return node;
            }
            ran = true;
            computed = mappingFunction(k);
            if (computed == null)
            {
                // Absent (or an expired node with no replacement) — remove any stale node.
                return null;
            }
            int weight = WeighEntry(k, computed);
            if (node != null)
            {
                // Refresh an expired node in place so its identity (and policy links) are preserved.
                lock (node)
                {
                    // Compute the variable-expiration time BEFORE mutating the node so that a throwing
                    // user IExpiry leaves the node untouched.
                    long varTime = ExpireAfterCreate(k, computed, now);
                    expiredOldValue = node.Value;
                    expiredOldWeight = node.Weight;
                    updateWeightDifference = weight - expiredOldWeight;
                    node.Value = computed;
                    node.Weight = weight;
                    SetVariableTime(node, varTime);
                    if (_expiresAfterWrite || _refreshAfterWrite) node.WriteTime = now;
                    if (_expiresAfterAccess) node.AccessTime = now;
                }
                updatedNode = node;
                return node;
            }
            newNode = NodeFactory.Create(_nodeFeatures, k, computed, weight, now);
            SetVariableTime(newNode, ExpireAfterCreate(k, computed, now));
            StampAsyncExpiryIfComputing(newNode, now);
            return newNode;
        });

        if (ran)
        {
            if (recordStats) _statsCounter.RecordMisses(1);
            if (newNode != null)
            {
                if (recordStats && recordLoad) _statsCounter.RecordLoadSuccess(_ticker.Read() - now);
                AfterWrite(new AddTask(this, newNode, newNode.Weight));
            }
            else if (updatedNode != null)
            {
                if (recordStats && recordLoad) _statsCounter.RecordLoadSuccess(_ticker.Read() - now);
                // The replaced node had expired: report the eviction and propagate the weight delta.
                if (expiredOldValue != null)
                {
                    _statsCounter.RecordEviction(expiredOldWeight, RemovalCause.Expired);
                    NotifyRemoval(key, expiredOldValue, RemovalCause.Expired);
                }
                AfterWrite(new UpdateTask(this, updatedNode, updateWeightDifference));
            }
            else if (recordStats && recordLoad)
            {
                _statsCounter.RecordLoadFailure(_ticker.Read() - now);
            }
        }
        else
        {
            if (recordStats) _statsCounter.RecordHits(1);
            if (existing != null) AfterRead(existing, now);
        }
        return computed;
    }

    public V? Remove(K key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Node<K, V>? removed = null;
        V? value = null;
        bool expired = false;
        long now = _ticker.Read();
        _data.Compute(key, (_, node) =>
        {
            if (node == null)
            {
                return null;
            }
            lock (node)
            {
                value = node.Value;
                expired = value != null && HasExpired(node, now);
                node.Retire();
                removed = node;
            }
            return null;
        });

        if (removed != null)
        {
            DiscardRefresh(removed.KeyReference); // cancel any in-flight reload for the removed key
            AfterWrite(new RemovalTask(this, removed));
            if (value != null)
            {
                // An already-expired entry is reported as EXPIRED, and its stale value is not
                // returned to the caller.
                NotifyRemoval(key, value, expired ? RemovalCause.Expired : RemovalCause.Explicit);
            }
            return expired ? null : value;
        }
        return null;
    }

    /// <summary>Removes the key only if it currently maps to <paramref name="value"/>.</summary>
    public bool Remove(K key, V value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        Node<K, V>? removed = null;
        _data.Compute(key, (_, node) =>
        {
            if (node == null)
            {
                return node;
            }
            lock (node)
            {
                if (!ReferenceEquals(node.Value, value) && !value.Equals(node.Value))
                {
                    return node; // value no longer matches; leave it
                }
                node.Retire();
                removed = node;
            }
            return null;
        });

        if (removed != null)
        {
            DiscardRefresh(removed.KeyReference);
            AfterWrite(new RemovalTask(this, removed));
            NotifyRemoval(key, value, RemovalCause.Explicit);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Replaces the value only if the key currently maps to <paramref name="oldValue"/>. When the new
    /// value equals the old (the async completion case: replacing a future with itself), this still
    /// re-weighs the entry and resets its expiry timestamps to now, so the real timers start once the
    /// in-flight future has resolved.
    /// </summary>
    public bool Replace(K key, V oldValue, V newValue)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(oldValue);
        ArgumentNullException.ThrowIfNull(newValue);
        int weight = WeighEntry(key, newValue);
        long now = _ticker.Read();

        Node<K, V>? updated = null;
        int weightDifference = 0;
        bool exceedsTolerance = false;
        _data.Compute(key, (_, node) =>
        {
            if (node == null)
            {
                return node;
            }
            lock (node)
            {
                V? current = node.Value;
                if (current == null
                    || (!ReferenceEquals(current, oldValue) && !oldValue.Equals(current)))
                {
                    return node; // old value no longer present
                }
                int oldWeight = node.Weight;
                long oldVarTime = node.VariableTime;
                long varTime = ExpireAfterUpdate(node, key, newValue, now);
                node.Value = newValue;
                node.Weight = weight;
                weightDifference = weight - oldWeight;
                bool writeExceeds = ExceedsWriteTimeTolerance(node, now);
                bool varExceeds = _expiresVariable && Math.Abs(varTime - oldVarTime) > ExpireTolerance;
                exceedsTolerance = writeExceeds || varExceeds;
                // Reset the write timers only when the reorder is worthwhile; for a completed async
                // future the variable delta always exceeds tolerance, so its wheel timer is rescheduled.
                if (exceedsTolerance)
                {
                    if (_expiresAfterWrite || _refreshAfterWrite) node.WriteTime = now;
                }
                if (_expiresAfterAccess) node.AccessTime = now;
                SetVariableTime(node, varTime);
                updated = node;
            }
            return node;
        });

        if (updated != null)
        {
            if (exceedsTolerance || weightDifference != 0)
            {
                AfterWrite(new UpdateTask(this, updated, weightDifference));
            }
            else
            {
                AfterRead(updated, now);
            }
            return true;
        }
        return false;
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
        lock (_evictionLock)
        {
            Maintenance(null);
        }
        RescheduleDrainIfIncomplete();
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
        if (_removalListener == null)
        {
            return;
        }
        void Run()
        {
            try { _removalListener.OnRemoval(key, value, cause); }
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
        long start = _ticker.Read();
        V? loaded = _loader!.Load(key);
        long elapsed = _ticker.Read() - start;
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
