using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;
using static Espresso.Internal.WindowClimber;
using static Espresso.Internal.WindowClimber.DensityClimber;
using static Espresso.Internal.WindowClimber.Ladder;
using static Espresso.Internal.WindowClimber.ReactiveClimber;
using static Espresso.Internal.WindowClimber.Reading;
using static Espresso.Internal.WindowClimber.Step;
using static Espresso.Tests.ClimberInvariants;
using static Espresso.Tests.FluentAssert;

namespace Espresso.Tests;

/// <summary>
/// Cache-level tests of the adaptive window: the <see cref="WindowClimber"/> integration in
/// <c>BoundedLocalCache</c> (hit/miss accounting, the climb gate, and the region transfers that
/// apply the climber's adjustment). The climber's own decisions are covered by
/// <see cref="WindowClimberTest"/>.
/// </summary>
public sealed class HillClimberTest : CacheTestBase
{
    private const double PercentMainProtected = 0.80;
    private const int QueueTransferThreshold = 1_000;

    // A full cache: a maximum of 50 holding 50 entries.
    private const int Full = 50;

    private const int Idle = 0;
    private const int Required = 1;
    private const int ProcessingToRequired = 3;

    /* --------------- Partition invariants under real workloads --------------- */

    [Fact]
    public void Build_InitialPartition_UsesOnePercentWindowRatio()
    {
        var cache = Probe.Of(SizeCache(10_000));

        // window = max - 0.99*max = 1% of max; mainProtected = 0.80*(max-window).
        Assert.Equal(100, cache.WindowMaximum);
        Assert.Equal((long)(0.80 * (10_000 - 100)), cache.MainProtectedMaximum);
        Assert.Equal(0, cache.Climber.Adjustment());
        AssertInvariants(cache.Climber, cache.Maximum);
    }

    [Theory]
    [InlineData(64)]       // the slow-adapt reactive tier
    [InlineData(1_000)]    // the stock reactive tier
    [InlineData(10_000)]   // the density tier
    public void Climb_AfterMixedWorkload_KeepsPartitionInvariant(int max)
    {
        var cache = Probe.Of(SizeCache(max));

        var rng = new Random(42);
        for (int round = 0; round < 40; round++)
        {
            for (int i = 0; i < Math.Max(max, 2_000); i++)
            {
                int key = rng.Next(max * 3);
                if (cache.Cache.GetIfPresent(key) == null)
                {
                    cache.Cache.Put(key, "v" + key);
                }
            }
            cache.Cache.CleanUp(); // run maintenance (incl. climb) synchronously
            cache.AssertValid();
        }
    }

    [Fact]
    public void Climb_UnderRecencyFrequencyWorkload_AdaptsWindowFromInitialRatio()
    {
        const int max = 500;
        var cache = Probe.Of(SizeCache(max));
        long initialWindow = cache.WindowMaximum;

        // Drive a mixed recency/frequency workload for many sample periods. The climber must move the
        // window away from its fixed 1% starting point (direction depends on the workload; here we
        // only assert that adaptation happens and the partition stays valid).
        var rng = new Random(11);
        for (int round = 0; round < 80; round++)
        {
            for (int i = 0; i < max * 2; i++)
            {
                // Half the traffic is a hot frequency-friendly set, half is a recency-friendly scan.
                int key = (i % 2 == 0) ? rng.Next(max / 4) : (i % (max + max / 2));
                if (cache.Cache.GetIfPresent(key) == null)
                {
                    cache.Cache.Put(key, "v" + key);
                }
            }
            cache.Cache.CleanUp();
        }

        Assert.True(cache.WindowMaximum != initialWindow,
            $"window {cache.WindowMaximum} did not adapt from its initial {initialWindow}");
        cache.AssertValid();
    }

    [Fact]
    public void Climb_UnderHeavyChurn_KeepsSizeBounded()
    {
        const int max = 400;
        var cache = Probe.Of(SizeCache(max));

        var rng = new Random(7);
        for (int i = 0; i < 200_000; i++)
        {
            int key = rng.Next(max * 4);
            if (cache.Cache.GetIfPresent(key) == null)
            {
                cache.Cache.Put(key, "v" + key);
            }
        }
        cache.Cache.CleanUp();

        Assert.True(cache.Cache.EstimatedSize() <= max,
            $"size {cache.Cache.EstimatedSize()} exceeded bound {max} after climbing");
        cache.AssertValid();
    }

    [Fact]
    public void Climb_OnSmallCache_UsesSlowAdaptTierWithoutCrashing()
    {
        // Small caches (<= 512) use the slow-adapt reactive tier (step decay / grown sample period).
        var cache = Probe.Of(SizeCache(64));
        Assert.IsType<ReactiveClimber>(cache.Climber.tier);

        for (int round = 0; round < 100; round++)
        {
            for (int i = 0; i < 200; i++)
            {
                int key = i % 96;
                if (cache.Cache.GetIfPresent(key) == null)
                {
                    cache.Cache.Put(key, "v" + key);
                }
            }
            cache.Cache.CleanUp();
        }

        Assert.True(cache.Cache.EstimatedSize() <= 64);
        cache.AssertValid();
    }

    [Fact]
    public void Climb_LargeCache_UsesDensityTierAndStaysValid()
    {
        const int max = 8_192;
        var cache = Probe.Of(SizeCache(max));
        Assert.IsType<DensityClimber>(cache.Climber.tier);

        // a skewed workload over a key space larger than the cache, so that it evicts and samples
        var rng = new Random(3);
        for (int i = 0; i < 400_000; i++)
        {
            int key = (int)Math.Min(int.MaxValue, Math.Floor(Math.Pow(rng.NextDouble(), 3) * 4 * max));
            if (cache.Cache.GetIfPresent(key) == null)
            {
                cache.Cache.Put(key, "v" + key);
            }
            if ((i % 10_000) == 0)
            {
                cache.Cache.CleanUp();
                cache.AssertValid();
            }
        }
        cache.Cache.CleanUp();
        Assert.True(cache.Cache.EstimatedSize() <= max);
        cache.AssertValid();
    }

    /* --------------- Hit / miss accounting --------------- */

    [Fact]
    public void OnAccess_ProbationHitAttributedBeforePromotion()
    {
        // The climber attributes a hit to the region that earned it: onAccess captures the node's
        // membership before reorderProbation can promote it, so the promoting access counts toward
        // the probation sample and only the next access counts as a protected hit.
        var cache = Probe.Of(FullCache(weighted: false));
        lock (cache.EvictionLock)
        {
            var node = cache.ProbationDeque.PeekFirst!;
            cache.Climber.ResetSample();

            cache.OnAccess(node, Access.Hit);
            AssertThat(node.InMainProtected).IsTrue();
            AssertThat(cache.Climber.sample.probationHits).IsEqualTo(1);
            AssertThat(cache.Climber.sample.hits).IsEqualTo(1);

            cache.OnAccess(node, Access.Hit);
            AssertThat(cache.Climber.sample.probationHits).IsEqualTo(1);
            AssertThat(cache.Climber.sample.hits).IsEqualTo(2);
        }
    }

    [Fact]
    public void OnAccess_Quiet_RecordsNeitherUsageNorHit()
    {
        var cache = Probe.Of(FullCache(weighted: false));
        lock (cache.EvictionLock)
        {
            var node = cache.WindowDeque.PeekFirst!;
            int key = node.Key;
            int frequency = cache.Sketch.Frequency(key);
            cache.Climber.ResetSample();

            cache.OnAccess(node, Access.Quiet);
            AssertThat(cache.Climber.sample.hits).IsEqualTo(0);
            AssertThat(cache.Climber.sample.misses).IsEqualTo(0);
            AssertThat(cache.Sketch.Frequency(key)).IsEqualTo(frequency);
        }
    }

    [Fact]
    public void ZeroWeight_DoesNotRecordClimberHit()
    {
        var cache = Probe.Of(Cache.NewBuilder<int, string>()
            .MaximumWeight(Full)
            .Weigher((_, _) => 0)
            .Executor(DirectExecutor.Instance)
            .Build());
        cache.Cache.Put(1, "a");
        cache.Cache.CleanUp();
        cache.Sketch.EnsureCapacity(Full);
        int frequency = cache.Sketch.Frequency(1);
        long windowHits = cache.Climber.sample.windowHits;
        long hits = cache.Climber.sample.hits;

        var value = cache.Cache.GetIfPresent(1);
        cache.Cache.CleanUp();

        AssertThat(value).IsNotNull();
        // A zero-weight entry occupies no capacity, so its hit must not steer the climber; the
        // admission sketch still observes the key
        AssertThat(cache.Climber.sample.hits).IsEqualTo(hits);
        AssertThat(cache.Climber.sample.windowHits).IsEqualTo(windowHits);
        AssertThat(cache.Sketch.Frequency(1)).IsEqualTo(frequency + 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegionSizes_MatchQueuedPolicyWeights(bool weighted)
    {
        // The climber's transfers move nodes by policy weight against the region sizes, so a node's
        // policy weight must be set once (by its AddTask), never pre-seeded at construction
        var cache = Probe.Of(FullCache(weighted));
        PrepareForAdaption(cache, recencyBias: true);
        lock (cache.EvictionLock)
        {
            long window = 0, isProtected = 0, total = 0;
            foreach (var node in cache.WindowDeque) { AssertThat(node.PolicyWeight).IsEqualTo(node.Weight); window += node.PolicyWeight; }
            foreach (var node in cache.ProtectedDeque) { AssertThat(node.PolicyWeight).IsEqualTo(node.Weight); isProtected += node.PolicyWeight; }
            foreach (var node in cache.ProbationDeque) { AssertThat(node.PolicyWeight).IsEqualTo(node.Weight); total += node.PolicyWeight; }
            total += window + isProtected;

            AssertThat(cache.WindowWeightedSize).IsEqualTo(window);
            AssertThat(cache.MainProtectedWeightedSize).IsEqualTo(isProtected);
            AssertThat(cache.WeightedSize).IsEqualTo(total);
        }
    }

    [Fact]
    public void AsyncInFlightRead_DoesNotRecordClimberHit()
    {
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(Full)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();
        var local = AsyncLocal.Of(cache);
        local.Sketch.EnsureCapacity(Full);
        var future = new TaskCompletionSource<string>().Task;
        cache.Put(1, future);
        cache.CleanUp();
        long hits = local.Cache.Climber!.sample.hits;
        AssertThat(local.Data.GetOrDefault(1)!.PolicyWeight).IsEqualTo(0);

        // An in-flight future's policy weight is zero: its coalesced reads are free window credit
        // until the load completes, so they must not steer the climber
        var read = cache.GetIfPresent(1);
        cache.CleanUp();
        AssertThat(ReferenceEquals(read, future)).IsTrue();
        AssertThat(local.Cache.Climber.sample.hits).IsEqualTo(hits);
    }

    [Fact]
    public void AsyncCompletion_DoesNotRecordAccess()
    {
        // A load's completion finalizes the entry's weight and expiration; it is bookkeeping, not a
        // usage, so the load counts once (the insertion's miss and sketch increment)
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(Full)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();
        var local = AsyncLocal.Of(cache);
        local.Sketch.EnsureCapacity(Full);

        var future = new TaskCompletionSource<string>();
        cache.Put(1, future.Task);
        future.SetResult("a");
        cache.CleanUp();

        AssertThat(local.Data.GetOrDefault(1)!.PolicyWeight).IsEqualTo(1);
        AssertThat(local.Cache.Climber!.sample.misses).IsEqualTo(1);
        AssertThat(local.Cache.Climber.sample.hits).IsEqualTo(0);
        AssertThat(local.Cache.Climber.sample.windowHits).IsEqualTo(0);
        AssertThat(local.Sketch.Frequency(1)).IsEqualTo(1);
    }

    [Fact]
    public async Task AsyncBulkCompletion_DoesNotRecordAccess()
    {
        var cache = Cache.NewBuilder<int, string>()
            .MaximumSize(Full)
            .Executor(DirectExecutor.Instance)
            .BuildAsync();
        var local = AsyncLocal.Of(cache);
        local.Sketch.EnsureCapacity(Full);
        int[] keys = { 1, 2, 3 };

        var result = await cache.GetAll(keys, (missing, _) =>
        {
            var loaded = new Dictionary<int, string>();
            foreach (int key in missing)
            {
                loaded[key] = "v" + key;
            }
            return Task.FromResult<IReadOnlyDictionary<int, string>>(loaded);
        });
        cache.CleanUp();

        AssertThat(result.Count).IsEqualTo(keys.Length);
        AssertThat(local.Cache.Climber!.sample.misses).IsEqualTo(keys.Length);
        AssertThat(local.Cache.Climber.sample.hits).IsEqualTo(0);
        AssertThat(local.Cache.Climber.sample.windowHits).IsEqualTo(0);
        foreach (int key in keys)
        {
            AssertThat(local.Sketch.Frequency(key)).IsEqualTo(1);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replace_RecordsAccessUnlessQuiet(bool quietly)
    {
        var cache = Probe.Of(FullCache(weighted: false));
        cache.Sketch.EnsureCapacity(Full);
        int frequency = cache.Sketch.Frequency(0);
        long hits = cache.Climber.sample.hits;

        AssertThat(cache.Blc.Replace(0, "v0", "a", quietly)).IsTrue();
        cache.Cache.CleanUp();

        AssertThat(cache.Climber.sample.hits).IsEqualTo(quietly ? hits : hits + 1);
        AssertThat(cache.Sketch.Frequency(0)).IsEqualTo(quietly ? frequency : frequency + 1);
    }

    [Fact]
    public void Read_RecordsClimberHit()
    {
        var cache = Probe.Of(SizeCache(Full));
        cache.Sketch.EnsureCapacity(Full);
        cache.Cache.Put(1, "a");
        cache.Cache.CleanUp();
        long hits = cache.Climber.sample.hits;
        long windowHits = cache.Climber.sample.windowHits;

        AssertThat(cache.Cache.GetIfPresent(1)).IsNotNull();
        cache.Cache.CleanUp();

        AssertThat(cache.Climber.sample.hits).IsEqualTo(hits + 1);
        AssertThat(cache.Climber.sample.windowHits).IsEqualTo(windowHits + 1);
    }

    [Fact]
    public void Insert_RecordsClimberMiss()
    {
        var cache = Probe.Of(SizeCache(Full));
        // an uninitialized sketch makes maintenance reset the sample, so size it first
        cache.Sketch.EnsureCapacity(Full);
        long misses = cache.Climber.sample.misses;

        cache.Cache.Put(1, "a");
        cache.Cache.CleanUp();

        AssertThat(cache.Climber.sample.misses).IsEqualTo(misses + 1);
        AssertThat(cache.Climber.sample.hits).IsEqualTo(0);
    }

    [Fact]
    public void ExpiredReload_RecordsClimberMiss()
    {
        var ticker = new FakeTicker();
        var cache = Probe.Of(Cache.NewBuilder<int, string>()
            .MaximumSize(Full)
            .ExpireAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .RecordStats()
            .Build());
        cache.Cache.Put(1, "a");
        cache.Cache.CleanUp();
        cache.Sketch.EnsureCapacity(Full);
        int frequency = cache.Sketch.Frequency(1);
        long misses = cache.Climber.sample.misses;
        long hits = cache.Climber.sample.hits;
        var stats = cache.Cache.Stats();

        ticker.Advance(TimeSpan.FromMinutes(2));
        var value = cache.Cache.Get(1, _ => "b");
        cache.Cache.CleanUp();

        AssertThat(value).IsEqualTo("b");
        AssertThat(cache.Cache.Stats().HitCount - stats.HitCount).IsEqualTo(0);
        AssertThat(cache.Cache.Stats().MissCount - stats.MissCount).IsEqualTo(1);
        AssertThat(cache.Climber.sample.hits).IsEqualTo(hits);
        AssertThat(cache.Climber.sample.windowHits).IsEqualTo(0);
        AssertThat(cache.Climber.sample.misses).IsEqualTo(misses + 1);
        AssertThat(cache.Sketch.Frequency(1)).IsEqualTo(frequency + 1);
    }

    [Fact]
    public void ExpiredPut_RecordsClimberMiss()
    {
        var ticker = new FakeTicker();
        var cache = Probe.Of(Cache.NewBuilder<int, string>()
            .MaximumSize(Full)
            .ExpireAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build());
        cache.Cache.Put(1, "a");
        cache.Cache.CleanUp();
        cache.Sketch.EnsureCapacity(Full);
        int frequency = cache.Sketch.Frequency(1);
        long misses = cache.Climber.sample.misses;
        long hits = cache.Climber.sample.hits;

        ticker.Advance(TimeSpan.FromMinutes(2));
        var previous = cache.Blc.Put(1, "b");
        cache.Cache.CleanUp();

        AssertThat(previous).IsNull();
        AssertThat(cache.Climber.sample.hits).IsEqualTo(hits);
        AssertThat(cache.Climber.sample.windowHits).IsEqualTo(0);
        AssertThat(cache.Climber.sample.misses).IsEqualTo(misses + 1);
        AssertThat(cache.Sketch.Frequency(1)).IsEqualTo(frequency + 1);
    }

    [Fact]
    public void Put_RecordsClimberHit()
    {
        var ticker = new FakeTicker();
        var cache = Probe.Of(Cache.NewBuilder<int, string>()
            .MaximumWeight(long.MaxValue >> 1)
            .Weigher((_, v) => v.Length)
            .ExpireAfterWrite(TimeSpan.FromMinutes(1))
            .Ticker(ticker.Read)
            .Executor(DirectExecutor.Instance)
            .Build());
        for (int i = 0; i < Full; i++)
        {
            cache.Cache.Put(i, "v" + i);
        }
        cache.Cache.CleanUp();
        cache.Sketch.EnsureCapacity(Full);
        int frequency = cache.Sketch.Frequency(0);
        long misses = cache.Climber.sample.misses;
        long hits = cache.Climber.sample.hits;

        // the weight change routes the live update through the same task the expired put takes
        var previous = cache.Blc.Put(0, "a much longer value");
        cache.Cache.CleanUp();

        AssertThat(previous).IsEqualTo("v0");
        AssertThat(cache.Climber.sample.hits).IsEqualTo(hits + 1);
        AssertThat(cache.Climber.sample.misses).IsEqualTo(misses);
        AssertThat(cache.Sketch.Frequency(0)).IsEqualTo(frequency + 1);
    }

    /* --------------- Demotion --------------- */

    [Fact]
    public void DemoteFromMain_Threshold()
    {
        var cache = Probe.Of(SizeCache(20 * QueueTransferThreshold));
        PromoteAll(cache, 10 * QueueTransferThreshold);
        lock (cache.EvictionLock)
        {
            int protectedCount = cache.ProtectedDeque.Count;
            int probationCount = cache.ProbationDeque.Count;
            long protectedSize = cache.MainProtectedWeightedSize;
            AssertThat(protectedCount).IsGreaterThan(2 * QueueTransferThreshold);

            cache.MainProtectedMaximum = 1;
            cache.DemoteFromMainProtected();

            AssertThat(cache.ProbationDeque.Count).IsEqualTo(probationCount + QueueTransferThreshold);
            AssertThat(cache.ProtectedDeque.Count).IsEqualTo(protectedCount - QueueTransferThreshold);
            AssertThat(cache.MainProtectedWeightedSize).IsEqualTo(protectedSize - QueueTransferThreshold);
            AssertThat(cache.DrainStatus).IsEqualTo(ProcessingToRequired);
        }
    }

    [Fact]
    public void DemoteFromMain_CappedBacklog()
    {
        var cache = Probe.Of(SizeCache(10_000));
        PromoteAll(cache, 10_000);
        AssertThat(cache.DrainStatus).IsEqualTo(Idle);
        AssertThat(cache.MainProtectedWeightedSize).IsEqualTo(cache.MainProtectedMaximum);
        AssertThat(cache.MainProtectedWeightedSize).IsGreaterThan(QueueTransferThreshold);

        // A resize can leave the protected region oversized by more than one cycle's transfer budget,
        // so the demotion is re-armed to drain the backlog across cycles. Espresso has no resize api,
        // so this replays what a policy resize does: resize and maintain under the lock.
        lock (cache.EvictionLock)
        {
            cache.SetMaximum(8_000);
            cache.Maintenance();
        }
        AssertThat(cache.MainProtectedWeightedSize).IsGreaterThan(cache.MainProtectedMaximum);
        AssertThat(cache.DrainStatus).IsEqualTo(Required);

        cache.Cache.CleanUp();
        AssertThat(cache.DrainStatus).IsEqualTo(Idle);
        AssertThat(cache.MainProtectedWeightedSize).IsEqualTo(cache.MainProtectedMaximum);
        cache.AssertValid();
    }

    /* --------------- Applying the adjustment --------------- */

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adapt_IncreaseWindow(bool weighted)
    {
        var cache = Probe.Of(FullCache(weighted));
        PrepareForAdaption(cache, recencyBias: false);

        int sampleSize = cache.Sketch.sampleSize;
        long protectedSize = cache.MainProtectedWeightedSize;
        long protectedMaximum = cache.MainProtectedMaximum;
        long windowSize = cache.WindowWeightedSize;
        long windowMaximum = cache.WindowMaximum;

        Adapt(cache, sampleSize);

        AssertThat(cache.MainProtectedWeightedSize).IsLessThan(protectedSize);
        AssertThat(cache.MainProtectedMaximum).IsLessThan(protectedMaximum);
        AssertThat(cache.WindowWeightedSize).IsGreaterThan(windowSize);
        AssertThat(cache.WindowMaximum).IsGreaterThan(windowMaximum);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adapt_DecreaseWindow(bool weighted)
    {
        var cache = Probe.Of(FullCache(weighted));
        PrepareForAdaption(cache, recencyBias: true);

        int sampleSize = cache.Sketch.sampleSize;
        long protectedSize = cache.MainProtectedWeightedSize;
        long protectedMaximum = cache.MainProtectedMaximum;
        long windowSize = cache.WindowWeightedSize;
        long windowMaximum = cache.WindowMaximum;

        Adapt(cache, sampleSize);

        AssertThat(cache.MainProtectedWeightedSize).IsGreaterThan(protectedSize);
        AssertThat(cache.MainProtectedMaximum).IsGreaterThan(protectedMaximum);
        AssertThat(cache.WindowWeightedSize).IsLessThan(windowSize);
        AssertThat(cache.WindowMaximum).IsLessThan(windowMaximum);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adapt_LargeSampleCount(bool weighted)
    {
        var cache = Probe.Of(FullCache(weighted));
        PrepareForAdaption(cache, recencyBias: false);

        // Counters past int.MaxValue must not disturb the hill climber: a sample count exceeding the
        // int range must still reach the adjustment/reset rather than wrap to a negative value.
        long hits = (long)int.MaxValue + 1;
        long misses = (long)int.MaxValue + 1;

        cache.Climber.sample.hits = hits;
        cache.Climber.sample.misses = misses;
        cache.Climber.sample.previousHitRate = 0.80;

        long windowMaximum = cache.WindowMaximum;
        long protectedMaximum = cache.MainProtectedMaximum;
        cache.Climb();

        AssertThat(cache.Climber.sample.hits).IsEqualTo(0);
        AssertThat(cache.Climber.sample.misses).IsEqualTo(0);
        AssertThat(cache.WindowMaximum).IsNotEqualTo(windowMaximum);
        AssertThat(cache.MainProtectedMaximum).IsNotEqualTo(protectedMaximum);
    }

    [Fact]
    public void Adapt_LargeCache_IncreaseWindowOnWindowDensity()
    {
        // Above the small-cache threshold the climber directs the window by the within-sample hit
        // density of the two regions. Forcing all sampled hits into the admission window makes its
        // density dominate the (hitless) main region, so the signed error is positive and the window
        // grows. A large sample count guarantees the adjustment runs and the counters reset.
        var cache = Probe.Of(FullCache(weighted: false));
        cache.SetMaximum(2 * DensityThreshold);
        cache.Sketch.EnsureCapacity(cache.Maximum);
        cache.WindowMaximum = cache.Maximum / 10;
        cache.MainProtectedMaximum = (long)(PercentMainProtected * (cache.Maximum - cache.WindowMaximum));

        long hits = (long)int.MaxValue + 1;
        cache.Climber.sample.previousHitRate = 0.50;
        cache.Climber.sample.windowHits = hits;
        cache.Climber.sample.misses = hits;
        cache.Climber.sample.hits = hits;

        DetermineAdjustment(cache);

        AssertThat(cache.Climber.sample.windowHits).IsEqualTo(0);
        AssertThat(cache.Climber.Adjustment()).IsGreaterThan(0);
        AssertThat(cache.Climber.sample.misses).IsEqualTo(0);
        AssertThat(cache.Climber.sample.hits).IsEqualTo(0);
    }

    [Fact]
    public void Adapt_LargeCache_DecreaseWindowOnMainDensity()
    {
        // The mirror case: all sampled hits land in the main region, so its density dominates and the
        // signed error is negative, shrinking the window. The window sits far above its floor, so the
        // proportional step is applied unclamped.
        var cache = Probe.Of(FullCache(weighted: false));
        cache.SetMaximum(2 * DensityThreshold);
        cache.Sketch.EnsureCapacity(cache.Maximum);
        cache.WindowMaximum = cache.Maximum / 2;
        cache.MainProtectedMaximum = (long)(PercentMainProtected * (cache.Maximum - cache.WindowMaximum));

        long hits = (long)int.MaxValue + 1;
        cache.Climber.sample.previousHitRate = 0.50;
        cache.Climber.sample.windowHits = 0;
        cache.Climber.sample.misses = hits;
        cache.Climber.sample.hits = hits;

        long windowMaximum = cache.WindowMaximum;
        DetermineAdjustment(cache);

        long floor = RoundHalfUp(WindowFloorFraction * cache.Maximum);
        AssertThat(windowMaximum + cache.Climber.Adjustment()).IsGreaterThan(floor);
        AssertThat(cache.Climber.Adjustment()).IsLessThan(0);
    }

    [Fact]
    public void Adapt_LargeCache_Weighted_ClimbsByDensity()
    {
        // The tier gate reads the configured maximum in its native weight units, so a weighted cache
        // climbs by density even though its entry count sits far below the threshold, and the step
        // it commands is denominated in those same units
        var cache = Probe.Of(FullCache(weighted: true));
        cache.SetMaximum(2 * DensityThreshold);
        cache.Sketch.EnsureCapacity(cache.Maximum);
        cache.WindowMaximum = cache.Maximum / 10;
        cache.MainProtectedMaximum = (long)(PercentMainProtected * (cache.Maximum - cache.WindowMaximum));

        long hits = (long)int.MaxValue + 1;
        cache.Climber.sample.previousHitRate = 0.50;
        cache.Climber.sample.windowHits = hits;
        cache.Climber.sample.misses = hits;
        cache.Climber.sample.hits = hits;

        DetermineAdjustment(cache);

        AssertThat(cache.Climber.Adjustment()).IsGreaterThan(0);
        AssertThat(cache.Climber.sample.windowHits).IsEqualTo(0);
        AssertThat(cache.Climber.sample.hits).IsEqualTo(0);
    }

    [Fact]
    public void Adapt_LargeCache_ClimbAppliesProbeEntryStride()
    {
        // A starved window at a blind corner arms an up probe, and climb() must carry the entry
        // stride through the region transfer: the window and protected maxima shift by the stride
        // with their sum conserved
        var cache = Probe.Of(WeightedCache(Full * 10));
        cache.SetMaximum(2 * DensityThreshold);
        // enough weight to pass the half of the maximum that releases the climber
        for (int i = 0; i < ((DensityThreshold / 10) + 10); i++)
        {
            cache.Cache.Put(i, "v" + i);
        }
        cache.Cache.CleanUp();
        cache.WindowMaximum = cache.Maximum / 10;
        cache.MainProtectedMaximum = (long)(PercentMainProtected * (cache.Maximum - cache.WindowMaximum));

        long hits = (long)int.MaxValue + 1;
        cache.Climber.sample.previousHitRate = 0.50;
        cache.Climber.sample.windowHits = 0;
        cache.Climber.sample.misses = hits;
        cache.Climber.sample.hits = hits;

        long stride = RoundHalfUp(StepPercent * cache.Maximum);
        long protectedMaximum = cache.MainProtectedMaximum;
        long windowMaximum = cache.WindowMaximum;
        cache.Climb();

        // The transfer applies as much of the stride as resident entries cover and gives the
        // remainder back into the climber's adjustment as the multi-cycle carry-over, so the applied
        // movement plus the carried remainder always equals the probe's entry stride.
        var density = (DensityClimber)cache.Climber.tier;
        long moved = cache.WindowMaximum - windowMaximum;
        AssertThat(WalkOf(density).down).IsFalse();
        AssertThat(moved).IsAtLeast(0);
        AssertThat(moved + cache.Climber.Adjustment()).IsEqualTo(stride);
        AssertThat(cache.MainProtectedMaximum).IsEqualTo(protectedMaximum - moved);
    }

    [Fact]
    public void Adapt_LargeCache_ClimbAppliesDownProbeStride()
    {
        // The mirror: a dead sample past the midpoint arms a down probe, and climb() shrinks the
        // window by the entry stride through the transfer path
        var cache = Probe.Of(WeightedCache(Full * 10));
        cache.SetMaximum(2 * DensityThreshold);
        // enough weight to pass the half of the maximum that releases the climber
        for (int i = 0; i < ((DensityThreshold / 10) + 10); i++)
        {
            cache.Cache.Put(i, "v" + i);
        }
        cache.Cache.CleanUp();
        cache.WindowMaximum = (cache.Maximum * 7) / 8;
        cache.MainProtectedMaximum = (long)(PercentMainProtected * (cache.Maximum - cache.WindowMaximum));

        long misses = (long)int.MaxValue + 1;
        cache.Climber.sample.previousHitRate = 0.50;
        cache.Climber.sample.windowHits = 0;
        cache.Climber.sample.misses = misses;
        cache.Climber.sample.hits = 0;

        long stride = RoundHalfUp(StepPercent * cache.Maximum);
        long protectedMaximum = cache.MainProtectedMaximum;
        long windowMaximum = cache.WindowMaximum;
        cache.Climb();

        // as above: the applied shrink plus the carried remainder equals the down stride
        var density = (DensityClimber)cache.Climber.tier;
        long moved = windowMaximum - cache.WindowMaximum;
        AssertThat(WalkOf(density).down).IsTrue();
        AssertThat(moved).IsAtLeast(0);
        AssertThat(moved - cache.Climber.Adjustment()).IsEqualTo(stride);
        AssertThat(cache.MainProtectedMaximum).IsEqualTo(protectedMaximum + moved);
    }

    [Fact]
    public void Adapt_DecreaseWindow_TransferCapsAtThreshold()
    {
        // A single cycle transfers at most QueueTransferThreshold nodes; the unfulfilled quota is
        // given back to the maxima and carried in the climber's adjustment for the next cycle
        var cache = Probe.Of(SizeCache(Full));
        cache.SetMaximum(8 * DensityThreshold);
        cache.WindowMaximum = cache.Maximum / 2;
        cache.MainProtectedMaximum = (long)(PercentMainProtected * (cache.Maximum - cache.WindowMaximum));
        for (int i = 0; i < (QueueTransferThreshold + 100); i++)
        {
            cache.Cache.Put(i, "v" + i);
        }
        cache.Cache.CleanUp();

        long windowMaximum = cache.WindowMaximum;
        lock (cache.EvictionLock)
        {
            cache.Climber.CarryOver(-(QueueTransferThreshold + 500));
            cache.DecreaseWindow();
        }

        AssertThat(cache.WindowMaximum).IsEqualTo(windowMaximum - QueueTransferThreshold);
        AssertThat(cache.Climber.Adjustment()).IsEqualTo(-500);
    }

    [Fact]
    public void Adapt_IncreaseWindow_TransferCapsAtThreshold()
    {
        // The mirror: growing the window demotes at most QueueTransferThreshold candidates from the
        // main space per cycle, carrying the remainder
        var cache = Probe.Of(SizeCache(Full));
        cache.SetMaximum(8 * DensityThreshold);
        cache.WindowMaximum = 100;
        cache.MainProtectedMaximum = (long)(PercentMainProtected * (cache.Maximum - cache.WindowMaximum));
        for (int i = 0; i < (QueueTransferThreshold + 200); i++)
        {
            cache.Cache.Put(i, "v" + i);
        }
        cache.Cache.CleanUp();

        long windowMaximum = cache.WindowMaximum;
        lock (cache.EvictionLock)
        {
            cache.Climber.CarryOver(QueueTransferThreshold + 500);
            cache.IncreaseWindow();
        }

        AssertThat(cache.WindowMaximum).IsEqualTo(windowMaximum + QueueTransferThreshold);
        AssertThat(cache.Climber.Adjustment()).IsEqualTo(500);
    }

    [Fact]
    public void Adapt_LargeCache_ShrinkClampsAtWindowFloor()
    {
        // A window near its floor with a shrink signal: the full proportional step would drive the
        // window below its signal-capable floor, so the step is clamped to land no lower than the
        // floor. The window earns enough hits to stay above the starvation bar, so this is a density
        // decision rather than a probe.
        var cache = Probe.Of(FullCache(weighted: false));
        cache.SetMaximum(2 * DensityThreshold);
        cache.Sketch.EnsureCapacity(cache.Maximum);
        long floor = RoundHalfUp(WindowFloorFraction * cache.Maximum);
        cache.WindowMaximum = floor + 100;
        cache.MainProtectedMaximum = (long)(PercentMainProtected * (cache.Maximum - cache.WindowMaximum));
        var density = (DensityClimber)cache.Climber.tier;

        long hits = (long)int.MaxValue + 1;
        cache.Climber.sample.windowHits = (hits >>> 8);
        cache.Climber.sample.previousHitRate = 0.50;
        cache.Climber.sample.misses = hits;
        cache.Climber.sample.hits = hits;

        long windowMaximum = cache.WindowMaximum;
        DetermineAdjustment(cache);

        AssertThat(density.walk).IsNull();
        AssertThat(cache.Climber.Adjustment()).IsLessThan(0);
        AssertThat(windowMaximum + cache.Climber.Adjustment()).IsAtLeast(floor);
    }

    [Fact]
    public void Adapt_LargeCache_ProbesOutOfStarvedFloor()
    {
        // A floor-sized window earning ~nothing next to an earning main region is the density
        // signal's blind corner: rather than hold a possibly-pinned position, the climber probes
        // upward with the goal-driven walk.
        var cache = Probe.Of(FullCache(weighted: false));
        cache.SetMaximum(2 * DensityThreshold);
        cache.Sketch.EnsureCapacity(cache.Maximum);
        long floor = RoundHalfUp(WindowFloorFraction * cache.Maximum);
        cache.WindowMaximum = floor;
        cache.MainProtectedMaximum = (long)(PercentMainProtected * (cache.Maximum - cache.WindowMaximum));

        long hits = (long)int.MaxValue + 1;
        cache.Climber.sample.previousHitRate = 0.50;
        cache.Climber.sample.windowHits = 0;
        cache.Climber.sample.misses = hits;
        cache.Climber.sample.hits = hits;

        DetermineAdjustment(cache);

        var density = (DensityClimber)cache.Climber.tier;
        AssertThat(cache.Climber.Adjustment()).IsGreaterThan(0);
        AssertThat(WalkOf(density).samples).IsEqualTo(1);
        AssertThat(WalkOf(density).down).IsFalse();
    }

    [Fact]
    public void Adapt_LargeCache_LiftsBelowFloorWindow()
    {
        // The initial window (1% of the maximum) starts below the signal-capable floor (2%); a shrink
        // signal must not wedge it there. The clamp lifts a below-floor window up to the floor.
        var cache = Probe.Of(FullCache(weighted: false));
        cache.SetMaximum(2 * DensityThreshold);
        cache.Sketch.EnsureCapacity(cache.Maximum);
        long floor = RoundHalfUp(WindowFloorFraction * cache.Maximum);
        cache.WindowMaximum = floor / 2;
        cache.MainProtectedMaximum = (long)(PercentMainProtected * (cache.Maximum - cache.WindowMaximum));
        var density = (DensityClimber)cache.Climber.tier;

        long hits = (long)int.MaxValue + 1;
        cache.Climber.sample.windowHits = (hits >>> 8);
        cache.Climber.sample.previousHitRate = 0.50;
        cache.Climber.sample.misses = hits;
        cache.Climber.sample.hits = hits;

        long windowMaximum = cache.WindowMaximum;
        DetermineAdjustment(cache);

        AssertThat(density.walk).IsNull();
        AssertThat(cache.Climber.Adjustment()).IsGreaterThan(0);
        AssertThat(windowMaximum + cache.Climber.Adjustment()).IsAtMost(floor);
        AssertThat(windowMaximum + cache.Climber.Adjustment()).IsAtLeast(floor - 1);
    }

    [Fact]
    public void Adapt_LargeCache_ResizeResetsProbeState()
    {
        // Changing the maximum re-seeds the climber; stale probe state must not carry across.
        var cache = Probe.Of(FullCache(weighted: false));
        cache.SetMaximum(2 * DensityThreshold);
        var density = (DensityClimber)cache.Climber.tier;
        density.walk = new Walk(density.starvation, isAudit: false, down: false, baseWindow: 1024,
            baseRequestCount: 1000, baseHitRate: 0.5, baseSmoothedRate: 0.0, baseProbationDensity: 0.0);
        density.refractoryLeft = 7;
        cache.Climber.CarryOver(-280_000);
        cache.Climber.sample.previousHitRate = 0.80;
        density.starvation.rung = ProbeBackoffMax;

        cache.SetMaximum(4 * DensityThreshold);

        var resetDensity = (DensityClimber)cache.Climber.tier;
        AssertThat(resetDensity.walk).IsNull();
        AssertThat(resetDensity.refractoryLeft).IsEqualTo(0);
        AssertThat(cache.Climber.Adjustment()).IsEqualTo(0);
        AssertThat(cache.Climber.sample.previousHitRate).IsEqualTo(0.0);
        AssertThat(resetDensity.starvation.rung).IsEqualTo(ProbeBackoffInitial);
    }

    [Fact]
    public void SetMaximum_Unchanged_KeepsClimberState()
    {
        // Re-applying the current maximum is a no-op, so it must not re-seed the climber mid-walk
        var cache = Probe.Of(FullCache(weighted: false));
        cache.SetMaximum(2 * DensityThreshold);
        var density = (DensityClimber)cache.Climber.tier;
        density.refractoryLeft = 7;

        cache.SetMaximum(2 * DensityThreshold);

        AssertThat(cache.Climber.tier).IsSameInstanceAs(density);
        AssertThat(density.refractoryLeft).IsEqualTo(7);
    }

    [Fact]
    public void Adapt_LargeCache_HugeMaximumStaysQuiescent()
    {
        // 4x a near-long.MaxValue maximum would overflow into a negative period; the saturated
        // multiply keeps the gate positive so an idle cache never fabricates samples.
        var cache = Probe.Of(FullCache(weighted: false));
        cache.SetMaximum(long.MaxValue);
        cache.Sketch.EnsureCapacity(1024);
        var density = (DensityClimber)cache.Climber.tier;

        DetermineAdjustment(cache);

        AssertThat(density.walk).IsNull();
        AssertThat(cache.Climber.Adjustment()).IsEqualTo(0);
        AssertThat(double.IsNaN(cache.Climber.tier.step.size)).IsFalse();
    }

    [Fact]
    public void Adapt_LargeCache_PreInitResetsWindowHits()
    {
        // Window hits drained before the sketch initializes must not leak into the first real sample,
        // where they would exceed the sample's hits and poison the density arithmetic.
        var cache = Probe.Of(SizeCache(Full));
        cache.SetMaximum(2 * DensityThreshold);
        AssertThat(cache.Sketch.IsNotInitialized).IsTrue();
        cache.Climber.sample.windowHits = 5_000_000;
        cache.Climber.sample.misses = 100;
        cache.Climber.sample.hits = 100;

        cache.Climb();

        AssertThat(cache.Climber.sample.windowHits).IsEqualTo(0);
        AssertThat(cache.Climber.sample.hits).IsEqualTo(0);
    }

    [Fact]
    public void Adapt_PartiallyFilled_HoldsWindow()
    {
        // A sketch sized before the cache fills (as an initial-capacity hint would size it, emulated
        // here since Espresso initializes the sketch lazily) must not release the climber over a cache that has
        // not begun to evict, on a sample period compressed to the hint.
        var cache = Probe.Of(SizeCache(8192));
        cache.Sketch.EnsureCapacity(256);
        long windowMaximum = cache.WindowMaximum;

        int residents = (int)(cache.Maximum / 2) - 1;
        for (int i = 0; i < residents; i++)
        {
            cache.Cache.Put(i, "v");
            cache.Cache.GetIfPresent(i);
            cache.Cache.GetIfPresent(i / 2);
        }
        cache.Cache.CleanUp();

        AssertThat(cache.Cache.EstimatedSize()).IsEqualTo(residents);
        AssertThat((long)cache.Sketch.sampleSize).IsLessThan(cache.Maximum);
        AssertThat(cache.WindowMaximum).IsEqualTo(windowMaximum);
        cache.AssertValid();
    }

    [Fact]
    public void Adapt_Filled_ClimbsWindow()
    {
        // The hold releases where the lazy sketch initialization already sat, so the same cache
        // adapts once it has filled.
        var cache = Probe.Of(SizeCache(8192));
        cache.Sketch.EnsureCapacity(256);
        long windowMaximum = cache.WindowMaximum;

        for (int i = 0; i < (int)(2 * cache.Maximum); i++)
        {
            cache.Cache.Put(i, "v");
            cache.Cache.GetIfPresent(i);
            cache.Cache.GetIfPresent(i / 2);
        }
        cache.Cache.CleanUp();

        AssertThat(cache.Cache.EstimatedSize()).IsEqualTo(cache.Maximum);
        AssertThat(cache.WindowMaximum).IsNotEqualTo(windowMaximum);
        cache.AssertValid();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adapt_SmallCache_PostponesAdjustmentAsStepDecays(bool weighted)
    {
        var cache = Probe.Of(FullCache(weighted));
        AssertThat(cache.Maximum).IsAtMost(SlowAdaptThreshold);
        PrepareForAdaption(cache, recencyBias: false);

        // Decay the step size to half its initial magnitude, which doubles the effective sample
        // period. Filling the sample buffer to only the base sample size should not trigger an
        // adjustment because the climber is waiting for more requests.
        double initialStep = StepPercent * cache.Maximum;
        cache.Climber.tier.step.size = Math.CopySign(initialStep / 2, cache.Climber.tier.step.size);

        int baseSampleSize = cache.Sketch.sampleSize;
        long windowMaximum = cache.WindowMaximum;
        long protectedMaximum = cache.MainProtectedMaximum;

        cache.Climber.sample.previousHitRate = 0.80;
        cache.Climber.sample.misses = (baseSampleSize / 2);
        cache.Climber.sample.hits = (baseSampleSize - cache.Climber.sample.misses);
        cache.Climb();

        // Adjustment should have been postponed: counters preserved, sizes unchanged.
        AssertThat(cache.Climber.sample.hits).IsGreaterThan(0);
        AssertThat(cache.WindowMaximum).IsEqualTo(windowMaximum);
        AssertThat(cache.MainProtectedMaximum).IsEqualTo(protectedMaximum);
    }

    [Fact]
    public void Adapt_IncreaseWindow_NegativeTransientOvershootTolerated()
    {
        // Pins the telescoping-race tolerance: a transiently negative policyWeight inflates the
        // transfer quota, so the caps over-shift beyond the command and the remainder is carried
        // over. The caps are policy targets that the controller coerces back.
        var cache = Probe.Of(FullCache(weighted: true));
        lock (cache.EvictionLock)
        {
            var node = cache.ProbationDeque.PeekFirst!;
            node.PolicyWeight = -3;
            long windowSize = cache.WindowWeightedSize;
            long windowMaximum = cache.WindowMaximum;
            long protectedMaximum = cache.MainProtectedMaximum;

            cache.Climber.CarryOver(1);
            cache.IncreaseWindow();

            // quota inflates 1 -> 4; the next candidate (weight 10) exceeds it, so the walk stops and
            // the inflated remainder is given back and carried over
            AssertThat(node.InWindow).IsTrue();
            AssertThat(cache.Climber.Adjustment()).IsEqualTo(4);
            AssertThat(cache.WindowMaximum).IsEqualTo(windowMaximum - 3);
            AssertThat(cache.MainProtectedMaximum).IsEqualTo(protectedMaximum + 3);
            AssertThat(cache.WindowWeightedSize).IsEqualTo(windowSize - 3);

            // settle the transient as the late UpdateTask would (the coercion the design relies on)
            node.PolicyWeight += 13;
            cache.WindowWeightedSize += 13;
            AssertThat(cache.EvictFromWindow()).IsNotNull();
            cache.Climber.CarryOver(0);
        }
    }

    [Fact]
    public void Adapt_DecreaseWindow_NegativeTransientOvershootTolerated()
    {
        // The decrease-side mirror of the tolerated over-shift: the commanded shrink nets a grow
        // and carries the inflated remainder, coerced back by subsequent samples.
        var cache = Probe.Of(FullCache(weighted: true));
        lock (cache.EvictionLock)
        {
            // a 10-weight entry cannot fit the 1% window, so plant the transient's deque state
            var node = cache.ProbationDeque.PeekFirst!;
            AssertThat(cache.ProbationDeque.Remove(node)).IsTrue();
            AssertThat(cache.WindowDeque.OfferLast(node)).IsTrue();
            node.MakeWindow();
            node.PolicyWeight = -3;
            long windowSize = cache.WindowWeightedSize;
            long windowMaximum = cache.WindowMaximum;
            long protectedMaximum = cache.MainProtectedMaximum;

            cache.Climber.CarryOver(-1);
            cache.DecreaseWindow();

            AssertThat(node.InMainProbation).IsTrue();
            AssertThat(cache.Climber.Adjustment()).IsEqualTo(-4);
            AssertThat(cache.WindowMaximum).IsEqualTo(windowMaximum + 3);
            AssertThat(cache.MainProtectedMaximum).IsEqualTo(protectedMaximum - 3);
            AssertThat(cache.WindowWeightedSize).IsEqualTo(windowSize + 3);

            // settle the transient as the late UpdateTask would (the coercion the design relies on)
            node.PolicyWeight += 13;
            cache.WindowWeightedSize -= 3;
            cache.Climber.CarryOver(0);
        }
    }

    /* --------------- Helpers --------------- */

    /// <summary>Rounds half up: <c>floor(x + 0.5)</c>, unlike banker's rounding.</summary>
    private static long RoundHalfUp(double x) => Common.ToLongSaturating(Math.Floor(x + 0.5));

    private static ICache<int, string> WeightedCache(long maximumWeight)
        => Cache.NewBuilder<int, string>()
            .MaximumWeight(maximumWeight)
            .Weigher((_, _) => 10)
            .Executor(DirectExecutor.Instance)
            .Build();

    /// <summary>
    /// A full cache: 50 entries of a 50-entry cache, or
    /// of a 500-weight cache whose entries weigh 10 each.
    /// </summary>
    private static ICache<int, string> FullCache(bool weighted)
    {
        var cache = weighted ? WeightedCache(Full * 10) : SizeCache(Full);
        for (int i = 0; i < Full; i++)
        {
            cache.Put(i, "v" + i);
        }
        cache.CleanUp();
        return cache;
    }

    /// <summary>Fills the cache and reads every entry so that the main space is promoted to protected.</summary>
    private static void PromoteAll(Probe cache, int count)
    {
        for (int i = 0; i < count; i++)
        {
            cache.Cache.Put(i, "v" + i);
        }
        for (int i = 0; i < count; i++)
        {
            Assert.NotNull(cache.Cache.GetIfPresent(i));
        }
        cache.Cache.CleanUp();
    }

    /// <summary>Runs the climber's sample against an initialized sketch, without applying the transfer.</summary>
    private static void DetermineAdjustment(Probe cache)
    {
        cache.Climber.DetermineAdjustment(cache.Maximum, cache.WindowMaximum,
            cache.MainProtectedMaximum, cache.Sketch.sampleSize);
    }

    /// <summary>The walk in flight, asserting that there is one; never hold it across an adaptation.</summary>
    private static Walk WalkOf(DensityClimber density)
    {
        var walk = density.walk;
        AssertThat(walk).IsNotNull();
        return walk!;
    }

    private static void PrepareForAdaption(Probe cache, bool recencyBias)
    {
        long maximum = cache.Maximum;
        cache.Climber.tier.step.size = (recencyBias ? 1 : -1) * Math.Abs(cache.Climber.tier.step.size);
        cache.WindowMaximum = (long)(0.5 * maximum);
        cache.MainProtectedMaximum = (long)(PercentMainProtected * (maximum - cache.WindowMaximum));

        // Fill window and main spaces
        cache.Cache.InvalidateAll();
        for (int i = 0; i < Full; i++)
        {
            cache.Cache.Put(i, "v" + i);
        }
        ReadAll(cache);
        ReadAll(cache);

        // Espresso's read buffer holds these reads until maintenance; drain them so the main
        // protected region is populated as a small, frequently-draining buffer would leave it
        cache.Cache.CleanUp();
    }

    private static void Adapt(Probe cache, int sampleSize)
    {
        cache.Climber.sample.previousHitRate = 0.80;
        cache.Climber.sample.misses = (sampleSize / 2);
        cache.Climber.sample.hits = (sampleSize - cache.Climber.sample.misses);
        lock (cache.EvictionLock)
        {
            cache.Climb();
        }

        // Fill main protected space
        ReadAll(cache);
        cache.Cache.CleanUp();
    }

    private static void ReadAll(Probe cache)
    {
        foreach (int key in cache.Keys())
        {
            Assert.NotNull(cache.Cache.GetIfPresent(key));
        }
    }

    private sealed class FakeTicker
    {
        private long _nanos;
        public long Read() => _nanos;
        public void Advance(TimeSpan by) => _nanos += by.Ticks * 100L;
    }

    /// <summary>White-box access to the <c>BoundedLocalCache</c> behind an async cache.</summary>
    private sealed class AsyncLocal
    {
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Type Type = typeof(BoundedLocalCache<int, Task<string>>);

        public readonly BoundedLocalCache<int, Task<string>> Cache;

        private AsyncLocal(IAsyncCache<int, string> cache)
        {
            Cache = (BoundedLocalCache<int, Task<string>>)typeof(LocalAsyncCache<int, string>)
                .GetField("Cache", Flags)!.GetValue(cache)!;
        }

        public static AsyncLocal Of(IAsyncCache<int, string> cache) => new(cache);

        public FrequencySketch Sketch => (FrequencySketch)Type.GetField("_sketch", Flags)!.GetValue(Cache)!;

        public ConcurrentHashMap<int, Node<int, Task<string>>> Data
            => (ConcurrentHashMap<int, Node<int, Task<string>>>)Type.GetField("_data", Flags)!.GetValue(Cache)!;
    }

    /// <summary>
    /// White-box access to a <c>BoundedLocalCache</c>'s eviction policy state. The climber is exposed
    /// internally; the region sizes and the policy methods the climb drives are private, so they are
    /// reached by reflection, as the other white-box cache tests do.
    /// </summary>
    private sealed class Probe
    {
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Type Type = typeof(BoundedLocalCache<int, string>);

        public readonly ICache<int, string> Cache;
        public readonly BoundedLocalCache<int, string> Blc;

        private Probe(ICache<int, string> cache)
        {
            Cache = cache;
            Blc = (BoundedLocalCache<int, string>)cache;
        }

        public static Probe Of(ICache<int, string> cache) => new(cache);

        private static FieldInfo Field(string name)
            => Type.GetField(name, Flags) ?? throw new InvalidOperationException($"field {name} not found");

        private object? Call(string name, params object?[] args)
        {
            MethodInfo method = Type.GetMethod(name, Flags)
                ?? throw new InvalidOperationException($"method {name} not found");
            try
            {
                return method.Invoke(Blc, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        private long Get(string name) => (long)Field(name).GetValue(Blc)!;

        private void Set(string name, long value) => Field(name).SetValue(Blc, value);

        public WindowClimber Climber => Blc.Climber!;
        public FrequencySketch Sketch => (FrequencySketch)Field("_sketch").GetValue(Blc)!;
        public object EvictionLock => Field("_evictionLock").GetValue(Blc)!;
        public int DrainStatus => (int)Field("_drainStatus").GetValue(Blc)!;

        public long Maximum => Get("_maximum");
        public long WeightedSize => Get("_weightedSize");
        public long WindowMaximum { get => Get("_windowMaximum"); set => Set("_windowMaximum", value); }
        public long WindowWeightedSize
        {
            get => Get("_windowWeightedSize");
            set => Set("_windowWeightedSize", value);
        }
        public long MainProtectedMaximum
        {
            get => Get("_mainProtectedMaximum");
            set => Set("_mainProtectedMaximum", value);
        }
        public long MainProtectedWeightedSize => Get("_mainProtectedWeightedSize");

        public AccessOrderDeque<Node<int, string>> WindowDeque => Deque("_windowDeque");
        public AccessOrderDeque<Node<int, string>> ProbationDeque => Deque("_probationDeque");
        public AccessOrderDeque<Node<int, string>> ProtectedDeque => Deque("_protectedDeque");

        private AccessOrderDeque<Node<int, string>> Deque(string name)
            => (AccessOrderDeque<Node<int, string>>)Field(name).GetValue(Blc)!;

        public void Climb() => Call("Climb");
        public void IncreaseWindow() => Call("IncreaseWindow");
        public void DecreaseWindow() => Call("DecreaseWindow");
        public void DemoteFromMainProtected() => Call("DemoteFromMainProtected");
        public void SetMaximum(long maximum) => Call("SetMaximum", maximum);
        public void Maintenance() => Call("Maintenance", new object?[] { null });
        public void OnAccess(Node<int, string> node, Access access) => Call("OnAccess", node, access);
        public Node<int, string>? EvictFromWindow() => (Node<int, string>?)Call("EvictFromWindow");

        public List<int> Keys()
        {
            var keys = new List<int>();
            lock (EvictionLock)
            {
                foreach (var node in WindowDeque) keys.Add(node.Key);
                foreach (var node in ProbationDeque) keys.Add(node.Key);
                foreach (var node in ProtectedDeque) keys.Add(node.Key);
            }
            return keys;
        }

        /// <summary>
        /// The climber's invariants plus the cache-side checks:
        /// the partition is legal and a pending sample never exceeds the period that would consume it.
        /// </summary>
        public void AssertValid()
        {
            lock (EvictionLock)
            {
                long maximum = Maximum;
                AssertInvariants(Climber, maximum);
                AssertThat(WindowMaximum).IsAtLeast(0);
                AssertThat(MainProtectedMaximum).IsAtLeast(0);
                AssertThat(WindowMaximum + MainProtectedMaximum).IsAtMost(maximum);
                AssertThat(WeightedSize).IsAtMost(maximum);

                if (Sketch.IsNotInitialized)
                {
                    return;
                }
                long requestCount = Climber.sample.hits + Climber.sample.misses;
                long effectiveSampleSize = Sketch.sampleSize;
                // Mirror the adaptive sample-period gate in WindowClimber.DetermineAdjustment
                if (maximum > DensityThreshold)
                {
                    long period = SampleMultiplier * maximum;
                    if ((period / SampleMultiplier) != maximum)
                    {
                        period = long.MaxValue;
                    }
                    effectiveSampleSize = Math.Min(period, Sketch.sampleSize);
                }
                else if ((maximum > 0) && (maximum <= SlowAdaptThreshold))
                {
                    double initialStep = StepPercent * maximum;
                    double magnitude = Math.Max(initialStep / SlowAdaptRatioCap,
                        Math.Abs(Climber.tier.step.size));
                    double ratio = Math.Max(1.0, Math.Min(SlowAdaptRatioCap, initialStep / magnitude));
                    effectiveSampleSize = Common.ToLongSaturating(effectiveSampleSize * ratio);
                }
                AssertThat(requestCount).IsLessThan(effectiveSampleSize);
            }
        }
    }
}
