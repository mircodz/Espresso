using System;
using Espresso.Internal;
using Xunit;
using static Espresso.Internal.WindowClimber;
using static Espresso.Internal.WindowClimber.Anchor;
using static Espresso.Internal.WindowClimber.AuditClock;
using static Espresso.Internal.WindowClimber.DensityClimber;
using static Espresso.Internal.WindowClimber.Ladder;
using static Espresso.Internal.WindowClimber.Rates;
using static Espresso.Internal.WindowClimber.Reading;
using static Espresso.Internal.WindowClimber.Step;
using static Espresso.Internal.WindowClimber.Walk;
using static Espresso.Tests.FluentAssert;

namespace Espresso.Tests;

/// <summary>
/// The white-box tests for the window climber's tiers and probe machine. The cache applies the
/// returned {@code adjustment} by transferring entries between the regions, so these drive
/// {@link WindowClimber#determineAdjustment} directly with the caller's evolving window size;
/// {@code BoundedLocalCacheTest}'s adapt tests cover the transfer plumbing.
/// </summary>
public sealed class WindowClimberTest
{
  private const long Maximum = 8192;
  private const double Stride = StepPercent * Maximum;
  private const double Floor = WindowFloorFraction * Maximum;

  /// <summary>Rounds half up: <c>floor(x + 0.5)</c> as a long, unlike banker's rounding.</summary>
  private static long RoundHalfUp(double x) => Common.ToLongSaturating(Math.Floor(x + 0.5));

  /* --------------- Seeding --------------- */

  [Fact]
  public void Resized_SeedsByTier() {
    var climber = new WindowClimber();
    climber.Resized(512);
    AssertThat(climber.tier.step.size).IsEqualTo(StepPercent * 512);

    climber.Resized(Maximum);
    var density = (DensityClimber) climber.tier;
    AssertThat(climber.tier.step.size).IsEqualTo(-Stride);
    AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffInitial);
    // every (re)size re-arms the cold-start calibration audit
    AssertThat(density.auditClock.waitSamples).IsEqualTo(AuditWaitFirst);
  }

  [Fact]
  public void Resized_ResetsProbeState() {
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    InjectWalk(density, /* isAudit= */ false, /* down= */ false,
        /* baseWindow= */ 1024, /* baseHitRate= */ 0.5, /* baseSmoothedRate= */ 0.5).samples = 5;
    density.refractoryLeft = 5;
    climber.CarryOver(100);

    climber.Resized(Maximum);
    var resetDensity = (DensityClimber) climber.tier;
    AssertThat(resetDensity.walk).IsNull();
    AssertThat(climber.Adjustment()).IsEqualTo(0);
    AssertThat(resetDensity.refractoryLeft).IsEqualTo(0);
    AssertThat(resetDensity.starvation.rung).IsEqualTo(ProbeBackoffInitial);
  }

  [Fact]
  public void Resized_ClearsTheSample() {
    // a resize discards the partial sample: counts earned under the old geometry must not seed
    // the first decision under the new one
    var climber = MakeClimber();
    climber.RecordHit(/* inWindow= */ true, /* inProbation= */ false);
    climber.RecordHit(/* inWindow= */ false, /* inProbation= */ true);
    climber.RecordMiss();
    climber.sample.previousHitRate = 0.5;

    climber.Resized(Maximum);
    AssertThat(climber.sample.hits).IsEqualTo(0);
    AssertThat(climber.sample.misses).IsEqualTo(0);
    AssertThat(climber.sample.windowHits).IsEqualTo(0);
    AssertThat(climber.sample.probationHits).IsEqualTo(0);
    AssertThat(climber.sample.previousHitRate).IsEqualTo(0.0);
  }

  [Fact]
  public void DiscardSample_KeepsTheCrossSampleRate() {
    // a sample gathered below half occupancy says nothing about the split, so the first decision
    // after the gap compares against the rate from before it rather than against zero
    var climber = MakeClimber();
    climber.sample.previousHitRate = 0.6;
    climber.sample.hits += 8192;
    climber.sample.misses += 8192;

    climber.DiscardSample(Maximum, /* sketchSampleSize= */ 16_384);
    AssertThat(climber.sample.hits).IsEqualTo(0);
    AssertThat(climber.sample.misses).IsEqualTo(0);
    AssertThat(climber.sample.previousHitRate).IsEqualTo(0.6);
  }

  /* --------------- Sampling --------------- */

  [Fact]
  public void SamplePeriod_TierBoundary_IsTheDensityThreshold() {
    // At the threshold the reactive climber runs on the sketch's period; one entry above it the
    // density climber runs on Min(4·maximum, sketch). A 16,400-request feed sits between the
    // two periods, so it decides above the boundary and holds at it. The tier gate, exact.
    var reactive = new WindowClimber();
    reactive.Resized(DensityThreshold);
    reactive.sample.hits += 8192;
    reactive.sample.misses += 8208;
    reactive.DetermineAdjustment(DensityThreshold, /* windowMaximum= */ 41,
        ProtectedMax(DensityThreshold, 41), /* sketchSampleSize= */ 16_488);
    AssertThat(reactive.Adjustment()).IsEqualTo(0);
    AssertThat(reactive.sample.hits).IsEqualTo(8192);

    var density = new WindowClimber();
    density.Resized(DensityThreshold + 1);
    density.sample.hits += 5000;
    density.sample.misses += 5000;
    density.DetermineAdjustment(DensityThreshold + 1, /* windowMaximum= */ 41,
        ProtectedMax(DensityThreshold + 1, 41), /* sketchSampleSize= */ 16_488);
    AssertThat(density.Adjustment()).IsEqualTo(0);

    density.sample.hits += 3192;
    density.sample.misses += 3208;
    density.DetermineAdjustment(DensityThreshold + 1, /* windowMaximum= */ 41,
        ProtectedMax(DensityThreshold + 1, 41), /* sketchSampleSize= */ 16_488);
    AssertThat(density.Adjustment()).IsNotEqualTo(0);
    AssertThat(density.sample.hits).IsEqualTo(0);
  }

  [Fact]
  public void SamplePeriod_LargeTier_CompletesExactlyAtFourMaxima() {
    // the density period is 4·maximum when the sketch's sample is larger: one request short
    // holds the carry-over, the exact count decides
    var climber = MakeClimber();
    climber.sample.hits += 16_384;
    climber.sample.misses += 16_383;
    climber.DetermineAdjustment(Maximum, /* windowMaximum= */ 2000,
        ProtectedMax(Maximum, 2000), /* sketchSampleSize= */ 100_000);
    AssertThat(climber.Adjustment()).IsEqualTo(0);
    AssertThat(climber.sample.hits).IsEqualTo(16_384);

    climber.sample.misses += 1;
    climber.DetermineAdjustment(Maximum, /* windowMaximum= */ 2000,
        ProtectedMax(Maximum, 2000), /* sketchSampleSize= */ 100_000);
    AssertThat(climber.Adjustment()).IsNotEqualTo(0);
    AssertThat(climber.sample.hits).IsEqualTo(0);
  }

  [Fact]
  public void SamplePeriod_HugeMaximum_StaysQuiescent() {
    // 4·maximum overflows near long.MaxValue; the guard saturates it rather than letting a
    // negative period close every sample, so the sketch's sample size bounds the period and the
    // climber idles
    long maximum = long.MaxValue - 100;
    var climber = new WindowClimber();
    climber.Resized(maximum);
    climber.sample.hits += 500_000;
    climber.sample.misses += 500_000;
    climber.DetermineAdjustment(maximum, /* windowMaximum= */ maximum / 100,
        /* mainProtectedMaximum= */ maximum / 2, /* sketchSampleSize= */ int.MaxValue);
    AssertThat(climber.Adjustment()).IsEqualTo(0);
    AssertThat(climber.sample.hits).IsEqualTo(500_000);
  }

  [Fact]
  public void SamplePeriod_SmallTier_StretchesAsTheStepDecays() {
    // the slow-adapt tier trades cadence for information: the period grows by the step's decay
    // from its initial magnitude, capped at the ratio limit (a zeroed step on a zero-capacity
    // cache degenerates to ratio one rather than poisoning the arithmetic)
    var climber = new WindowClimber();
    climber.Resized(512);
    climber.sample.hits += 500;
    climber.sample.misses += 500;
    climber.DetermineAdjustment(512, /* windowMaximum= */ 5,
        ProtectedMax(512, 5), /* sketchSampleSize= */ 1000);
    AssertThat(climber.sample.hits).IsEqualTo(0);

    climber.tier.step.size = 8.0;
    climber.sample.hits += 2000;
    climber.sample.misses += 1999;
    climber.DetermineAdjustment(512, /* windowMaximum= */ 5,
        ProtectedMax(512, 5), /* sketchSampleSize= */ 1000);
    AssertThat(climber.sample.hits).IsEqualTo(2000);

    climber.sample.misses += 1;
    climber.DetermineAdjustment(512, /* windowMaximum= */ 5,
        ProtectedMax(512, 5), /* sketchSampleSize= */ 1000);
    AssertThat(climber.sample.hits).IsEqualTo(0);

    var empty = new WindowClimber();
    empty.Resized(0);
    empty.tier.step.size = 0.0;
    empty.sample.misses += 1000;
    empty.DetermineAdjustment(0, /* windowMaximum= */ 0,
        /* mainProtectedMaximum= */ 0, /* sketchSampleSize= */ 1000);
    AssertThat(empty.sample.misses).IsEqualTo(0);
  }

  [Fact]
  public void StepDecayRate_Boundary_IsTheSlowAdaptThreshold() {
    // 512 decays at the slow rate, 513 at the standard rate. The small-cache tuning's edge
    var slow = new WindowClimber();
    slow.Resized(512);
    double slowSeed = slow.tier.step.size;
    ReactiveSampleAt(slow, 512, /* hits= */ 500, /* misses= */ 501);
    ReactiveSampleAt(slow, 512, /* hits= */ 505, /* misses= */ 496);
    AssertThat(slow.tier.step.size).IsWithin(1.0e-9).Of(0.995 * slowSeed);

    var stock = new WindowClimber();
    stock.Resized(513);
    double stockSeed = stock.tier.step.size;
    ReactiveSampleAt(stock, 513, /* hits= */ 500, /* misses= */ 501);
    ReactiveSampleAt(stock, 513, /* hits= */ 505, /* misses= */ 496);
    AssertThat(stock.tier.step.size).IsWithin(1.0e-9).Of(StepDecayRate * stockSeed);
  }

  [Fact]
  public void ReactiveStep_FlatSampleKeepsDirection_ExactRestartReseeds() {
    // a hit rate change of exactly zero keeps the bold driver's direction, and a change of
    // exactly the restart threshold re-anneals the decayed step to its full magnitude
    var climber = new WindowClimber();
    climber.Resized(2048);
    double seed = climber.tier.step.size;
    ReactiveSampleAt(climber, 2048, /* hits= */ 500, /* misses= */ 501);
    ReactiveSampleAt(climber, 2048, /* hits= */ 500, /* misses= */ 501);
    AssertThat(climber.Adjustment()).IsEqualTo((long) seed);
    AssertThat(climber.tier.step.size).IsWithin(1.0e-9).Of(StepDecayRate * seed);

    ReactiveSampleAt(climber, 2048, /* hits= */ 560, /* misses= */ 441);
    AssertThat(climber.Adjustment()).IsEqualTo((long) (StepDecayRate * seed));
    AssertThat(climber.tier.step.size).IsEqualTo(seed);
  }

  /** Completes one full reactive-tier sample at the given maximum (sketch period 1001). */
  private static void ReactiveSampleAt(WindowClimber climber, long maximum, int hits, int misses) {
    for (int i = 0; i < hits; i++) {
      climber.RecordHit(/* inWindow= */ false, /* inProbation= */ false);
    }
    for (int i = 0; i < misses; i++) {
      climber.RecordMiss();
    }
    climber.DetermineAdjustment(maximum, /* windowMaximum= */ 20,
        ProtectedMax(maximum, 20), /* sketchSampleSize= */ 1001);
  }

  /* --------------- Arming --------------- */

  [Fact]
  public void ArmProbe_WindowBlind_ProbesUp() {
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 1024,
        /* windowHits= */ 0, /* mainHits= */ 500, /* misses= */ 500);

    var walk = WalkOf(density);
    AssertThat(walk.down).IsFalse();
    AssertThat(adjustment).IsEqualTo((long) Stride);
    AssertThat(walk.baseWindow).IsEqualTo(1024);
    AssertThat(walk.baseHitRate).IsEqualTo(0.5);
    AssertThat(walk.samples).IsEqualTo(1);
  }

  [Fact]
  public void ArmProbe_MainBlind_NeverProbes() {
    // a starved main beside a large window arms nothing, since the equilibrium audit owns that
    // terrain; the sample falls through to the steering law with main priced at its floor
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 7000,
        /* windowHits= */ 500, /* mainHits= */ 0, /* misses= */ 500);

    double windowDensity = 500 / 7000.0;
    double mainFloor = (0.125 * 4) / (Maximum - 7000.0);
    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo(
        (long) (Math.Log(windowDensity / mainFloor) * DensityGain * Maximum));
  }

  [Fact]
  public void ArmProbe_DeadSample_ProbesAwayFromNearerBound() {
    var wide = MakeClimber();
    var wideDensity = (DensityClimber) wide.tier;
    long down = Sample(wide, /* windowMax= */ 5000,
        /* windowHits= */ 0, /* mainHits= */ 0, /* misses= */ 1000);
    AssertThat(WalkOf(wideDensity).down).IsTrue();
    AssertThat(down).IsEqualTo((long) -Stride);

    var narrow = MakeClimber();
    var narrowDensity = (DensityClimber) narrow.tier;
    long up = Sample(narrow, /* windowMax= */ 3000,
        /* windowHits= */ 0, /* mainHits= */ 0, /* misses= */ 1000);
    AssertThat(WalkOf(narrowDensity).down).IsFalse();
    AssertThat(up).IsEqualTo((long) Stride);
  }

  [Fact]
  public void ArmProbe_StarvedLargeRegion_DoesNotProbe() {
    // a scan-filled main earning nothing is visible to density; probing would shrink the one
    // region that is working
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 4000,
        /* windowHits= */ 1000, /* mainHits= */ 0, /* misses= */ 0);
    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsGreaterThan(0);

    var other = MakeClimber();
    var otherDensity = (DensityClimber) other.tier;
    long shrink = Sample(other, /* windowMax= */ 3000,
        /* windowHits= */ 0, /* mainHits= */ 1000, /* misses= */ 0);
    AssertThat(otherDensity.walk).IsNull();
    AssertThat(shrink).IsLessThan(0);
  }

  [Fact]
  public void ArmProbe_Refractory_TicksAndHolds() {
    // a refractory sample was just classified as one the density signal cannot be trusted on, so
    // the climber holds; falling through to a steering step was the dead-phase rider's delivery
    // vehicle (a handful of window hits in a blank sample authorized the maximum step)
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.refractoryLeft = 3;
    long adjustment = Sample(climber, /* windowMax= */ 1024,
        /* windowHits= */ 0, /* mainHits= */ 500, /* misses= */ 500);

    AssertThat(density.walk).IsNull();
    AssertThat(density.refractoryLeft).IsEqualTo(2);
    AssertThat(adjustment).IsEqualTo(0);
  }

  [Fact]
  public void ArmProbe_Refractory_LiftsABelowFloorWindow() {
    // the refractory hold still honors the below-floor lift: on a workload whose every sample
    // is blind, a bare hold wedges the initial 1% window under the signal-capable floor for
    // the life of the run
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.refractoryLeft = 3;
    long adjustment = Sample(climber, /* windowMax= */ 82,
        /* windowHits= */ 0, /* mainHits= */ 500, /* misses= */ 500);

    AssertThat(density.walk).IsNull();
    AssertThat(density.refractoryLeft).IsEqualTo(2);
    AssertThat(adjustment).IsEqualTo((long) (Floor - 82));
  }

  [Fact]
  public void ArmProbe_Refractory_DueClockAuditsRatherThanHolding() {
    // the hold commands nothing and is spent only on blind samples, so a corner that never clears
    // serves its whole backoff standing still while the clock says the position is due to be
    // re-tested. armProbe_refractory_ticksAndHolds and _liftsABelowFloorWindow are the not-due
    // half, where it still holds and still lifts
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.refractoryLeft = 3;
    density.auditClock.stillSamples = AuditWaitFirst;
    long adjustment = Sample(climber, /* windowMax= */ 1024,
        /* windowHits= */ 0, /* mainHits= */ 500, /* misses= */ 500);

    AssertThat(WalkOf(density).isAudit).IsTrue();
    AssertThat(density.refractoryLeft).IsEqualTo(3);
    AssertThat(adjustment).IsEqualTo((long) -Stride);
  }

  [Fact]
  public void UndoProbe_AuditRetreat_LeavesTheStarvationRefractoryAlone() {
    // the refractory is the starvation machine's backoff, armed by its own endings; an audit that
    // preempted a hold and crashed hands the corner back with the hold where it was, not re-armed
    // to the whole rung, which deferred the corner's next probe for an audit that was not the
    // probe's doing (a starvation walk's undo still arms it, as the walkStep_ tests pin)
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.refractoryLeft = 3;
    density.auditClock.stillSamples = AuditWaitFirst;
    Sample(climber, /* windowMax= */ 1024,
        /* windowHits= */ 0, /* mainHits= */ 500, /* misses= */ 500);
    AssertThat(WalkOf(density).isAudit).IsTrue();

    long undo = Sample(climber, /* windowMax= */ 512,
        /* windowHits= */ 0, /* mainHits= */ 300, /* misses= */ 700);
    AssertThat(density.walk).IsNull();
    AssertThat(undo).IsEqualTo(1024 - 512);
    AssertThat(density.refractoryLeft).IsEqualTo(3);
    AssertThat(density.auditClock.waitSamples).IsEqualTo(ProbeBackoffInitial);
  }

  [Fact]
  public void ArmProbe_Refractory_DueClockBelowTheFloor_StillClearsIt() {
    // pre-empting the hold must not cost the below-floor lift the hold would have made: the
    // window sits under the floor, so the audit is refused a downward direction and its entry
    // stride carries it clear
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.refractoryLeft = 3;
    density.auditClock.stillSamples = AuditWaitFirst;
    long adjustment = Sample(climber, /* windowMax= */ 82,
        /* windowHits= */ 0, /* mainHits= */ 500, /* misses= */ 500);

    AssertThat(WalkOf(density).isAudit).IsTrue();
    AssertThat(82 + adjustment).IsAtLeast((long) Floor);
  }

  [Fact]
  public void DensityStep_StarvedOpposingRegion_BoundsTheStep() {
    // a blank main is imputed a density of an eighth of the starvation bar spread over its
    // capacity (the strongest claim the sample can honestly make) rather than the epsilon's
    // ~20 nat balloon,
    // so a handful of window hits cannot authorize the maximum step
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 2049,
        /* windowHits= */ 40, /* mainHits= */ 0, /* misses= */ 960);

    double windowDensity = 40 / 2049.0;
    double mainFloor = (0.125 * 4) / (Maximum - 2049);
    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo((long) (Math.Log(windowDensity / mainFloor)
        * DensityGain * Maximum));
    AssertThat(adjustment).IsLessThan((long) (MaxStepFraction * Maximum));
  }

  [Fact]
  public void DensityStep_CommonModeScaling_Cancels() {
    // The steering signal's central property: a workload phase that scales both regions'
    // earnings together (the cross-sample hit rate weather) cancels in the density ratio, so
    // two samples with the same split quality at different traffic intensities command the
    // same step. This is what the reactive climber's cross-sample comparison lacks.
    var quiet = MakeClimber();
    long small = Sample(quiet, /* windowMax= */ 2000,
        /* windowHits= */ 100, /* mainHits= */ 300, /* misses= */ 600);

    var loud = MakeClimber();
    long large = Sample(loud, /* windowMax= */ 2000,
        /* windowHits= */ 400, /* mainHits= */ 1200, /* misses= */ 2400);

    AssertThat(small).IsEqualTo(DensityStep(/* windowMax= */ 2000,
        /* windowHits= */ 100, /* mainHits= */ 300));
    AssertThat(small).IsNotEqualTo(0);
    AssertThat(large).IsEqualTo(small);
  }

  [Fact]
  public void DensityStep_StarvedInteriorWindow_SteersInsteadOfProbing() {
    // a starved region that is not small is not blind: the position is mid-range, the other
    // region is measurable, and the floored steering error walks the window down without the
    // probe machinery (probes are reserved for the corners where nobody can see)
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 3000,
        /* windowHits= */ 0, /* mainHits= */ 600, /* misses= */ 400);

    double windowFloor = (0.125 * 4) / 3000;
    double mainDensity = 600 / (double) (Maximum - 3000);
    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo(
        (long) (Math.Log(windowFloor / mainDensity) * DensityGain * Maximum));
  }

  [Fact]
  public void DensityStep_FloorCrossingShrink_ClampsAtTheFloor() {
    // a sighted shrink command that would cross the floor is clamped to land exactly on it
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 200,
        /* windowHits= */ 4, /* mainHits= */ 900, /* misses= */ 96);

    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo((long) (Floor - 200));
  }

  [Fact]
  public void BlindCorner_PositionBoundaries_WindowQuarterPointOnly() {
    // a starved window is blind only while it holds at most a quarter of the cache, exact at the
    // edge; a starved main is never blind at any window, since the equilibrium audit owns that
    // terrain and the probe it once armed there confirmed onto positions main could not price
    var atEdge = MakeClimber();
    var atEdgeDensity = (DensityClimber) atEdge.tier;
    Sample(atEdge, /* windowMax= */ 2048,
        /* windowHits= */ 0, /* mainHits= */ 600, /* misses= */ 400);
    AssertThat(atEdgeDensity.walk).IsNotNull();

    var pastEdge = MakeClimber();
    var pastEdgeDensity = (DensityClimber) pastEdge.tier;
    Sample(pastEdge, /* windowMax= */ 2049,
        /* windowHits= */ 0, /* mainHits= */ 600, /* misses= */ 400);
    AssertThat(pastEdgeDensity.walk).IsNull();

    var mainAtCorner = MakeClimber();
    var mainAtCornerDensity = (DensityClimber) mainAtCorner.tier;
    Sample(mainAtCorner, /* windowMax= */ 6144,
        /* windowHits= */ 600, /* mainHits= */ 0, /* misses= */ 400);
    AssertThat(mainAtCornerDensity.walk).IsNull();

    var mainPastCorner = MakeClimber();
    var mainPastCornerDensity = (DensityClimber) mainPastCorner.tier;
    Sample(mainPastCorner, /* windowMax= */ 6600,
        /* windowHits= */ 600, /* mainHits= */ 0, /* misses= */ 400);
    AssertThat(mainPastCornerDensity.walk).IsNull();
  }

  [Fact]
  public void ArmProbe_DeadSampleDirection_SplitsAtTheMidpoint() {
    // a dead sample probes away from the nearer bound, deciding at exactly half the capacity
    var down = MakeClimber();
    var downDensity = (DensityClimber) down.tier;
    Sample(down, /* windowMax= */ 4096,
        /* windowHits= */ 0, /* mainHits= */ 0, /* misses= */ 1000);
    AssertThat(WalkOf(downDensity).down).IsTrue();

    var up = MakeClimber();
    var upDensity = (DensityClimber) up.tier;
    Sample(up, /* windowMax= */ 4095,
        /* windowHits= */ 0, /* mainHits= */ 0, /* misses= */ 1000);
    AssertThat(WalkOf(upDensity).down).IsFalse();
  }

  [Fact]
  public void DensityStep_BalancedShares_HoldsFirm() {
    // At the proportional balance point the error is ~zero and the window rests. Flat terrain
    // costs nothing because convergence and holding still are the same mechanism (the reactive
    // climber's restart rule keeps it moving on the same terrain, at a measured 10.5pp cost)
    foreach (long windowMax in new long[] {1000, 2000, 4000, 6000}) {
      var climber = MakeClimber();
      var density = (DensityClimber) climber.tier;
      long adjustment = SteadySample(climber, windowMax, /* hitRate= */ 0.8);
      AssertThat(density.walk).IsNull();
      AssertThat(Math.Abs(adjustment)).IsAtMost(2);
    }
  }

  [Fact]
  public void DensityStep_ExtremeImbalance_SaturatesAtMaximumStep() {
    // a sighted region beside a blank one still saturates the step cap: the steering floor
    // prices "earned nothing" low, not infinitely low, and the cap bounds the displacement
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 164,
        /* windowHits= */ 1000, /* mainHits= */ 0, /* misses= */ 0);

    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo((long) (MaxStepFraction * Maximum));
  }

  [Fact]
  public void StarvationBar_KnifeEdge_ProbesOnlyBelowTheBar() {
    // The whisper family's exact mechanism: window earnings at the starvation bar are "sighted"
    // (no blind corner, no probe, since the equilibrium audit owns that case), while one hit fewer
    // is starved and arms the probe machinery. The bar's floor is 4 for these short samples.
    var sighted = MakeClimber();
    var sightedDensity = (DensityClimber) sighted.tier;
    long held = Sample(sighted, /* windowMax= */ 164,
        /* windowHits= */ 4, /* mainHits= */ 800, /* misses= */ 196);
    AssertThat(sightedDensity.walk).IsNull();
    AssertThat(held).IsAtMost(0);

    var starved = MakeClimber();
    var starvedDensity = (DensityClimber) starved.tier;
    long probed = Sample(starved, /* windowMax= */ 164,
        /* windowHits= */ 3, /* mainHits= */ 800, /* misses= */ 197);
    AssertThat(WalkOf(starvedDensity).down).IsFalse();
    AssertThat(probed).IsEqualTo((long) Stride);
  }

  /* --------------- Guard rail and audits --------------- */

  [Fact]
  public void GuardRail_SustainedShortfall_VetoesBackToAnchor() {
    // the anchor forms at a steady, well-earning position; when the density arm walks away and
    // the smoothed rate settles measurably below the anchor's claim, the veto returns there and
    // parks. The climber refuses to remain somewhere worse than a place it has already been
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < 30; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
    }
    AssertThat(density.anchor.window).IsEqualTo(2000);
    AssertThat(density.anchor.rate).IsWithin(1.0e-6).Of(0.70);

    long windowMax = 4000;
    bool vetoed = false;
    for (int i = 0; i < 40; i++) {
      long adjustment = SteadySample(climber, windowMax, /* hitRate= */ 0.68);
      if (density.anchor.returning || density.anchor.held) {
        vetoed = true;
        AssertThat(adjustment).IsAtMost(0);
        windowMax += adjustment;
      }
      if (density.anchor.held && !density.anchor.returning) {
        break;
      }
    }
    AssertThat(vetoed).IsTrue();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.IsAt(windowMax, WindowClimber.Reading.StableBand(Maximum))).IsTrue();

    long held = SteadySample(climber, windowMax, /* hitRate= */ 0.68);
    AssertThat(held).IsEqualTo(0);
  }

  [Fact]
  public void GuardRail_OnAnchor_RateClaimTracksReality() {
    // standing on the anchor re-syncs its claim to the live measurement, so a workload whose
    // rate genuinely fell does not leave a stale claim that vetoes forever
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < 30; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
    }
    for (int i = 0; i < 40; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.66);
    }
    AssertThat(density.anchor.window).IsEqualTo(2000);
    AssertThat(density.anchor.rate).IsWithin(0.005).Of(0.66);
    AssertThat(density.anchor.held).IsFalse();
  }

  [Fact]
  public void GuardRail_CrashScaleShift_NearAnchor_StandsDown() {
    // a crash-scale swing at the anchor's own position is a claim tested and found wrong: the
    // guard discards it and re-learns rather than dragging the window to a stale reference
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < 30; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
    }
    SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.55);
    AssertThat(density.anchor.IsPlanted()).IsFalse();
    AssertThat(density.anchor.held).IsFalse();
    AssertThat(density.anchor.shortfallStreak).IsEqualTo(0);
  }

  [Fact]
  public void GuardRail_CrashScaleShift_NearAnchor_ReseedsTheGoalMetric() {
    // the swing that discards a claim also invalidates the reference the claim was measured
    // against: an EMA smoothed across the shift is mostly the regime that just ended, and the
    // next sample re-plants the anchor from it, so the layer would carry a claim the workload
    // can no longer produce and no walk could clear at any position
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < 30; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
    }
    SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.20);
    AssertThat(density.anchor.IsPlanted()).IsFalse();
    AssertThat(density.rates.smoothed).IsWithin(1.0e-6).Of(0.20);
    AssertThat(density.rates.deviation).IsWithin(1.0e-6).Of(DeviationSeed);

    // the re-plant on the next sample claims the new regime's rate, not a blend of both
    SteadySample(climber, /* windowMax= */ 5000, /* hitRate= */ 0.20);
    AssertThat(density.anchor.window).IsEqualTo(5000);
    AssertThat(density.anchor.rate).IsWithin(0.01).Of(0.20);
  }

  [Fact]
  public void GuardRail_CrashScaleShift_FarFromAnchor_KeepsAnchor() {
    // a crash far from the anchor is not evidence against it. That shape is typically the
    // controller's own retreat crossing a band edge, exactly what the veto exists to catch, so
    // neither the claim nor the reference it was measured against is thrown away
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < 30; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
    }
    SteadySample(climber, /* windowMax= */ 5000, /* hitRate= */ 0.55);
    AssertThat(density.anchor.window).IsEqualTo(2000);
    AssertThat(density.anchor.held).IsFalse();
    AssertThat(density.anchor.shortfallStreak).IsEqualTo(0);
    AssertThat(density.rates.smoothed).IsWithin(1.0e-6).Of(0.67);
  }

  [Fact]
  public void AuditClock_RateSwings_DoNotResetStillness() {
    // stillness is a property of the position, not the rate: a periodic crash-scale swing at a
    // motionless window must not hold the audit clock below its arming wait forever
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    bool armed = false;
    for (int i = 0; i < (2 * AuditWaitInitial); i++) {
      double hitRate = ((i % 2) == 0) ? 0.70 : 0.60;
      SteadySample(climber, /* windowMax= */ 2000, hitRate);
      if (density.walk != null) {
        armed = true;
        break;
      }
    }
    AssertThat(armed).IsTrue();
    AssertThat(WalkOf(density).isAudit).IsTrue();
  }

  [Fact]
  public void AuditClock_FirstSampleAfterAResize_IsStillWhereverItRests() {
    // nothing may move the window before the first sample closes, so that sample is a stillness
    // observation wherever the resize left it; the unrecorded position is not a place to compare
    // against, and comparing it makes the calibration audit's schedule depend on the geometry
    var atSplit = MakeClimber();
    var atSplitDensity = (DensityClimber) atSplit.tier;
    SteadySample(atSplit, /* windowMax= */ 82, /* hitRate= */ 0.70);
    AssertThat(atSplitDensity.auditClock.stillSamples).IsEqualTo(1);

    var elsewhere = MakeClimber();
    var elsewhereDensity = (DensityClimber) elsewhere.tier;
    SteadySample(elsewhere, /* windowMax= */ 2000, /* hitRate= */ 0.70);
    AssertThat(elsewhereDensity.auditClock.stillSamples).IsEqualTo(1);
  }

  [Fact]
  public void AuditClock_Overflow_RemainsDue() {
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    var clock = density.auditClock;
    long band = WindowClimber.Reading.StableBand(Maximum);
    clock.Tick(/* windowMax= */ 2000, band);

    // Accelerate to the boundary; stationary samples must leave the audit due without wrapping.
    clock.stillSamples = int.MaxValue - 1;
    AssertThat(clock.IsDue()).IsTrue();
    clock.Tick(/* windowMax= */ 2000, band);
    AssertThat(clock.stillSamples).IsEqualTo(int.MaxValue);

    clock.Tick(/* windowMax= */ 2000, band);
    AssertThat(clock.stillSamples).IsEqualTo(int.MaxValue);
    AssertThat(clock.IsDue()).IsTrue();

    clock.Tick(/* windowMax= */ 4000, band);
    AssertThat(clock.stillSamples).IsEqualTo(int.MaxValue - 1);
    AssertThat(clock.IsDue()).IsTrue();
  }

  [Fact]
  public void GuardRail_NoAnchorSeedWhileProbing() {
    // a probe's transient window paired with an EMA earned elsewhere is a phantom claim; the
    // seed waits until the walk resolves
    var climber = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    density.anchor.window = -1;
    density.rates.smoothed = 0.5;
    // the watched region stays under the adjudication bar, so the walk continues
    long walking = Sample(climber, /* windowMax= */ 3000,
        /* windowHits= */ 5, /* mainHits= */ 515, /* misses= */ 480);
    AssertThat(walking).IsNotEqualTo(0);
    AssertThat(density.walk).IsNotNull();
    AssertThat(density.anchor.window).IsEqualTo(-1);
  }

  [Fact]
  public void Audit_FreshPark_ShieldLastsItsWholeSchedule() {
    // The shield's LENGTH, not merely its existence. A confirm parks for one initial audit wait,
    // and the park must survive crash-scale weather for exactly that many samples: a shorter
    // shield releases a validated position into the first squall, and one that never ages holds
    // a position the workload has left. The sibling test spends the shield by hand, which pins
    // only that it covers more than one sample.
    var climber = ArmAudit(/* windowMax= */ 2000);
    var density = (DensityClimber) climber.tier;
    long walked = 2000 + climber.Adjustment();
    for (int i = 0; (density.walk != null) && (i < ProbeWalkBudget); i++) {
      walked += SteadySample(climber, walked, /* hitRate= */ 0.74);
    }
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.freshLeft).IsEqualTo(AuditWaitInitial);

    // alternating crash-scale swings would stand the anchor down on every sample but for the
    // shield, so the release lands on the sample that spends its last count
    int releasedAt = -1;
    for (int i = 1; i <= (2 * AuditWaitInitial); i++) {
      SteadySample(climber, walked, /* hitRate= */ ((i % 2) == 0) ? 0.80 : 0.74);
      if (!density.anchor.held) {
        releasedAt = i;
        break;
      }
    }
    AssertThat(releasedAt).IsEqualTo(AuditWaitInitial);
    AssertThat(density.anchor.freshLeft).IsEqualTo(0);
  }

  [Fact]
  public void AuditClock_Restart_MakesTheNextAuditWaitOutTheStillnessAgain() {
    // Arming an audit restarts the stillness run. Asserting the wait variable does not pin that:
    // a long-still position carries its whole run into the walk, so an audit that ends quickly
    // would be due again immediately and the position is re-tested continuously rather than on
    // a schedule. Driven at the standard wait, where the run is long enough for the residue to
    // show; the cold-start wait is too short to tell the two apart.
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitInitial;
    int armedAt = -1;
    for (int i = 1; i <= (4 * AuditWaitInitial); i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
      if (density.walk != null) {
        armedAt = i;
        break;
      }
    }
    AssertThat(armedAt).IsAtLeast(AuditWaitInitial);
    // the arm restarts the run and this sample's own tick is the first of the new one
    AssertThat(density.auditClock.stillSamples).IsEqualTo(1);

    // a crash-scale drop ends the walk on its first sample, so almost none of the run is spent
    SteadySample(climber, /* windowMax= */ 2000 + climber.Adjustment(), /* hitRate= */ 0.40);
    AssertThat(density.walk).IsNull();

    // the next audit waits out a fresh run rather than inheriting the one the arm consumed
    int rearmedAt = -1;
    for (int i = 1; i <= (4 * AuditWaitInitial); i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.40);
      if (density.walk != null) {
        rearmedAt = i;
        break;
      }
    }
    AssertThat(rearmedAt).IsAtLeast(ProbeBackoffInitial);
  }

  [Fact]
  public void Audit_ArmsAtQuietEquilibrium_AndBacksOffOnFailure() {
    // a sighted equilibrium is re-tested by the probe machinery: the FIRST audit is the
    // cold-start calibration probe and arms after a short stillness wait (a sighted false
    // equilibrium otherwise pins for the full initial wait that a short trace mostly spends
    // motionless); an unconfirmed audit then retries on the ladder's cadence and doubles its
    // own clock so a settled workload is re-examined ever more rarely
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    int armedAt = -1;
    for (int i = 0; i < 80; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
      if (density.walk != null) {
        armedAt = i;
        break;
      }
    }
    AssertThat(armedAt).IsAtLeast(AuditWaitFirst - 1);
    AssertThat(armedAt).IsLessThan(AuditWaitInitial - 1);
    var walk = WalkOf(density);
    AssertThat(walk.isAudit).IsTrue();
    AssertThat(walk.down).IsTrue();
    AssertThat(climber.Adjustment()).IsEqualTo((long) -Stride);

    // The audit is adjudicated by the goal metric, not density: it walks its whole budget while
    // the smoothed rate fails to clear the anchor's margin, then undoes in full with the retry
    // paced by the deepening ladder.
    long walked = 2000 + climber.Adjustment();
    for (int i = 0; i < (2 * ProbeWalkBudget); i++) {
      walked += SteadySample(climber, walked, /* hitRate= */ 0.70);
      if ((density.walk == null) && (density.undoRemaining == 0)) {
        break;
      }
    }
    AssertThat(density.walk).IsNull();
    AssertThat(walked).IsEqualTo(2000);
    AssertThat(density.auditClock.waitSamples).IsEqualTo(density.audit.rung);
    AssertThat(density.auditClock.waitSamples).IsEqualTo(2 * ProbeBackoffInitial);
    AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffInitial);
  }

  [Fact]
  public void Audit_SustainedImprovement_ConfirmsAndParks() {
    // an audit that finds a sustained improvement (a streak of raw samples above the frozen
    // reference) keeps the position and parks there: density disagreed with the position by
    // construction, so steering would walk the paid-for escape home; the audit clock owns
    // re-exploration from the parked equilibrium
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < AuditWaitInitial + 2; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
      if (density.walk != null) {
        break;
      }
    }
    AssertThat(WalkOf(density).isAudit).IsTrue();
    density.anchor.held = true;

    long walked = 2000 + climber.Adjustment();
    for (int i = 0; i < ProbeWalkBudget; i++) {
      walked += SteadySample(climber, walked, /* hitRate= */ 0.74);
      if (density.walk == null) {
        break;
      }
    }
    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(walked).IsNotEqualTo(2000);
    AssertThat(density.auditClock.waitSamples).IsEqualTo(AuditWaitInitial);
    AssertThat(density.starvation.rung).IsEqualTo(1);

    // the goal-validated position is the guard rail's anchor at once and the park holds it
    AssertThat(density.anchor.window).IsEqualTo(walked);
    AssertThat(density.anchor.rate).IsWithin(1.0e-9).Of(density.rates.smoothed);
    long held = SteadySample(climber, walked, /* hitRate= */ 0.74);
    AssertThat(held).IsEqualTo(0);
  }

  [Fact]
  public void Audit_ConfirmSample_TakesNoSteeringStep() {
    // the confirm's own sample must not fall through to a density step: density disagrees with
    // the validated position by construction, so a parting step would displace the park off the
    // position the walk just paid for, leaving the anchor defending a claim the window does not
    // occupy; the same walk under a skewed region split pins the confirm sample's return at zero
    var climber = ArmAudit(/* windowMax= */ 2000);
    var density = (DensityClimber) climber.tier;
    long walked = 2000 + climber.Adjustment();
    long confirmStep = -1;
    for (int i = 0; (density.walk != null) && (i < (2 * ProbeWalkBudget)); i++) {
      confirmStep = Sample(climber, walked,
          /* windowHits= */ 600, /* mainHits= */ 140, /* misses= */ 260);
      walked += confirmStep;
    }
    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(confirmStep).IsEqualTo(0);
    AssertThat(density.anchor.window).IsEqualTo(walked);
  }

  [Fact]
  public void ProbeEnding_StarvationConfirm_DoesNotPark() {
    // a density-agreed confirm needs no park: steering agrees with the position it validated,
    // so the climber stays live (cheap re-probing is load-bearing on phase alternation); nor
    // does it touch the audit clock, which is the audit layer's own state
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 5, /* mainHits= */ 900, /* misses= */ 95);

    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(1);
    AssertThat(density.anchor.held).IsFalse();
    AssertThat(density.anchor.freshLeft).IsEqualTo(0);
    AssertThat(density.auditClock.waitSamples).IsEqualTo(AuditWaitFirst);
    AssertThat(adjustment).IsEqualTo(DensityStep(
        /* windowMax= */ 2000, /* windowHits= */ 5, /* mainHits= */ 900));
  }

  [Fact]
  public void ProbeEnding_StarvationConfirm_LeavesTheAuditClock() {
    // A starvation confirm once reset the audit clock to the standard wait, silently spending
    // the cold-start calibration schedule before the first audit ever ran, and 32 quiet
    // samples is a run long enough for ordinary burstiness to keep breaking, which switched
    // the audit layer off permanently (the position jam's easy regime). The schedule survives
    // the confirm and the calibration audit still arms early from the validated landing.
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var density = (DensityClimber) climber.tier;
    Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 5, /* mainHits= */ 900, /* misses= */ 95);
    AssertThat(density.walk).IsNull();
    AssertThat(density.auditClock.waitSamples).IsEqualTo(AuditWaitFirst);

    int armedAt = -1;
    for (int i = 0; i < AuditWaitInitial; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
      if (density.walk != null) {
        armedAt = i;
        break;
      }
    }
    AssertThat(armedAt).IsAtLeast(AuditWaitFirst - 1);
    AssertThat(armedAt).IsLessThan(AuditWaitInitial - 1);
    AssertThat(WalkOf(density).isAudit).IsTrue();

    // a deep post-failure schedule survives the confirm unchanged as well
    var deep = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var deepDensity = (DensityClimber) deep.tier;
    deepDensity.auditClock.waitSamples = AuditWaitMax;
    Sample(deep, /* windowMax= */ 2000,
        /* windowHits= */ 5, /* mainHits= */ 900, /* misses= */ 95);
    AssertThat(deepDensity.walk).IsNull();
    AssertThat(deepDensity.auditClock.waitSamples).IsEqualTo(AuditWaitMax);
  }

  [Fact]
  public void ProbeEnding_StarvationConfirm_LeavesTheAuditLadder() {
    // The audit's rung and crash evidence are its own ledger: a starvation confirm resets the
    // starvation ladder so re-probing turns cheap, but must not shallow the deep stride and
    // commitment that a searched equilibrium's next re-test earned.
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var density = (DensityClimber) climber.tier;
    density.audit.rung = ProbeBackoffMax;
    density.audit.crashStreak = 1;
    Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 5, /* mainHits= */ 900, /* misses= */ 95);

    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(1);
    AssertThat(density.audit.crashStreak).IsEqualTo(1);
    AssertThat(density.audit.rung).IsEqualTo(ProbeBackoffMax);
  }

  [Fact]
  public void Audit_InterruptedImprovement_FailsTheWalk() {
    // the confirm is a consecutive-sample streak on RAW rates, not a level test on the smoothed
    // rate: a periodic dip below the reference resets it, so scattered above-reference samples
    // whose average clears the bar cannot confirm (the smoothed form confirmed exactly this
    // shape, which is how workload weather bought false confirms)
    var climber = ArmAudit(/* windowMax= */ 2000);
    var density = (DensityClimber) climber.tier;
    double[] cycle = {0.74, 0.74, 0.74, 0.703};
    long walked = 2000 + climber.Adjustment();
    for (int i = 0; (density.walk != null) && (i < (2 * ProbeWalkBudget)); i++) {
      walked += SteadySample(climber, walked, cycle[i % cycle.Length]);
    }
    AssertThat(density.walk).IsNull();
    AssertThat(density.audit.rung).IsEqualTo(2 * ProbeBackoffInitial);
  }

  [Fact]
  public void Audit_WalkThatNeverBeatsItsStart_CannotConfirm() {
    // the confirm's reference is the smoothed rate frozen at arm, and it can be colder than the
    // walk, most sharply for the cold-start calibration audit, where the smoothing still carries
    // the rate the cache earned while filling. A streak against that reference completes on the
    // warm-up ramp alone, so the confirm also requires the walk to have out-earned the sample it
    // started from at least once: here every walk sample clears the reference by a wide margin
    // yet none beats the arming rate, and the walk fails.
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < AuditWaitInitial + 2; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
      if (density.walk != null) {
        break;
      }
    }
    AssertThat(WalkOf(density).isAudit).IsTrue();
    AssertThat(RebaseReference(climber, 0.40).baseHitRate).IsEqualTo(0.70);

    long walked = 2000 + climber.Adjustment();
    for (int i = 0; (density.walk != null) && (i < (2 * ProbeWalkBudget)); i++) {
      walked += SteadySample(climber, walked, /* hitRate= */ 0.69);
    }
    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsFalse();
    AssertThat(density.audit.rung).IsEqualTo(2 * ProbeBackoffInitial);
  }

  [Fact]
  public void Audit_WalkThatMatchesASaturatedStart_StillConfirms() {
    // the gate is inclusive and takes no margin: an arming sample that saturated makes any
    // strictly-greater bar unsatisfiable, which would silently disable every later confirm
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < AuditWaitInitial + 2; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 1.0);
      if (density.walk != null) {
        break;
      }
    }
    AssertThat(WalkOf(density).isAudit).IsTrue();
    AssertThat(RebaseReference(climber, 0.40).baseHitRate).IsEqualTo(1.0);

    long walked = 2000 + climber.Adjustment();
    for (int i = 0; (density.walk != null) && (i < ProbeWalkBudget); i++) {
      walked += SteadySample(climber, walked, /* hitRate= */ 1.0);
    }
    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsTrue();
  }

  [Fact]
  public void Audit_FreshPark_RidesOutCrashWeather() {
    // a just-confirmed park survives crash-scale rate swings for one initial audit wait: on a
    // workload whose per-sample scatter reaches the restart threshold, the stand-down would
    // otherwise release the validated position within a few samples and density re-balloons;
    // after the shield lapses, the stand-down releases as before
    var climber = ArmAudit(/* windowMax= */ 2000);
    var density = (DensityClimber) climber.tier;
    long walked = 2000 + climber.Adjustment();
    for (int i = 0; (density.walk != null) && (i < ProbeWalkBudget); i++) {
      walked += SteadySample(climber, walked, /* hitRate= */ 0.74);
    }
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.window).IsEqualTo(walked);

    long held = SteadySample(climber, walked, /* hitRate= */ 0.80);
    AssertThat(held).IsEqualTo(0);
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.window).IsEqualTo(walked);

    density.anchor.freshLeft = 0;
    SteadySample(climber, walked, /* hitRate= */ 0.74);
    // released: the park no longer holds (the discarded anchor re-seeds at the freed position
    // in the same sample, so the release is observable through the hold alone), and the shield
    // dies with it
    AssertThat(density.anchor.held).IsFalse();
    AssertThat(density.anchor.freshLeft).IsEqualTo(0);
  }

  [Fact]
  public void GuardRail_VetoPark_IsNotCrashShielded() {
    // the fresh-park shield belongs to audit confirms alone: a guard-rail veto's park earned no
    // validation walk, so it carries no shield (a starvation confirm between audits must not
    // leave a stale shield for the veto to inherit; the shield lives and dies with the park). A
    // crash-scale swing on the landing sample is the return's own and waits for the retest,
    // which then releases the park one sample later
    var climber = SeededShortfall();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitInitial;
    for (int i = 0; i < VetoStreak; i++) {
      SteadySample(climber, /* windowMax= */ 1500, /* hitRate= */ 0.66);
    }
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.freshLeft).IsEqualTo(0);

    SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.60);
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.retestClaim).IsWithin(1.0e-6).Of(0.70);

    SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.60);
    AssertThat(density.anchor.held).IsFalse();
    AssertThat(density.anchor.IsPlanted()).IsFalse();
    AssertThat(density.rates.IsUnseeded()).IsTrue();
  }

  [Fact]
  public void Audit_ConfirmUsesReferenceFrozenAtArm() {
    // a mid-walk move of the smoothed rate, or a re-plant of the anchor, must not move the bar the
    // audit is judged against; the reference freezes when the probe arms
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < AuditWaitInitial + 2; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
      if (density.walk != null) {
        break;
      }
    }
    AssertThat(WalkOf(density).isAudit).IsTrue();
    density.rates.smoothed = 0.99;
    density.anchor.rate = 0.99;

    long walked = 2000 + climber.Adjustment();
    bool confirmed = false;
    for (int i = 0; i < ProbeWalkBudget; i++) {
      walked += SteadySample(climber, walked, /* hitRate= */ 0.74);
      if (density.walk == null) {
        confirmed = (density.starvation.rung == 1);
        break;
      }
    }
    AssertThat(confirmed).IsTrue();
  }

  [Fact]
  public void Audit_AfterARegimeShift_ConfirmsAgainstTheNewRegime() {
    // the stale-claim failure mode end to end: with the reference smoothed across a crash-scale shift,
    // the re-planted claim keeps most of the old regime's rate, and since the claim only re-syncs
    // while the window stands on it, one steering step away freezes it. Every later audit then
    // fails at every position, escalating the ladder until the layer stops trying
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < 30; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
    }
    SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.20);

    bool armed = false;
    for (int i = 0; i < (2 * AuditWaitInitial); i++) {
      SteadySample(climber, /* windowMax= */ 5000, /* hitRate= */ 0.20);
      if (density.walk != null) {
        armed = true;
        break;
      }
    }
    AssertThat(armed).IsTrue();
    AssertThat(WalkOf(density).isAudit).IsTrue();
    AssertThat(WalkOf(density).baseSmoothedRate).IsLessThan(0.25);

    // a position the new regime can actually hold clears the bar and is kept
    long walked = 5000 + climber.Adjustment();
    for (int i = 0; i < ProbeWalkBudget; i++) {
      walked += SteadySample(climber, walked, /* hitRate= */ 0.24);
      if (density.walk == null) {
        break;
      }
    }
    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.window).IsEqualTo(walked);
  }

  [Fact]
  public void Audit_AfterAnAwayShift_ConfirmsAgainstTheNewRegime() {
    // the stale claim's away-anchor case end to end: the window rests off the anchor when the
    // regime shifts, so the swing does not test the claim and the guard keeps it, as it must for
    // the terrain's own collapses at a still window that the rail then recovers from. Measured
    // against that claim, an audit that walked to a far better position would fail at budget; the
    // walk is measured against the rate it left instead, and confirms on the new regime
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < 30; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
    }
    for (int i = 0; i < 4; i++) {
      SteadySample(climber, /* windowMax= */ 5000, /* hitRate= */ 0.70);
    }
    SteadySample(climber, /* windowMax= */ 5000, /* hitRate= */ 0.20);
    AssertThat(density.anchor.window).IsEqualTo(2000);
    AssertThat(density.anchor.rate).IsWithin(0.01).Of(0.70);

    // the clock is not due at the shift, as on the witness, so the smoothing has re-learned the
    // regime by the time the audit arms
    density.auditClock.Restart();
    bool armed = false;
    for (int i = 0; i < (2 * AuditWaitInitial); i++) {
      SteadySample(climber, /* windowMax= */ 5000, /* hitRate= */ 0.20);
      if (density.walk != null) {
        armed = true;
        break;
      }
    }
    AssertThat(armed).IsTrue();
    AssertThat(WalkOf(density).isAudit).IsTrue();
    AssertThat(density.anchor.rate).IsWithin(0.01).Of(0.70);
    AssertThat(WalkOf(density).baseSmoothedRate).IsLessThan(0.25);

    // a position the new regime can actually hold clears the bar and is kept
    long walked = 5000 + climber.Adjustment();
    for (int i = 0; i < ProbeWalkBudget; i++) {
      walked += SteadySample(climber, walked, /* hitRate= */ 0.24);
      if (density.walk == null) {
        break;
      }
    }
    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.window).IsEqualTo(walked);
  }

  [Fact]
  public void Audit_RetryAfterConfirm_FlooredAtInitialBackoff() {
    // a confirm resets the starvation ladder to 1, but the audit clock is paced by the audit
    // layer's own rung, which never sinks below the initial refractory. A confirm-cheapened
    // starvation ladder cannot leak a 1-2 sample retry cadence into the audit schedule
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.starvation.rung = 1;
    var walk = InjectWalk(density, /* isAudit= */ true, /* down= */ true,
        /* baseWindow= */ 2000, /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.99);
    walk.samples = ProbeWalkBudget;
    density.rates.smoothed = 0.70;
    climber.sample.previousHitRate = 0.70;
    SteadySample(climber, /* windowMax= */ 1800, /* hitRate= */ 0.70);

    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(1);
    AssertThat(density.audit.rung).IsEqualTo(2 * ProbeBackoffInitial);
    AssertThat(density.auditClock.waitSamples).IsEqualTo(2 * ProbeBackoffInitial);
  }

  [Fact]
  public void Audit_DirectionByPosition_NeedsRoomToStride() {
    // an audit explores toward the farther rail and refuses a direction with less than one
    // stride of room: a walk sent into a nearer wall clamps on its entry stride, stands still
    // for the rest of its budget, and the information-free expiry is priced as a failed
    // experiment, doubling both the refractory ladder and the audit clock
    var low = ArmAudit(/* windowMax= */ 300);
    var lowDensity = (DensityClimber) low.tier;
    AssertThat(WalkOf(lowDensity).down).IsFalse();
    AssertThat(low.Adjustment()).IsEqualTo((long) Stride);

    var atTwiceTheFloor = ArmAudit(/* windowMax= */ 327);
    var atTwiceTheFloorDensity = (DensityClimber) atTwiceTheFloor.tier;
    AssertThat(WalkOf(atTwiceTheFloorDensity).down).IsFalse();

    var interior = ArmAudit(/* windowMax= */ 328);
    var interiorDensity = (DensityClimber) interior.tier;
    AssertThat(WalkOf(interiorDensity).down).IsFalse();

    var lastWithoutRoom = ArmAudit(/* windowMax= */ (long) (Floor + Stride));
    var lastWithoutRoomDensity = (DensityClimber) lastWithoutRoom.tier;
    AssertThat(WalkOf(lastWithoutRoomDensity).down).IsFalse();

    var firstWithRoom = ArmAudit(/* windowMax= */ (long) (Floor + Stride) + 1);
    var firstWithRoomDensity = (DensityClimber) firstWithRoom.tier;
    AssertThat(WalkOf(firstWithRoomDensity).down).IsTrue();

    var high = ArmAudit(/* windowMax= */ 6500);
    var highDensity = (DensityClimber) high.tier;
    AssertThat(WalkOf(highDensity).down).IsTrue();
    AssertThat(high.Adjustment()).IsEqualTo((long) -Stride);
  }

  [Fact]
  public void Audit_Direction_RoomIsMeasuredAgainstTheRungsOwnStride() {
    // the refusal and the walk must price the same move: at the deepest rung a walk strides four
    // times the flat magnitude, so a position with room for the flat stride but not the scaled one
    // has to be refused. Admitting it clamps the entry stride at the floor, and the walk then
    // stands still for its whole budget before an information-free expiry doubles the audit clock
    long windowMax = (long) (Floor + (3 * Stride));

    var deep = ArmAuditAtRung(windowMax, ProbeBackoffMax);
    var deepDensity = (DensityClimber) deep.tier;
    AssertThat(WalkOf(deepDensity).down).IsFalse();

    var shallow = ArmAuditAtRung(windowMax, ProbeBackoffInitial);
    var shallowDensity = (DensityClimber) shallow.tier;
    AssertThat(WalkOf(shallowDensity).down).IsTrue();
    AssertThat(shallow.Adjustment()).IsEqualTo((long) -Stride);
  }

  [Fact]
  public void Audit_Direction_RoomRefusalOverridesAlternation() {
    // near a rail, alternation would point every other audit into the wall; the refusal
    // re-flips it so each audit walks the only informative direction
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    int audits = 0;
    for (int i = 0; (i < 300) && (audits < 2); i++) {
      SteadySample(climber, /* windowMax= */ 500, /* hitRate= */ 0.70);
      var walk = density.walk;
      if ((walk != null) && (walk.samples == 1)) {
        audits++;
        AssertThat(walk.isAudit).IsTrue();
        AssertThat(walk.down).IsFalse();
      }
    }
    AssertThat(audits).IsEqualTo(2);
  }

  [Fact]
  public void Audit_StreakBoundary_ConfirmsAtExactlyFourConsecutive() {
    // pins AuditConfirmStreak's exact boundary: an interrupted opening resets the run, four
    // consecutive above-reference samples then complete it, and the confirm fires on that
    // fourth sample, not one earlier and needing no fifth
    var climber = ArmAudit(/* windowMax= */ 2000);
    var density = (DensityClimber) climber.tier;
    double[] rates = {0.703, 0.74, 0.74, 0.74, 0.74};
    long walked = 2000 + climber.Adjustment();
    for (int i = 0; i < rates.Length; i++) {
      walked += SteadySample(climber, walked, rates[i]);
      if (i < (rates.Length - 1)) {
        AssertThat(density.walk).IsNotNull();
      }
    }
    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsTrue();
  }

  [Fact]
  public void Audit_Direction_CeilingSideRoomRefusal() {
    // the mirror of the floor-side refusal: at an interior position within one stride of the
    // ceiling guard, alternation's up-turn is refused back downward, so every audit walks the
    // only informative direction
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    int audits = 0;
    for (int i = 0; (i < 300) && (audits < 2); i++) {
      SteadySample(climber, /* windowMax= */ 5800, /* hitRate= */ 0.70);
      var walk = density.walk;
      if ((walk != null) && (walk.samples == 1)) {
        audits++;
        AssertThat(walk.isAudit).IsTrue();
        AssertThat(walk.down).IsTrue();
      }
    }
    AssertThat(audits).IsEqualTo(2);
  }

  [Fact]
  public void Audit_CeilingDirection_IsSticky() {
    // a ceiling-adjacent equilibrium is forced downward on every audit. It never alternates,
    // since the wall above it has nothing to test (the caller pins the position, a rail)
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    int audits = 0;
    for (int i = 0; (i < 150) && (audits < 2); i++) {
      SteadySample(climber, /* windowMax= */ 6500, /* hitRate= */ 0.70);
      var walk = density.walk;
      if ((walk != null) && (walk.samples == 1)) {
        audits++;
        AssertThat(walk.isAudit).IsTrue();
        AssertThat(walk.down).IsTrue();
      }
    }
    AssertThat(audits).IsEqualTo(2);
  }

  [Fact]
  public void AuditClock_StillnessToleratesTheOrbit() {
    // held equilibria orbit rather than rest, since the transfer geometry and per-sample noise keep
    // the window moving a little, so stillness is a band: an orbit within it accumulates
    // toward the audit, while a wander beyond it on every sample starves the clock
    var orbiting = MakeClimber();
    var orbitingDensity = (DensityClimber) orbiting.tier;
    bool armed = false;
    for (int i = 0; i < (2 * AuditWaitInitial); i++) {
      long windowMax = ((i % 2) == 0) ? 2000 : 2163;
      SteadySample(orbiting, windowMax, /* hitRate= */ 0.70);
      if (orbitingDensity.walk != null) {
        armed = true;
        break;
      }
    }
    AssertThat(armed).IsTrue();

    var wandering = MakeClimber();
    var wanderingDensity = (DensityClimber) wandering.tier;
    for (int i = 0; i < (2 * AuditWaitInitial); i++) {
      long windowMax = ((i % 2) == 0) ? 2000 : 2164;
      SteadySample(wandering, windowMax, /* hitRate= */ 0.70);
    }
    AssertThat(wanderingDensity.walk).IsNull();
  }

  [Fact]
  public void AuditClock_PeriodicSuperBandMotion_DecaysInsteadOfResetting() {
    // the position jam: a workload that moves the window past the band once per wait held a
    // hard-reset clock at zero forever, so the equilibrium was never re-tested. A moving
    // sample must decay the run instead, letting mostly-still stretches accumulate while the
    // every-sample wander above still starves the clock
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitInitial;
    bool armed = false;
    for (int i = 0; i < (3 * AuditWaitInitial); i++) {
      long windowMax = (((i / 8) % 2) == 0) ? 2000 : 2400;
      SteadySample(climber, windowMax, /* hitRate= */ 0.70);
      if (density.walk != null) {
        armed = true;
        break;
      }
    }
    AssertThat(armed).IsTrue();
    AssertThat(WalkOf(density).isAudit).IsTrue();
  }

  [Fact]
  public void AuditClock_HalfMotionAlternation_NeverAccumulates() {
    // the decay's analytic boundary: motion on half the samples is net-zero drift, so the
    // clock must never accumulate to an arm. The periodic-motion pin above sits on the
    // accumulating side of this line, the every-sample wander beyond it (a steeper decay
    // would move the boundary and un-jam nothing, since partial-motion cadences are not
    // realizable by steering; only this net-zero arithmetic keeps the two pins honest)
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitInitial;
    for (int i = 0; i < (8 * AuditWaitInitial); i++) {
      long windowMax = (((i / 2) % 2) == 0) ? 2000 : 2400;
      SteadySample(climber, windowMax, /* hitRate= */ 0.70);
      AssertThat(density.walk).IsNull();
    }
  }

  [Fact]
  public void Audit_InteriorDirection_AlternatesAcrossAudits() {
    // interior equilibria are searched in both directions across successive audits, so a false
    // equilibrium whose escape lies opposite the first walk is still found
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    long windowMax = 2000;
    bool firstDown = false;
    int audits = 0;
    for (int i = 0; (i < 200) && (audits < 2); i++) {
      var walk = density.walk;
      if ((walk != null) && walk.isAudit && (walk.samples == 1)) {
        audits++;
        if (audits == 1) {
          firstDown = walk.down;
        } else {
          AssertThat(walk.down).IsEqualTo(!firstDown);
        }
      }
      long adjustment = SteadySample(climber, windowMax, /* hitRate= */ 0.70);
      windowMax = Math.Max(164, windowMax + adjustment);
      if ((density.walk == null) && (density.undoRemaining == 0) && !density.anchor.returning) {
        windowMax = 2000;
      }
    }
    AssertThat(audits).IsEqualTo(2);
  }

  [Fact]
  public void Audit_ParksFirstAudit_FollowsTheConfirmedWalk() {
    // A confirm ends a walk on evidence of improvement rather than its exhaustion, so the ground
    // beyond it is the unexplored side: the park's first audit continues that direction, where the
    // alternation would send it back through the ground the walk just covered
    var climber = ConfirmedAuditPark(/* windowMax= */ 5000, /* baseRate= */ 0.70,
        /* walkedRate= */ 0.74);
    var density = (DensityClimber) climber.tier;
    AssertThat(density.anchor.window).IsLessThan(5000L);
    ArmNextAudit(climber, density.anchor.window, /* hitRate= */ 0.74);
    AssertThat(WalkOf(density).down).IsTrue();

    // and the audit after that alternates
    var settled = ConfirmedAuditPark(/* windowMax= */ 5000, /* baseRate= */ 0.70,
        /* walkedRate= */ 0.74);
    var settledDensity = (DensityClimber) settled.tier;
    long parked = settledDensity.anchor.window;
    ArmNextAudit(settled, parked, /* hitRate= */ 0.74);
    for (int i = 0; (i < ProbeWalkBudget) && (settledDensity.walk != null); i++) {
      SteadySample(settled, parked, /* hitRate= */ 0.74);
    }
    AssertThat(settledDensity.walk).IsNull();
    settledDensity.refractoryLeft = 0;
    settledDensity.undoRemaining = 0;
    ArmNextAudit(settled, parked, /* hitRate= */ 0.74);
    AssertThat(WalkOf(settledDensity).down).IsFalse();
  }

  [Fact]
  public void Audit_ParksFirstAudit_AlternatesWhenTheClaimMoved() {
    // a smoothed rate that moved by a restart threshold while the window stood still says the
    // workload moved, and the walk's direction says nothing about the terrain (the trend-misconfirm
    // family): the alternation stands. So does a park that was stood down before its first audit
    var trended = ConfirmedAuditPark(/* windowMax= */ 5000, /* baseRate= */ 0.70,
        /* walkedRate= */ 0.74);
    var trendedDensity = (DensityClimber) trended.tier;
    ArmNextAudit(trended, trendedDensity.anchor.window,
        /* hitRate= */ 0.74 + RestartThreshold + 0.01);
    AssertThat(WalkOf(trendedDensity).down).IsFalse();

    var released = ConfirmedAuditPark(/* windowMax= */ 5000, /* baseRate= */ 0.70,
        /* walkedRate= */ 0.74);
    var releasedDensity = (DensityClimber) released.tier;
    long parked = releasedDensity.anchor.window;
    releasedDensity.anchor.Release();
    ArmNextAudit(released, parked, /* hitRate= */ 0.74);
    AssertThat(WalkOf(releasedDensity).down).IsFalse();
  }

  [Fact]
  public void Audit_CommitmentDepth_BlocksAnImmediateConfirm() {
    // the verdict needs samples along the walk before it says anything, so an audit may not
    // confirm before its committed depth even when every stride clears the frozen reference
    // from the first sample onward
    var climber = ArmAudit(/* windowMax= */ 2000);
    var density = (DensityClimber) climber.tier;
    long walked = 2000 + climber.Adjustment();
    for (int depth = 1; depth <= AuditCommitment; depth++) {
      long adjustment = SteadySample(climber, walked, /* hitRate= */ 0.78);
      walked = Math.Max(164, walked + adjustment);
      if (depth < AuditCommitment) {
        AssertThat(density.walk).IsNotNull();
      }
    }
    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.starvation.rung).IsEqualTo(1);
  }

  [Fact]
  public void Audit_Confirm_ParksAtTheWalksBestSample() {
    // the streak needs four samples above the reference and the walk strides on through all four,
    // so a crest crossed on the way is behind the window by the time the verdict comes in. The
    // confirm is for the position the walk earned most at, and the window returns there: on
    // crestpast the calibration audit crosses the optimum on its first stride and confirms four
    // strides past it, and the position it parks on is what it then defends and re-tests
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    InjectWalk(density, /* isAudit= */ true, /* down= */ false,
        /* baseWindow= */ 2000, /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.40);
    density.rates.smoothed = 0.70;
    climber.sample.previousHitRate = 0.70;
    climber.tier.step.size = Stride;

    double[] rates = {0.80, 0.78, 0.77, 0.76, 0.755, 0.75};
    long confirmStep = 0;
    for (int i = 0; i < rates.Length; i++) {
      confirmStep = SteadySample(climber, /* windowMax= */ 2200 + (200 * i), rates[i]);
    }

    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.window).IsEqualTo(2200);
    // the confirm's own sample commands the return rather than standing where the streak ended,
    // and a return within one step arrives on that sample
    AssertThat(confirmStep).IsEqualTo(2200 - 3200);
    AssertThat(density.anchor.returning).IsFalse();
  }

  [Fact]
  public void Audit_Confirm_SkipsANegativeBestSample() {
    // a transient negative policy weight can report the window below zero, and a streak sample
    // taken there can carry the walk's best rate. The verdict must not send the window to that
    // position, so the audit parks where its streak completed
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < (AuditWaitInitial + 2); i++) {
      SteadySample(climber, /* windowMax= */ 4096, /* hitRate= */ 0.50);
      if (density.walk != null) {
        break;
      }
    }
    AssertThat(WalkOf(density).isAudit).IsTrue();

    Sample(climber, /* windowMax= */ -100, /* windowHits= */ 0, /* mainHits= */ 600,
        /* misses= */ 400);
    for (int i = 0; (i < ProbeWalkBudget) && (density.walk != null); i++) {
      SteadySample(climber, /* windowMax= */ 3000, /* hitRate= */ 0.58);
    }

    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.window).IsEqualTo(3000);
  }

  [Fact]
  public void Audit_Confirm_OnAnImprovingWalk_ParksWhereItStopped() {
    // a walk still improving when its streak completes has its best sample last, so the verdict is
    // the position it stands on, and a walk over a flat plateau has a best sample by noise alone,
    // which the verdict's margin refuses: both park where the walk ended and command no return
    double[][] walks =
        {new[] {0.72, 0.73, 0.74, 0.75, 0.76, 0.80}, new[] {0.75, 0.755, 0.75, 0.752, 0.75, 0.753}};
    foreach (double[] rates in walks) {
      var climber = MakeClimber();
      var density = (DensityClimber) climber.tier;
      InjectWalk(density, /* isAudit= */ true, /* down= */ false,
          /* baseWindow= */ 2000, /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.40);
      density.rates.smoothed = 0.70;
      climber.sample.previousHitRate = 0.70;
      climber.tier.step.size = Stride;

      long confirmStep = 0;
      for (int i = 0; i < rates.Length; i++) {
        confirmStep = SteadySample(climber, /* windowMax= */ 2200 + (200L * i), rates[i]);
      }

      AssertThat(density.walk).IsNull();
      AssertThat(density.anchor.window).IsEqualTo(3200);
      AssertThat(density.anchor.returning).IsFalse();
      AssertThat(confirmStep).IsEqualTo(0);
    }
  }

  [Fact]
  public void Audit_RetreatLandingAtThePark_KeepsTheAnchor() {
    // the retreat that ends a park's audit restores the rate the walk had spent, and that
    // recovery is a crash-scale move at the anchor's own position, where a workload shift
    // discards the claim. It is the machine's own move, so the park survives its own re-test
    var climber = ArmAudit(/* windowMax= */ 2000);
    var density = (DensityClimber) climber.tier;
    density.anchor.held = true;
    density.anchor.freshLeft = 0;
    density.anchor.Plant(2000, /* claimed= */ 0.70);
    long walked = 2000 + climber.Adjustment();

    long undo = SteadySample(climber, walked, /* hitRate= */ 0.50);
    AssertThat(density.walk).IsNull();
    AssertThat(undo).IsEqualTo(2000 - walked);
    AssertThat(density.retreatLeft).IsEqualTo(RetreatCover);

    // the landing sample: the rate returns to what the park earns, a crash-scale step up
    long held = SteadySample(climber, 2000, /* hitRate= */ 0.70);
    AssertThat(held).IsEqualTo(0);
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.window).IsEqualTo(2000);
    AssertThat(density.retreatLeft).IsEqualTo(RetreatCover - 1);

    // and the cover runs out with it: the next crash-scale move is the workload's again
    SteadySample(climber, 2000, /* hitRate= */ 0.70);
    AssertThat(density.retreatLeft).IsEqualTo(0);
    SteadySample(climber, 2000, /* hitRate= */ 0.30);
    AssertThat(density.anchor.IsPlanted()).IsFalse();
  }

  [Fact]
  public void Audit_DeepestRungFailure_DoublesTheWaitToItsCap() {
    // failures below the deepest rung retry on the ladder's cadence (the escalating search);
    // only a failure at the deepest rung (a genuinely searched equilibrium) doubles the
    // audit clock, so a settled workload is re-tested ever more rarely, up to the cap
    int[] expected = {2 * ProbeBackoffMax, 4 * ProbeBackoffMax, AuditWaitMax,
        AuditWaitMax};
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = ProbeBackoffMax;
    foreach (int expectedWait in expected) {
      var walk = InjectWalk(density, /* isAudit= */ true, /* down= */ true,
          /* baseWindow= */ 2000, /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.99);
      walk.samples = ProbeWalkBudget;
      density.rates.smoothed = 0.70;
      climber.sample.previousHitRate = 0.70;
      density.audit.rung = ProbeBackoffMax;
      SteadySample(climber, /* windowMax= */ 1800, /* hitRate= */ 0.70);

      AssertThat(density.walk).IsNull();
      AssertThat(density.auditClock.waitSamples).IsEqualTo(expectedWait);
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
    }
  }

  [Fact]
  public void Audit_DeepestRungCrash_RetriesOnTheLadderCadence() {
    // a lone crash is priced as a workload shift, not a searched failure, so an audit ending in
    // one keeps the ladder's cadence even at the deepest rung. Doubling the clock there would
    // defer the very re-exploration the shift calls for; a first crash aborts on its first
    // below-bar sample (tolerance is the escalated response, not the default)
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = 4 * ProbeBackoffMax;
    var walk = InjectWalk(density, /* isAudit= */ true, /* down= */ true,
        /* baseWindow= */ 2000, /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.99);
    walk.samples = ProbeWalkBudget - 10;
    density.rates.smoothed = 0.70;
    climber.sample.previousHitRate = 0.70;
    density.audit.rung = ProbeBackoffMax;
    SteadySample(climber, /* windowMax= */ 1800, /* hitRate= */ 0.60);

    AssertThat(density.walk).IsNull();
    AssertThat(density.audit.crashStreak).IsEqualTo(1);
    AssertThat(density.audit.rung).IsEqualTo(ProbeBackoffMax);
    AssertThat(density.auditClock.waitSamples).IsEqualTo(ProbeBackoffMax);
  }

  [Fact]
  public void Audit_DeepestRungEscalatedCrash_KeepsTheLadderCadence() {
    // A cycle of crashes escalates the audit's own rung like a completed failure, but the crash
    // never takes the failure's clock doubling. Even escalated, a crash is priced as a
    // workload shift, and deferring the retry beyond the ladder's cadence let three exogenous
    // pulses ratchet the clock to a 128-sample stand-down at the window floor. The prior crash
    // also arms tolerance: the retry of a crashing equilibrium rides out two below-bar samples
    // and aborts on the third.
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = ProbeBackoffMax;
    density.audit.crashStreak = 1;
    var walk = InjectWalk(density, /* isAudit= */ true, /* down= */ true,
        /* baseWindow= */ 2000, /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.99);
    walk.samples = ProbeWalkBudget - 10;
    density.rates.smoothed = 0.70;
    climber.sample.previousHitRate = 0.70;
    density.starvation.rung = ProbeBackoffInitial;
    density.audit.rung = ProbeBackoffMax;
    SteadySample(climber, /* windowMax= */ 1800, /* hitRate= */ 0.60);
    AssertThat(density.walk).IsNotNull();
    SteadySample(climber, /* windowMax= */ 1800, /* hitRate= */ 0.60);
    AssertThat(density.walk).IsNotNull();
    SteadySample(climber, /* windowMax= */ 1800, /* hitRate= */ 0.60);

    AssertThat(density.walk).IsNull();
    AssertThat(density.audit.crashStreak).IsEqualTo(2);
    AssertThat(density.audit.rung).IsEqualTo(ProbeBackoffMax);
    AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffInitial);
    AssertThat(density.auditClock.waitSamples).IsEqualTo(ProbeBackoffMax);
  }

  [Fact]
  public void Audit_LoneCrashTrain_NeverDefersBeyondTheLadder() {
    // The pulse train: three separate audits, each killed by an exogenous rate pulse,
    // with nothing but quiet steering between them. The crash streak pairs across the train
    // and deepens the audit rung to its cap, but the wait never takes the failure's doubling
    // (the shared-ladder form ratcheted to a 128-sample stand-down at the window floor) and
    // the starvation ladder never moves.
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    int[] expectedRung = {ProbeBackoffInitial, 2 * ProbeBackoffInitial, ProbeBackoffMax};
    for (int crash = 0; crash < 3; crash++) {
      var walk = InjectWalk(density, /* isAudit= */ true, /* down= */ true,
          /* baseWindow= */ 2000, /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.99);
      walk.samples = ProbeWalkBudget - 10;
      density.rates.smoothed = 0.70;
      climber.sample.previousHitRate = 0.70;
      int samples = (crash == 0) ? 1 : AuditCrashPersistence;
      for (int i = 0; i < samples; i++) {
        SteadySample(climber, /* windowMax= */ 1800, /* hitRate= */ 0.60);
      }
      AssertThat(density.walk).IsNull();
      AssertThat(density.audit.crashStreak).IsEqualTo(Math.Min(crash + 1, ProbeCrashEscalation));
      AssertThat(density.audit.rung).IsEqualTo(expectedRung[crash]);
      AssertThat(density.auditClock.waitSamples).IsAtMost(ProbeBackoffMax);
      AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffInitial);
    }
  }

  [Fact]
  public void Audit_ToleratedDip_HoldsTheWalkDirection() {
    // while the persistence counter is adjudicating a dip, the walk neither crashes nor
    // reverses: the same unbelieved evidence must not drive a reversal through the base into
    // a completed, rung-doubling failure. That conversion re-created the very cost the
    // tolerance removes
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.audit.crashStreak = 1;
    var walk = InjectWalk(density, /* isAudit= */ true, /* down= */ true,
        /* baseWindow= */ 2000, /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.99);
    walk.samples = ProbeWalkBudget - 10;
    density.rates.smoothed = 0.70;
    climber.sample.previousHitRate = 0.70;
    climber.tier.step.size = -Stride;
    long step = SteadySample(climber, /* windowMax= */ 1800, /* hitRate= */ 0.55);

    AssertThat(density.walk).IsSameInstanceAs(walk);
    AssertThat(walk.belowBarStreak).IsEqualTo(1);
    AssertThat(step).IsLessThan(0L);
  }

  [Fact]
  public void Audit_LowBaseRate_CrashAbortsOnAProportionalDrop() {
    // the abort is a level test against the rate frozen at the arm, so a threshold wider than that
    // rate cannot be satisfied at all and only the budget bounds a walk doing real damage
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    InjectWalk(density, /* isAudit= */ true, /* down= */ false,
        /* baseWindow= */ 1024, /* baseHitRate= */ 0.04, /* baseSmoothedRate= */ 0.0);
    climber.sample.previousHitRate = 0.04;

    // 0.033 falls past 0.04 - (AuditBarFraction * 0.04) but not past the negative 0.04 - 5pp
    Sample(climber, /* windowMax= */ 2048,
        /* windowHits= */ 20, /* mainHits= */ 13, /* misses= */ 967);

    AssertThat(density.walk).IsNull();
    AssertThat(density.audit.crashStreak).IsEqualTo(1);
  }

  [Fact]
  public void Audit_HighBaseRate_CrashBarStaysAbsolute() {
    // the proportional floor takes over only where the threshold outruns the rate; a walk whose
    // own rate exceeds it keeps the 5pp depth the pricing graveyard is written against
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    InjectWalk(density, /* isAudit= */ true, /* down= */ false,
        /* baseWindow= */ 1024, /* baseHitRate= */ 0.60, /* baseSmoothedRate= */ 0.0);
    climber.sample.previousHitRate = 0.60;

    // 0.53 falls past the absolute 5pp but not past the proportional 9pp, so it discriminates:
    // a bar that took the fraction unconditionally would let this walk continue
    Sample(climber, /* windowMax= */ 2048,
        /* windowHits= */ 280, /* mainHits= */ 250, /* misses= */ 470);

    AssertThat(density.walk).IsNull();
    AssertThat(density.audit.crashStreak).IsEqualTo(1);
  }

  [Fact]
  public void Audit_NoisyLowRate_CrashBarStaysOnTheFrozenRate() {
    // the noise term belongs to the reversal alone. Taking it here too would widen the level test
    // on exactly the workloads every dead pricing candidate widened it on, so a drop past the
    // fraction of the arming rate aborts however scattered the workload is
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    InjectWalk(density, /* isAudit= */ true, /* down= */ false,
        /* baseWindow= */ 1024, /* baseHitRate= */ 0.04, /* baseSmoothedRate= */ 0.0);
    density.rates.smoothed = 0.04;
    density.rates.deviation = 0.05;
    climber.sample.previousHitRate = 0.04;

    // 0.033 falls past 0.04 - (AuditBarFraction * 0.04) but not past the reversal's noise-priced
    // bar, so a crash abort sharing the reversal's pricing would let this walk continue
    Sample(climber, /* windowMax= */ 2048,
        /* windowHits= */ 20, /* mainHits= */ 13, /* misses= */ 967);

    AssertThat(density.walk).IsNull();
    AssertThat(density.audit.crashStreak).IsEqualTo(1);
  }

  [Fact]
  public void Audit_WalkThroughValley_ConfirmsPastTheDip() {
    // the moat shape end-to-end: on the retry of a crashing equilibrium, two below-bar samples
    // are tolerated in the committed direction, the far bank resets the persistence counter,
    // and the walk survives to confirm. Under the one-sample abort the far bank was
    // unreachable at any rung, because the abort fired before the walk could cross
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.audit.crashStreak = 1;
    var walk = InjectWalk(density, /* isAudit= */ true, /* down= */ false,
        /* baseWindow= */ 2000, /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.40);
    density.rates.smoothed = 0.70;
    climber.sample.previousHitRate = 0.70;
    climber.tier.step.size = Stride;
    SteadySample(climber, /* windowMax= */ 2200, /* hitRate= */ 0.55);
    AssertThat(density.walk).IsSameInstanceAs(walk);
    SteadySample(climber, /* windowMax= */ 2400, /* hitRate= */ 0.56);
    AssertThat(density.walk).IsSameInstanceAs(walk);
    AssertThat(walk.belowBarStreak).IsEqualTo(2);
    SteadySample(climber, /* windowMax= */ 2600, /* hitRate= */ 0.80);
    AssertThat(walk.belowBarStreak).IsEqualTo(0);
    for (int i = 0; (density.walk != null) && (i < ProbeWalkBudget); i++) {
      SteadySample(climber, /* windowMax= */ 2800, /* hitRate= */ 0.80);
    }

    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.audit.crashStreak).IsEqualTo(0);
    AssertThat(density.auditClock.waitSamples).IsEqualTo(AuditWaitInitial);
  }

  [Fact]
  public void Audit_BudgetExpiry_LeavesTheStarvationLedger() {
    // an audit's own endings clear its own crash streak; forgiving the starvation machine's
    // would let an unrelated audit reset the escalation a blind corner had earned
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.starvation.crashStreak = 1;
    density.audit.crashStreak = 1;
    var walk = InjectWalk(density, /* isAudit= */ true, /* down= */ true,
        /* baseWindow= */ 2000, /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.99);
    walk.samples = ProbeWalkBudget;
    density.rates.smoothed = 0.70;
    climber.sample.previousHitRate = 0.70;
    SteadySample(climber, /* windowMax= */ 1800, /* hitRate= */ 0.70);

    AssertThat(density.walk).IsNull();
    AssertThat(density.audit.crashStreak).IsEqualTo(0);
    AssertThat(density.starvation.crashStreak).IsEqualTo(1);
    AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffInitial);
  }

  [Fact]
  public void WalkStep_ReversalThroughBase_LeavesTheOtherLayersLedger() {
    // A reversal through the walk's own start is a completed failure, and it retires only the
    // owning layer's crash streak. Clearing both disarms the other layer's crash responses,
    // for audits, AuditCrashPersistence arms at a streak of one, so a starvation probe
    // reversing between two audit crashes would restore the one-sample abort the moat and the
    // pulse train need it not to have.
    foreach (bool audit in new bool[] {false, true}) {
      var climber = MakeClimber();
      var density = (DensityClimber) climber.tier;
      density.starvation.crashStreak = 1;
      density.audit.crashStreak = 1;
      var walk = InjectWalk(density, audit, /* down= */ false,
          /* baseWindow= */ 2000, /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.99);
      walk.samples = 1;
      density.rates.smoothed = 0.70;
      density.rates.deviation = 0.001;
      climber.sample.previousHitRate = 0.70;
      climber.tier.step.size = -Stride;
      Sample(climber, /* windowMax= */ 2000,
          /* windowHits= */ 10, /* mainHits= */ 690, /* misses= */ 300);

      AssertThat(density.walk).IsNull();
      AssertThat(density.audit.crashStreak).IsEqualTo(audit ? 0 : 1);
      AssertThat(density.starvation.crashStreak).IsEqualTo(audit ? 1 : 0);
    }
  }

  [Fact]
  public void WalkStep_FloorBasedWalk_EndsAtBudgetWithAFullUndo() {
    // A walk based at the density tier's resting position cannot end through its own base: the
    // machine rests at the truncated integer one entry below the 2% floor, a reversal's raw
    // landing there is the base itself, which the strict crossing test refuses, and the floor
    // clamp then commits one entry above the base. The budget bound and the full undo are the
    // invariant this pins; weakening either leaves such a walk unbounded.
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    var walk = InjectWalk(density, /* isAudit= */ true, /* down= */ false,
        /* baseWindow= */ (long) Floor, /* baseHitRate= */ 0.06, /* baseSmoothedRate= */ 0.99);
    walk.samples = 1;
    density.rates.smoothed = 0.06;
    density.rates.deviation = 0.001;
    climber.sample.previousHitRate = 0.06;
    climber.tier.step.size = Stride;

    // improving samples keep the bold driver's heading, a worsening one reverses it
    SteadySample(climber, /* windowMax= */ 675, /* hitRate= */ 0.70);
    AssertThat(density.walk).IsSameInstanceAs(walk);
    SteadySample(climber, /* windowMax= */ 1187, /* hitRate= */ 0.40);
    AssertThat(density.walk).IsSameInstanceAs(walk);

    // the reversal proposes the base exactly; the walk survives and rests one entry above it
    long landing = SteadySample(climber, /* windowMax= */ 675, /* hitRate= */ 0.60);
    AssertThat(density.walk).IsSameInstanceAs(walk);
    AssertThat(landing).IsEqualTo(-511);

    SteadySample(climber, /* windowMax= */ 164, /* hitRate= */ 0.055);
    AssertThat(density.walk).IsSameInstanceAs(walk);

    walk.samples = ProbeWalkBudget;
    long undo = SteadySample(climber, /* windowMax= */ 676, /* hitRate= */ 0.70);
    AssertThat(undo).IsEqualTo(((long) Floor) - 676);
    AssertThat(density.walk).IsNull();
    AssertThat(density.undoRemaining).IsEqualTo(0);
    AssertThat(density.audit.crashStreak).IsEqualTo(0);
    AssertThat(density.audit.rung).IsEqualTo(2 * ProbeBackoffInitial);
    AssertThat(density.anchor.held).IsFalse();
  }

  [Fact]
  public void Anchor_RatchetsToClearlyBetterPosition_AndReleasesPark() {
    // The memory follows the goal metric upward: standing elsewhere with the smoothed rate a
    // full margin above the anchor's claim moves the anchor here, and a parked climber whose
    // workload found somewhere better is released rather than dragged back. The ratchet is
    // deliberately unhurried: the rate step that signals the improvement also inflates the
    // deviation estimate, so the margin widens before it clears. A fresh step-change is
    // indistinguishable from noise onset until the smoothing settles.
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < 30; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
    }
    AssertThat(density.anchor.window).IsEqualTo(2000);
    density.anchor.held = true;
    // an audit reaching this position first would plant and park it by its own confirm, which the
    // ratchet is not observable through, so the clock is held back for the length of the scenario
    density.auditClock.stillSamples = 0;

    long parked = SteadySample(climber, /* windowMax= */ 4000, /* hitRate= */ 0.74);
    AssertThat(parked).IsEqualTo(0);
    AssertThat(density.anchor.held).IsTrue();

    int ratchetedAt = -1;
    for (int i = 2; i <= 20; i++) {
      SteadySample(climber, /* windowMax= */ 4000, /* hitRate= */ 0.74);
      if (density.anchor.window == 4000) {
        ratchetedAt = i;
        break;
      }
    }
    AssertThat(ratchetedAt).IsGreaterThan(2);
    AssertThat(ratchetedAt).IsAtMost(20);
    AssertThat(density.anchor.held).IsFalse();
    AssertThat(density.anchor.rate).IsWithin(1.0e-9).Of(density.rates.smoothed);
  }

  [Fact]
  public void Anchor_MidUndoDrain_DoesNotPlantAtTheRetreatingWindow() {
    // A capped retreat drains across later samples with the walk already ended and no return in
    // flight, so the window passes through the positions the probe was charged for rejecting. An
    // unplanted anchor waits for the retreat to land rather than claiming one of them; the drain
    // is read before the sample's stride, so the sample that empties the ledger is still transit.
    var climber = FailingDownProbe();
    var density = (DensityClimber) climber.tier;
    Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 200, /* mainHits= */ 500, /* misses= */ 300);
    AssertThat(density.walk).IsNull();
    AssertThat(density.undoRemaining).IsGreaterThan(0);

    SteadySample(climber, /* windowMax= */ 4457, /* hitRate= */ 0.70);
    AssertThat(density.undoRemaining).IsGreaterThan(0);
    AssertThat(density.anchor.IsPlanted()).IsFalse();

    SteadySample(climber, /* windowMax= */ 6914, /* hitRate= */ 0.70);
    AssertThat(density.undoRemaining).IsEqualTo(0);
    AssertThat(density.anchor.IsPlanted()).IsFalse();

    // the retreat has landed, so the position is the cache's own and may be claimed
    SteadySample(climber, /* windowMax= */ 7000, /* hitRate= */ 0.70);
    AssertThat(density.anchor.window).IsEqualTo(7000);
    AssertThat(density.anchor.rate).IsWithin(1.0e-9).Of(0.70);
  }

  [Fact]
  public void Anchor_MidUndoDrain_DoesNotRatchetOffTheProbeBase() {
    // The ratchet reads the same transit. A parked anchor at the probe's base, holding a claim the
    // smoothed rate now clears by a full margin, would otherwise move to wherever the retreat
    // happened to be and drop its park, leaving the guard rail defending a rejected position.
    var climber = FailingDownProbe();
    var density = (DensityClimber) climber.tier;
    density.anchor.Plant(/* windowMax= */ 7000, /* claimed= */ 0.40);
    density.anchor.Park(AuditWaitInitial);
    Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 200, /* mainHits= */ 500, /* misses= */ 300);

    SteadySample(climber, /* windowMax= */ 4457, /* hitRate= */ 0.70);
    AssertThat(density.anchor.window).IsEqualTo(7000);
    AssertThat(density.anchor.rate).IsEqualTo(0.40);
    AssertThat(density.anchor.held).IsTrue();

    // the re-sync is the half that does not wait: once the retreat is back inside the anchor's
    // band the stale claim decays into the live measurement, drain still in flight or not
    SteadySample(climber, /* windowMax= */ 6914, /* hitRate= */ 0.70);
    AssertThat(density.undoRemaining).IsEqualTo(0);
    AssertThat(density.anchor.window).IsEqualTo(7000);
    AssertThat(density.anchor.rate).IsWithin(1.0e-9).Of(0.70);

    long parked = SteadySample(climber, /* windowMax= */ 7000, /* hitRate= */ 0.70);
    AssertThat(parked).IsEqualTo(0);
    AssertThat(density.anchor.window).IsEqualTo(7000);
    AssertThat(density.anchor.held).IsTrue();
  }

  [Fact]
  public void GuardRail_MarginTracksNoise_SilentWhileWideThenVetoes() {
    // The shortfall margin is cleared of the workload's own hit-rate noise: while the deviation
    // estimate is wide the rail stays silent on a gap it cannot distinguish from weather, and
    // as quiet samples tighten the estimate the same gap becomes actionable. The mechanism
    // that keeps noisy-texture traces from being steered by wrong vetoes. The anchor here is
    // one capped stride away, so the return completes within a single sample and the park
    // (the anchor holding) is the observable outcome.
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.anchor.window = 2000;
    density.anchor.rate = 0.70;
    density.rates.smoothed = 0.66;
    climber.sample.previousHitRate = 0.66;
    density.rates.deviation = 0.05;
    density.auditClock.waitSamples = AuditWaitInitial;

    int vetoedAt = -1;
    for (int i = 1; i <= 12; i++) {
      SteadySample(climber, /* windowMax= */ 4000, /* hitRate= */ 0.66);
      if (density.anchor.held) {
        vetoedAt = i;
        break;
      }
    }
    AssertThat(vetoedAt).IsGreaterThan(5);
    AssertThat(vetoedAt).IsAtMost(12);
    AssertThat(density.anchor.returning).IsFalse();
    AssertThat(climber.Adjustment()).IsEqualTo(2000 - 4000);
  }

  [Fact]
  public void GuardRail_VetoReturnBudget_SettlesWhereItStands() {
    // a return whose strides the cache cannot realize (a rail: commands issued, transfers
    // starved) settles at its budget rather than striding forever; the park then holds the
    // reached position and the audit clock owns further exploration
    var climber = SeededShortfall();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitInitial;

    int strides = 0;
    for (int i = 0; i < 20; i++) {
      long adjustment = SteadySample(climber, /* windowMax= */ 7500, /* hitRate= */ 0.66);
      if (adjustment == -(long) (MaxStepFraction * Maximum)) {
        strides++;
      }
      if (density.anchor.held && !density.anchor.returning) {
        break;
      }
    }
    AssertThat(strides).IsEqualTo(VetoReturnBudget);
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.returning).IsFalse();

    long held = SteadySample(climber, /* windowMax= */ 7500, /* hitRate= */ 0.66);
    AssertThat(held).IsEqualTo(0);
  }

  [Fact]
  public void GuardRail_ReturnArrival_FalsifiesAStaleClaim() {
    // arriving proves nothing about the claim that sent the window here: the arrival's own drop
    // can fall under the crash-scale threshold while the claim is still a past regime's. The
    // position is settled and then judged against the claim frozen when the return committed,
    // and one that cannot earn it is stood down exactly as a crash-scale swing would have
    var climber = SeededShortfall();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitInitial;
    for (int i = 0; i < VetoStreak; i++) {
      SteadySample(climber, /* windowMax= */ 1500, /* hitRate= */ 0.66);
    }
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.returning).IsFalse();
    AssertThat(density.anchor.retestClaim).IsWithin(1.0e-6).Of(0.70);

    for (int i = 0; i < RetestSettle - 1; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.66);
      AssertThat(density.anchor.IsPlanted()).IsTrue();
    }
    // the on-anchor re-sync has decayed the live claim into the shortfall being tested, which is
    // why the frozen one is the judge
    AssertThat(density.anchor.rate).IsWithin(0.005).Of(0.66);
    AssertThat(density.anchor.retestClaim).IsWithin(1.0e-6).Of(0.70);

    SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.66);
    AssertThat(density.anchor.IsPlanted()).IsFalse();
    AssertThat(density.anchor.held).IsFalse();
    AssertThat(density.rates.IsUnseeded()).IsTrue();
  }

  [Fact]
  public void GuardRail_ReturnArrival_KeepsAClaimThePositionEarns() {
    // the retest is a test, not a discard: a rate that recovers on the way home leaves the
    // window at a position that does earn the claim, and the park stands
    var climber = SeededShortfall();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitInitial;
    for (int i = 0; i < VetoStreak; i++) {
      SteadySample(climber, /* windowMax= */ 7500, /* hitRate= */ 0.66);
    }
    AssertThat(density.anchor.returning).IsTrue();

    // strides home under a recovery no single sample announces as a workload change
    long windowMax = 7500;
    double hitRate = 0.66;
    for (int i = 0; i < 20; i++) {
      hitRate = Math.Min(0.76, hitRate + 0.02);
      windowMax += SteadySample(climber, windowMax, hitRate);
      if (!density.anchor.returning) {
        break;
      }
    }
    AssertThat(density.anchor.returning).IsFalse();
    AssertThat(density.anchor.IsAt(windowMax, WindowClimber.Reading.StableBand(Maximum))).IsTrue();
    AssertThat(density.anchor.retestClaim).IsWithin(1.0e-6).Of(0.70);

    for (int i = 0; i < RetestSettle; i++) {
      AssertThat(SteadySample(climber, windowMax, hitRate)).IsEqualTo(0);
    }
    AssertThat(density.anchor.retestClaim).IsEqualTo(-1);
    AssertThat(density.anchor.IsPlanted()).IsTrue();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.rates.IsUnseeded()).IsFalse();
  }

  [Fact]
  public void GuardRail_RetestDue_WaitsForTheReturnToEnd() {
    // the router asks only once a return has ended, but a return still under way has no retest
    // due, and asking from off the anchor must not cancel the retest that the arrival owes
    var climber = SeededShortfall();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitInitial;
    for (int i = 0; i < VetoStreak; i++) {
      SteadySample(climber, /* windowMax= */ 7500, /* hitRate= */ 0.66);
    }
    AssertThat(density.anchor.returning).IsTrue();
    AssertThat(density.anchor.retestClaim).IsWithin(1.0e-6).Of(0.70);
    int settleLeft = density.anchor.settleLeft;

    var reading = new WindowClimber.Reading(climber.sample, Maximum, /* windowMax= */ 7500,
        ProtectedMax(Maximum, /* windowMax= */ 7500));
    AssertThat(density.anchor.IsRetestDue(reading)).IsFalse();
    AssertThat(density.anchor.retestClaim).IsWithin(1.0e-6).Of(0.70);
    AssertThat(density.anchor.settleLeft).IsEqualTo(settleLeft);
  }

  [Fact]
  public void GuardRail_ReturnLanding_IsJudgedByTheRetest() {
    // the landing sample's recovery is the return's own, not a workload change: the claim the
    // return set out for survives to the retest, which keeps a position that earns it
    var climber = SeededShortfall();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitInitial;
    for (int i = 0; i < VetoStreak; i++) {
      SteadySample(climber, /* windowMax= */ 1500, /* hitRate= */ 0.66);
    }
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.returning).IsFalse();
    AssertThat(density.anchor.retestClaim).IsWithin(1.0e-6).Of(0.70);

    SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.80);
    AssertThat(density.anchor.IsPlanted()).IsTrue();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.retestClaim).IsWithin(1.0e-6).Of(0.70);
    AssertThat(density.rates.IsUnseeded()).IsFalse();

    for (int i = 0; i < RetestSettle - 1; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.80);
    }
    AssertThat(density.anchor.retestClaim).IsEqualTo(-1);
    AssertThat(density.anchor.IsPlanted()).IsTrue();
    AssertThat(density.anchor.held).IsTrue();
  }

  [Fact]
  public void GuardRail_ReturnShortOfTheAnchor_HasNothingToRetest() {
    // a return that spends its budget without arriving never reached the position its claim
    // describes, so there is no claim to judge and the reached position keeps its park
    var climber = SeededShortfall();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitInitial;
    for (int i = 0; i < 20; i++) {
      SteadySample(climber, /* windowMax= */ 7500, /* hitRate= */ 0.66);
      if (density.anchor.held && !density.anchor.returning) {
        break;
      }
    }
    AssertThat(density.anchor.returning).IsFalse();
    AssertThat(density.anchor.IsAt(7500, WindowClimber.Reading.StableBand(Maximum))).IsFalse();

    for (int i = 0; i <= RetestSettle; i++) {
      SteadySample(climber, /* windowMax= */ 7500, /* hitRate= */ 0.66);
    }
    AssertThat(density.anchor.IsPlanted()).IsTrue();
    AssertThat(density.anchor.window).IsEqualTo(2000);
    AssertThat(density.anchor.retestClaim).IsEqualTo(-1);
  }

  [Fact]
  public void GuardRail_CrashScaleShift_ReleasesTheParkAndReturn() {
    // a crash-scale swing announces a workload change: any park or in-progress return is
    // released so the climber re-engages with the new regime rather than defending a stale one
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < 30; i++) {
      SteadySample(climber, /* windowMax= */ 2000, /* hitRate= */ 0.70);
    }
    density.anchor.held = true;
    density.anchor.returning = true;
    density.anchor.returnLeft = 5;

    SteadySample(climber, /* windowMax= */ 5000, /* hitRate= */ 0.55);
    AssertThat(density.anchor.held).IsFalse();
    AssertThat(density.anchor.returning).IsFalse();
    AssertThat(density.anchor.window).IsEqualTo(2000);
  }

  [Fact]
  public void GuardRail_VetoReturn_ShiftMidReturn_AbandonsTheRetest() {
    // a crash-scale swing mid-return interrupts the return, and the retest goes with it, since
    // the frozen claim is judged where the return lands and an interrupted return does not land.
    // A claim left pending survives the stand-down, and a same-sample re-plant puts the window
    // back on the anchor, where the stale claim would judge the new position against a past
    // regime's rate
    var climber = SeededShortfall();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitInitial;
    density.anchor.window = 7000; // too far to reach in one stride, so the return is in flight
    for (int i = 0; i < VetoStreak; i++) {
      SteadySample(climber, /* windowMax= */ 1500, /* hitRate= */ 0.66);
    }
    AssertThat(density.anchor.returning).IsTrue();
    AssertThat(density.anchor.retestClaim).IsWithin(1.0e-6).Of(0.70);

    // mid-return the workload recovers and then jumps crash-scale, far from the anchor; the
    // recovery is seeded directly (an EMA pair only reaches it after a long plateau)
    density.rates.smoothed = 0.75;
    density.rates.deviation = 0.001;
    climber.sample.previousHitRate = 0.75;
    long adjustment = Sample(climber, /* windowMax= */ 3957,
      /* windowHits= */ 5, /* mainHits= */ 805, /* misses= */ 190);

    AssertThat(density.anchor.retestClaim).IsEqualTo(-1); // the interrupted retest is abandoned
    AssertThat(density.anchor.settleLeft).IsEqualTo(0);
    AssertThat(density.anchor.returning).IsFalse();
    AssertThat(density.anchor.window).IsEqualTo(3957); // the swing re-planted at its position
    AssertThat(density.anchor.rate).IsWithin(1.0e-6).Of(0.762);
    AssertThat(adjustment).IsNotEqualTo(0); // the sample steered rather than settled the retest
  }

  [Fact]
  public void GuardRail_VetoReturn_BudgetOut_ThenAnchorMoves_AbandonsTheRetest() {
    // the same stale claim without any crash-scale swing: a return that spends its budget short
    // of the anchor leaves the retest pending, and a rate that recovered while it strode moves
    // the anchor onto the window on the very next sample. The window is now "at" an anchor the
    // return never reached, so the claim frozen for the old position judges the new one
    var climber = SeededShortfall();
    var density = (DensityClimber) climber.tier;
    density.auditClock.waitSamples = AuditWaitMax;
    for (int i = 0; i < 20; i++) {
      SteadySample(climber, /* windowMax= */ 7500, /* hitRate= */ 0.66);
      if (density.anchor.held && !density.anchor.returning) {
        break;
      }
    }
    AssertThat(density.anchor.returning).IsFalse();      // the budget ran out short of the anchor
    AssertThat(density.anchor.window).IsEqualTo(2000);
    AssertThat(density.anchor.retestClaim).IsWithin(1.0e-6).Of(0.70);

    // the workload recovered while the return strode; seeded directly, as an EMA pair reaches it
    // only over a long plateau. No single sample moves a crash-scale step, so nothing stands down
    density.rates.smoothed = 0.76;
    density.rates.deviation = 0.001;
    climber.sample.previousHitRate = 0.76;
    SteadySample(climber, /* windowMax= */ 7500, /* hitRate= */ 0.76);

    AssertThat(density.anchor.window).IsEqualTo(7500);   // the anchor followed the better position
    AssertThat(density.anchor.retestClaim).IsEqualTo(-1);
    AssertThat(density.anchor.settleLeft).IsEqualTo(0);
  }

  [Fact]
  public void GuardRail_BlindCorner_OutranksTheVeto() {
    // a starved sample cannot honestly adjudicate a shortfall: blindness routes to the probe
    // machinery before the veto may fire, while an identically-placed sighted sample vetoes
    // (and, with the anchor one stride away, completes its return and parks within the call)
    var vetoed = SeededShortfall();
    var vetoedDensity = (DensityClimber) vetoed.tier;
    var probed = SeededShortfall();
    var probedDensity = (DensityClimber) probed.tier;
    for (int i = 0; i < 3; i++) {
      SteadySample(vetoed, /* windowMax= */ 1500, /* hitRate= */ 0.66);
      SteadySample(probed, /* windowMax= */ 1500, /* hitRate= */ 0.66);
    }
    AssertThat(vetoedDensity.anchor.held).IsFalse();
    AssertThat(probedDensity.anchor.held).IsFalse();

    SteadySample(vetoed, /* windowMax= */ 1500, /* hitRate= */ 0.66);
    AssertThat(vetoedDensity.anchor.held).IsTrue();
    AssertThat(vetoed.Adjustment()).IsEqualTo(2000 - 1500);

    long adjustment = Sample(probed, /* windowMax= */ 1500,
        /* windowHits= */ 0, /* mainHits= */ 660, /* misses= */ 340);
    AssertThat(probedDensity.walk).IsNotNull();
    AssertThat(probedDensity.anchor.held).IsFalse();
    AssertThat(adjustment).IsEqualTo((long) Stride);
  }

  /**
   * A climber whose anchor claims 0.70 at 2000 while the live smoothed rate runs 0.66, with the
   * deviation estimate settled so the margin floor binds (the cold-start seed would otherwise
   * hold the margin wide for several samples, the behavior pinned by the margin test).
   */
  private static WindowClimber SeededShortfall() {
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.anchor.window = 2000;
    density.anchor.rate = 0.70;
    density.rates.smoothed = 0.66;
    climber.sample.previousHitRate = 0.66;
    density.rates.deviation = 0.0;
    return climber;
  }

  /** Runs steady samples at the position until the audit arms, asserting it does. */
  private static WindowClimber ArmAudit(long windowMax) {
    return ArmAuditAtRung(windowMax, ProbeBackoffInitial);
  }

  /** Arms an audit at a position with the audit ladder already deepened to the given rung. */
  private static WindowClimber ArmAuditAtRung(long windowMax, int rung) {
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.audit.rung = rung;
    bool armed = false;
    for (int i = 0; i < (2 * AuditWaitInitial); i++) {
      SteadySample(climber, windowMax, /* hitRate= */ 0.70);
      if (density.walk != null) {
        armed = true;
        break;
      }
    }
    AssertThat(armed).IsTrue();
    AssertThat(WalkOf(density).isAudit).IsTrue();
    return climber;
  }

  [Fact]
  public void Audit_SightedFalseEquilibrium_ArmsWithoutStarvation() {
    // The whisper scenario end to end at the unit level: a thin above-bar trickle keeps the
    // window "sighted" at the floor, steering's clamp holds it there, no starvation is ever
    // declared, and the positional stillness clock arms an upward audit anyway. This is the
    // coverage the starvation triggers cannot provide.
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    int armedAt = -1;
    for (int i = 0; i < (2 * AuditWaitInitial); i++) {
      long adjustment = Sample(climber, /* windowMax= */ 164,
          /* windowHits= */ 5, /* mainHits= */ 595, /* misses= */ 400);
      if (density.walk != null) {
        armedAt = i;
        AssertThat(WalkOf(density).isAudit).IsTrue();
        break;
      }
      AssertThat(adjustment).IsEqualTo(0);
    }
    AssertThat(armedAt).IsAtLeast(AuditWaitFirst - 1);
    AssertThat(WalkOf(density).down).IsFalse();
    AssertThat(climber.Adjustment()).IsEqualTo((long) Stride);
  }

  /**
   * Completes a thousand-request sample at the given hit rate whose region split is proportional
   * to capacity, so the steering error is ~zero and the position reads as a quiet equilibrium.
   */
  private static long SteadySample(WindowClimber climber, long windowMax, double hitRate) {
    long hits = RoundHalfUp(1000 * hitRate);
    long windowHits = (hits * windowMax) / Maximum;
    return Sample(climber, windowMax, (int) windowHits, (int) (hits - windowHits),
        (int) (1000 - hits));
  }

  /* --------------- The walk --------------- */

  [Fact]
  public void WalkStep_StrideScalesByRung() {
    long[][] expected = {new long[] {16, 1}, new long[] {32, 2}, new long[] {64, 4}};
    foreach (long[] rungAndScale in expected) {
      var climber = MakeClimber();
      var density = (DensityClimber) climber.tier;
      density.starvation.rung = (int) rungAndScale[0];
      long adjustment = Sample(climber, /* windowMax= */ 1024,
          /* windowHits= */ 0, /* mainHits= */ 500, /* misses= */ 500);
      AssertThat(adjustment).IsEqualTo(rungAndScale[1] * (long) Stride);
    }
  }

  [Fact]
  public void WalkStep_Continuation_DecaysWhileQuiet() {
    var climber = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 1536,
        /* windowHits= */ 5, /* mainHits= */ 495, /* misses= */ 500);

    AssertThat(density.walk).IsNotNull();
    AssertThat(adjustment).IsEqualTo((long) (StepDecayRate * Stride));
  }

  [Fact]
  public void WalkStep_CrashScaleSampleDrop_Reverses() {
    // a drop past the walk-interior bar (at the cold-start deviation seed the priced bar sits
    // at its 15pp cap) reverses the bold driver, while remaining above the probe's own base so
    // the crash veto does not fire
    var climber = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    climber.sample.previousHitRate = 0.6;
    long adjustment = Sample(climber, /* windowMax= */ 2048,
        /* windowHits= */ 5, /* mainHits= */ 435, /* misses= */ 560);

    AssertThat(density.walk).IsNotNull();
    AssertThat(adjustment).IsEqualTo((long) -Stride);
  }

  [Fact]
  public void WalkStep_ReversalThroughBase_FailsAndDoubles() {
    var climber = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    climber.sample.previousHitRate = 0.6;
    long adjustment = Sample(climber, /* windowMax= */ 1280,
        /* windowHits= */ 5, /* mainHits= */ 435, /* misses= */ 560);

    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo(1024 - 1280);
    AssertThat(density.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
    AssertThat(density.refractoryLeft).IsEqualTo(density.starvation.rung);
  }

  [Fact]
  public void WalkStep_ReversalThroughBase_FailsDownward() {
    // the mirror of the up-probe reversal: a down walk whose bar-scale reversal would cross
    // back up through its own start finishes as a completed, failed experiment
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    climber.sample.previousHitRate = 0.6;
    long adjustment = Sample(climber, /* windowMax= */ 6900,
        /* windowHits= */ 435, /* mainHits= */ 5, /* misses= */ 560);

    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo(7000 - 6900);
    AssertThat(density.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
  }

  [Fact]
  public void WalkStep_FloorClamp_LandsOnFloor() {
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 400,
        /* windowHits= */ 495, /* mainHits= */ 5, /* misses= */ 500);

    AssertThat(density.walk).IsNotNull();
    AssertThat(adjustment).IsEqualTo((long) (Floor - 400));
    AssertThat(climber.tier.step.size).IsEqualTo(Floor - 400);
  }

  [Fact]
  public void WalkStep_FloorBlocked_RestartHoldsDirection() {
    // at the floor the clamp yields a zero step whose sign must stay negative, else
    // a restart-scale improving sample strides upward off positive zero during a down walk
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    long landing = Sample(climber, /* windowMax= */ 163,
        /* windowHits= */ 495, /* mainHits= */ 5, /* misses= */ 500);
    AssertThat(landing).IsEqualTo(0);
    AssertThat(climber.tier.step.size).IsEqualTo(-0.0);

    long jitter = Sample(climber, /* windowMax= */ 163,
        /* windowHits= */ 555, /* mainHits= */ 5, /* misses= */ 440);
    AssertThat(jitter).IsEqualTo(0);
    AssertThat(density.walk).IsNotNull();
    AssertThat(climber.tier.step.size).IsEqualTo(-0.0);
  }

  [Fact]
  public void WalkStep_FloorBlocked_CrashScaleDropWalksBackOut() {
    // an intended bold-driver reversal: the floor position is hurting, so walk out of it (the
    // elevated sample keeps the follow-on drop bar-scale against it yet above the base veto)
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    long landing = Sample(climber, /* windowMax= */ 163,
        /* windowHits= */ 555, /* mainHits= */ 5, /* misses= */ 440);
    AssertThat(landing).IsEqualTo(0);

    long reversal = Sample(climber, /* windowMax= */ 163,
        /* windowHits= */ 395, /* mainHits= */ 5, /* misses= */ 600);
    AssertThat(density.walk).IsNotNull();
    AssertThat(reversal).IsEqualTo((long) Stride);
  }

  /* --------------- Endings --------------- */

  [Fact]
  public void ProbeEnding_Crash_UndoesWithoutDoubling() {
    // at the cold-start deviation seed the probe's priced bar sits at its 15pp cap, so the
    // collapse must clear that to abort
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 5000,
        /* windowHits= */ 340, /* mainHits= */ 0, /* misses= */ 660);

    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo(7000 - 5000);
    AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffInitial);
    AssertThat(density.refractoryLeft).IsEqualTo(ProbeBackoffInitial);
  }

  [Fact]
  public void ProbeEnding_SubBarDrop_DoesNotCrashAStarvationProbe() {
    // the walk-interior bar is priced by the workload's own deviation (seeded wide at cold
    // start, capped at three restart thresholds): a 10pp drop is weather, not a collapse, and
    // the walk continues, while the same drop crash-aborts an audit, whose bar stays absolute
    // (audit_deepestRungCrash_retriesOnTheLadderCadence pins that side)
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 5000,
        /* windowHits= */ 400, /* mainHits= */ 0, /* misses= */ 600);

    AssertThat(density.walk).IsNotNull();
    AssertThat(adjustment).IsEqualTo((long) (StepDecayRate * -Stride));
  }

  [Fact]
  public void ProbeEnding_ProbeBarFloorsAtTheRestartThreshold() {
    // once the measured deviation is small the pricing floors at the absolute threshold, so a
    // 6pp drop on a quiet workload still crash-aborts a starvation probe
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    density.rates.smoothed = 0.5;
    density.rates.deviation = 0.001;
    long adjustment = Sample(climber, /* windowMax= */ 5000,
        /* windowHits= */ 440, /* mainHits= */ 0, /* misses= */ 560);

    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo(7000 - 5000);
    AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffInitial);
  }

  [Fact]
  public void ProbeEnding_StarvationCrash_LeavesTheAuditLadder() {
    // the inverse separation: a starvation probe's crash counts on its own ledger and
    // escalates only its own machine. The audit rung, streak, and clock never move
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    density.rates.smoothed = 0.5;
    density.rates.deviation = 0.001;
    Sample(climber, /* windowMax= */ 5000,
        /* windowHits= */ 440, /* mainHits= */ 0, /* misses= */ 560);

    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.crashStreak).IsEqualTo(1);
    AssertThat(density.audit.crashStreak).IsEqualTo(0);
    AssertThat(density.audit.rung).IsEqualTo(ProbeBackoffInitial);
    AssertThat(density.auditClock.waitSamples).IsEqualTo(AuditWaitFirst);
  }

  [Fact]
  public void ProbeEnding_ProbeBarCapsAtThreeRestarts() {
    // the cap is the abort's damage ceiling: on a workload so scattered that three deviations
    // read ~34pp, a 16pp collapse must still crash the walk. Uncapped pricing let walks roam
    // the all-blind families at a measured cost
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    density.rates.smoothed = 0.5;
    density.rates.deviation = 0.10;
    long adjustment = Sample(climber, /* windowMax= */ 5000,
        /* windowHits= */ 340, /* mainHits= */ 0, /* misses= */ 660);

    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo(7000 - 5000);
    AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffInitial);
  }

  [Fact]
  public void ProbeEnding_ProbeBarTracksTheLiveDeviation_NotTheArmingValue() {
    // The starvation bar prices against the deviation as it stands NOW, not a value frozen when
    // the probe armed, and that is load-bearing rather than an oversight: on a dosed workload the
    // deviation at arm is a pre-dose value, so a frozen bar sits below the weather and the walk
    // crash-aborts inside the very dose the pricing exists to survive (measured 2026-08-02,
    // mixnoise_a10 60.7 -> 56.0 and crashnoise_a12 63.6 -> 62.2, both deterministic). The live
    // reading and ProbeBarCap are a matched pair. The feedback lifts the bar while the walk is
    // exposed, the cap bounds where that would run away. Here the same 9pp drop crashes the walk
    // at the arming deviation and is tolerated once the deviation has risen under it.
    foreach (bool weatherRose in new bool[] {false, true}) {
      var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
      var density = (DensityClimber) climber.tier;
      density.rates.smoothed = 0.5;
      density.rates.deviation = weatherRose ? 0.06 : 0.001;
      Sample(climber, /* windowMax= */ 5000,
          /* windowHits= */ 410, /* mainHits= */ 0, /* misses= */ 590);

      AssertThat(density.walk != null).IsEqualTo(weatherRose);
    }
  }

  [Fact]
  public void Audit_WalkStep_ReversalStaysAbsolute() {
    // an audit's bold driver reverses on the absolute 5pp, not the probes' priced bar: the
    // same 7pp drop that a starvation probe walks through (the deviation prices its bar to
    // the cap) turns an audit's walk around. The audit-side pricing was studied to a fresh
    // holdout and rejected, so this asymmetry is load-bearing
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    var walk = InjectWalk(density, /* isAudit= */ true, /* down= */ false,
        /* baseWindow= */ 1024, /* baseHitRate= */ 0.60, /* baseSmoothedRate= */ 0.99);
    walk.samples = ProbeWalkBudget - 10;
    climber.tier.step.size = Stride;
    density.rates.smoothed = 0.70;
    climber.sample.previousHitRate = 0.70;
    long adjustment = Sample(climber, /* windowMax= */ 2560,
        /* windowHits= */ 5, /* mainHits= */ 625, /* misses= */ 370);

    AssertThat(density.walk).IsNotNull();
    AssertThat(adjustment).IsEqualTo((long) -Stride);
  }

  [Fact]
  public void Audit_WalkStep_ReversalIsPricedAboveTheNoise() {
    // the reversal is a first-difference test, so it is priced against the larger of the rate
    // frozen at the arm and the workload's scatter. The same 1pp drop turns the walk around on a
    // quiet workload and is no evidence at all on a noisy one, where a rate-only price sits at a
    // fifth of the per-sample noise and a reversal back through the base is charged as a completed
    // failure
    foreach (bool noisy in new bool[] {false, true}) {
      var climber = MakeClimber();
      var density = (DensityClimber) climber.tier;
      var walk = InjectWalk(density, /* isAudit= */ true, /* down= */ false,
          /* baseWindow= */ 1024, /* baseHitRate= */ 0.04, /* baseSmoothedRate= */ 0.99);
      walk.samples = ProbeWalkBudget - 10;
      climber.tier.step.size = Stride;
      density.rates.smoothed = 0.05;
      density.rates.deviation = noisy ? 0.05 : 0.001;
      climber.sample.previousHitRate = 0.05;
      long adjustment = Sample(climber, /* windowMax= */ 2560,
          /* windowHits= */ 10, /* mainHits= */ 30, /* misses= */ 960);

      AssertThat(density.walk).IsNotNull();
      AssertThat(adjustment > 0).IsEqualTo(noisy);
    }
  }

  [Fact]
  public void ProbeEnding_BudgetExpiry_UndoesAndDoubles() {
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    WalkOf(density).samples = ProbeWalkBudget;
    long adjustment = Sample(climber, /* windowMax= */ 5000,
        /* windowHits= */ 495, /* mainHits= */ 5, /* misses= */ 500);

    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo(7000 - 5000);
    AssertThat(density.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
    AssertThat(density.refractoryLeft).IsEqualTo(density.starvation.rung);
  }

  [Fact]
  public void ProbeEnding_WatchedRegionIsMainForDownProbe() {
    // the window earning richly must not adjudicate a down probe; main is the watched region
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 900, /* mainHits= */ 5, /* misses= */ 95);

    AssertThat(density.walk).IsNotNull();
    AssertThat(adjustment).IsEqualTo((long) (StepDecayRate * -Stride));
  }

  [Fact]
  public void ProbeEnding_Adjudication_ConfirmsOnDensity() {
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 5, /* mainHits= */ 900, /* misses= */ 95);

    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(1);
    AssertThat(density.refractoryLeft).IsEqualTo(0);
    AssertThat(adjustment).IsEqualTo(DensityStep(
        /* windowMax= */ 2000, /* windowHits= */ 5, /* mainHits= */ 900));
  }

  [Fact]
  public void ProbeEnding_Adjudication_WrongSignFailsAndDoubles() {
    // the 5000-entry restore exceeds the 30% maximum step, so the undo honors the cap and the
    // remainder is carried across the following samples before steering resumes
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var density = (DensityClimber) climber.tier;
    double cap = MaxStepFraction * Maximum;
    long adjustment = Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 880, /* mainHits= */ 20, /* misses= */ 100);

    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo((long) cap);
    AssertThat(density.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);

    long second = Sample(climber, /* windowMax= */ 2000 + adjustment,
        /* windowHits= */ 880, /* mainHits= */ 20, /* misses= */ 100);
    AssertThat(second).IsEqualTo((long) cap);

    long third = Sample(climber, /* windowMax= */ 2000 + adjustment + second,
        /* windowHits= */ 880, /* mainHits= */ 20, /* misses= */ 100);
    AssertThat(third).IsEqualTo(5000 - (2 * (long) (MaxStepFraction * Maximum)));
    AssertThat(adjustment + second + third).IsEqualTo(5000);
    AssertThat(density.undoRemaining).IsEqualTo(0);
  }

  [Fact]
  public void ProbeEnding_SecondConsecutiveCrash_Escalates() {
    // one crash is priced as a possible workload shift (no doubling); a cycle of them is probe
    // damage, so consecutive crashes escalate like completed failures instead of repeating at a
    // fixed rung forever
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    long first = Sample(climber, /* windowMax= */ 5000,
        /* windowHits= */ 340, /* mainHits= */ 0, /* misses= */ 660);
    AssertThat(first).IsEqualTo(7000 - 5000);
    AssertThat(density.starvation.crashStreak).IsEqualTo(1);
    AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffInitial);

    var walk = InjectWalk(density, /* isAudit= */ false, /* down= */ true,
        /* baseWindow= */ 7000, /* baseHitRate= */ 0.5, /* baseSmoothedRate= */ 0.0);
    walk.samples = 1;
    climber.tier.step.size = -Stride;
    climber.sample.previousHitRate = 0.5;
    long second = Sample(climber, /* windowMax= */ 5000,
        /* windowHits= */ 340, /* mainHits= */ 0, /* misses= */ 660);
    AssertThat(second).IsEqualTo(7000 - 5000);
    AssertThat(density.starvation.crashStreak).IsEqualTo(2);
    AssertThat(density.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
  }

  [Fact]
  public void ProbeEnding_CrashStreak_Saturates() {
    // the ledger only distinguishes a lone crash from a run, so the streak holds at the escalation
    // threshold rather than counting on toward a wrap
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    density.starvation.crashStreak = ProbeCrashEscalation;
    long returned = Sample(climber, /* windowMax= */ 5000,
        /* windowHits= */ 340, /* mainHits= */ 0, /* misses= */ 660);
    AssertThat(returned).IsEqualTo(7000 - 5000);
    AssertThat(density.starvation.crashStreak).IsEqualTo(ProbeCrashEscalation);
    AssertThat(density.starvation.CrashEscalates()).IsTrue();
    AssertThat(density.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
  }


  [Fact]
  public void ProbeEnding_MidRungCommitment_BlocksEarlyExit() {
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.starvation.rung = 2 * ProbeBackoffInitial;
    long entry = Sample(climber, /* windowMax= */ 1024,
        /* windowHits= */ 0, /* mainHits= */ 500, /* misses= */ 500);
    AssertThat(density.walk).IsNotNull();
    AssertThat(entry).IsEqualTo(2 * (long) Stride);

    // depth 1 < the middle rung's commitment of 2: adjudication is blocked despite the signal
    long walked = Sample(climber, /* windowMax= */ 3072,
        /* windowHits= */ 400, /* mainHits= */ 100, /* misses= */ 500);
    AssertThat(density.walk).IsNotNull();
    AssertThat(walked).IsEqualTo((long) (StepDecayRate * 2 * Stride));

    // depth 2: the same signal now adjudicates (and confirms; the window is the denser region)
    long confirmed = Sample(climber, /* windowMax= */ 3072,
        /* windowHits= */ 400, /* mainHits= */ 100, /* misses= */ 500);
    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(1);
    AssertThat(confirmed).IsEqualTo(DensityStep(
        /* windowMax= */ 3072, /* windowHits= */ 400, /* mainHits= */ 100));
  }

  [Fact]
  public void ProbeEnding_UpProbe_ConfirmsAgainstFrozenBaseline() {
    // The up-probe verdict prices opportunity cost at main's margin: a thin reuse band whose
    // window density loses to main's protected-core AVERAGE still confirms by out-earning the
    // probation baseline frozen at the probe's start (the trickle family's repair). Density then
    // walks the window home in the same sample, and the ladder prices that reversal as a completed
    // experiment, so the next walk commits deeper (the band past a first-round stride is reached
    // by the deeper rungs, not by the confirm)
    var climber = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5,
        /* baseProbationDensity= */ 0.002);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 30, /* mainHits= */ 940, /* misses= */ 30);

    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
    AssertThat(density.refractoryLeft).IsEqualTo(0);
    AssertThat(adjustment).IsEqualTo(DensityStep(
        /* windowMax= */ 2000, /* windowHits= */ 30, /* mainHits= */ 940));
  }

  [Fact]
  public void ProbeEnding_UpProbe_FailsAgainstRicherFrozenBaseline() {
    // The mirror: when the boundary was earning richly at arm time (a frequency floor), the same
    // watched signal fails the verdict and the walk is undone with the ladder doubled.
    var climber = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5,
        /* baseProbationDensity= */ 0.5);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 30, /* mainHits= */ 940, /* misses= */ 30);

    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo(1024 - 2000);
    AssertThat(density.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
    AssertThat(density.refractoryLeft).IsEqualTo(density.starvation.rung);
  }

  [Fact]
  public void ProbeEnding_UpProbeVerdict_IsFreeOfTheSampleLength() {
    // The up-probe divides a live window density by a probation density frozen at the arm, and a
    // density is a hit count over a capacity: two of them form a ratio only when both count the
    // same span. The same per-request rates over a sample twice as long must reach the same
    // verdict. Nothing pins this on an unweighted cache, whose period is a constant four maxima;
    // a weighted one samples on the sketch's entry-denominated period, which moves with the
    // resident count, so a growing count would otherwise confirm every up-probe it lengthens.
    var brief = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.97,
        /* baseProbationDensity= */ 0.02);
    var briefDensity = (DensityClimber) brief.tier;
    long briefVerdict = Sample(brief, /* windowMax= */ 2000,
        /* windowHits= */ 30, /* mainHits= */ 940, /* misses= */ 30);

    var extended = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.97,
        /* baseProbationDensity= */ 0.02);
    var extendedDensity = (DensityClimber) extended.tier;
    long extendedVerdict = Sample(extended, /* windowMax= */ 2000,
        /* windowHits= */ 60, /* mainHits= */ 1880, /* misses= */ 60);

    // the window earns less per request than the boundary did, so both fail and undo to the base
    AssertThat(briefDensity.walk).IsNull();
    AssertThat(extendedDensity.walk).IsNull();
    AssertThat(briefVerdict).IsEqualTo(1024 - 2000);
    AssertThat(extendedVerdict).IsEqualTo(1024 - 2000);
    AssertThat(briefDensity.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
    AssertThat(extendedDensity.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
  }

  [Fact]
  public void ArmProbe_FreezesProbationBaselinePerArm() {
    // The baseline snapshots at each arm, never mid-walk: the walk's own demotions enrich live
    // probation (a demoted protected-hot core earns furiously there), so adjudicating against
    // the live rate is an absorbing false-veto; re-arming re-snapshots, so a cold-start
    // transient baseline heals on the next round.
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    long capacity = ProbationCapacity(1024);
    long entry = SampleWithProbation(climber, /* windowMax= */ 1024,
        /* windowHits= */ 0, /* mainHits= */ 500, /* probationHits= */ 200, /* misses= */ 500);
    AssertThat(density.walk).IsNotNull();
    AssertThat(entry).IsEqualTo((long) Stride);
    AssertThat(WalkOf(density).baseProbationDensity).IsWithin(1.0e-9).Of(200.0 / capacity);

    density.walk = null;
    density.refractoryLeft = 0;
    long rearm = SampleWithProbation(climber, /* windowMax= */ 1024,
        /* windowHits= */ 0, /* mainHits= */ 500, /* probationHits= */ 10, /* misses= */ 500);
    AssertThat(density.walk).IsNotNull();
    AssertThat(rearm).IsEqualTo((long) Stride);
    AssertThat(WalkOf(density).baseProbationDensity).IsWithin(1.0e-9).Of(10.0 / capacity);
  }

  [Fact]
  public void ProbeEnding_UpProbe_ZeroBaselineConfirmsOnAnySignal() {
    // A probation region earning nothing at arm time prices the claimed capacity at ~zero, so
    // any watched earnings above the adjudication bar confirm. The opportunity cost of a dead
    // boundary is nothing; the confirm hands to density, which reverses the sparser window, and
    // that reversal deepens the ladder rather than resetting it
    var climber = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5,
        /* baseProbationDensity= */ 0.0);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 30, /* mainHits= */ 940, /* misses= */ 30);

    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
    AssertThat(adjustment).IsEqualTo(DensityStep(
        /* windowMax= */ 2000, /* windowHits= */ 30, /* mainHits= */ 940));
  }

  [Fact]
  public void ProbeEnding_StarvationConfirm_ReversedByDensity_EscalatesTheLadder() {
    // A confirm the density arm reverses in the same sample keeps nothing: the window is walked
    // home and the corner re-arms, so a reward here restarts the ladder on every cycle of a dither
    // whose first-round stride never reaches the band it is looking for. The reversal is priced as
    // the completed experiment it is, so the next walk commits deeper; the handoff to density and
    // the zero refractory of a confirm are unchanged, and the ladder's cap still holds
    var reversed = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5,
        /* baseProbationDensity= */ 0.002);
    var reversedDensity = (DensityClimber) reversed.tier;
    long adjustment = Sample(reversed, /* windowMax= */ 2000,
        /* windowHits= */ 30, /* mainHits= */ 940, /* misses= */ 30);
    AssertThat(reversedDensity.walk).IsNull();
    AssertThat(reversedDensity.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
    AssertThat(reversedDensity.refractoryLeft).IsEqualTo(0);
    AssertThat(reversedDensity.anchor.held).IsFalse();
    AssertThat(adjustment).IsLessThan(0L);
    AssertThat(adjustment).IsEqualTo(DensityStep(
        /* windowMax= */ 2000, /* windowHits= */ 30, /* mainHits= */ 940));

    // the same verdict where density agrees with the walk keeps the reward
    var agreed = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var agreedDensity = (DensityClimber) agreed.tier;
    Sample(agreed, /* windowMax= */ 2000,
        /* windowHits= */ 5, /* mainHits= */ 900, /* misses= */ 95);
    AssertThat(agreedDensity.walk).IsNull();
    AssertThat(agreedDensity.starvation.rung).IsEqualTo(1);
    AssertThat(agreedDensity.refractoryLeft).IsEqualTo(0);

    // at the deepest rung a reversal cannot deepen the ladder further
    var deepest = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5,
        /* baseProbationDensity= */ 0.002);
    var deepestDensity = (DensityClimber) deepest.tier;
    deepestDensity.starvation.rung = ProbeBackoffMax;
    WalkOf(deepestDensity).samples = ProbeCommitmentDeep;
    Sample(deepest, /* windowMax= */ 2000,
        /* windowHits= */ 30, /* mainHits= */ 940, /* misses= */ 30);
    AssertThat(deepestDensity.walk).IsNull();
    AssertThat(deepestDensity.starvation.rung).IsEqualTo(ProbeBackoffMax);
    AssertThat(deepestDensity.refractoryLeft).IsEqualTo(0);
  }

  [Fact]
  public void ProbeEnding_StarvationConfirm_RepeatOfTheFarthest_EscalatesTheLadder() {
    // A kept confirm at or short of the farthest window the ladder's walks have already confirmed
    // re-finds ground the machine found and then lost, so it is priced as a completed experiment
    // rather than rewarded: rewarding it pins the ladder at its first rung on every cycle of a lure
    // that a first-round stride catches and density dismantles when it stops (the absolve family).
    // The handoff to density and the zero refractory are unchanged; a confirm beyond the farthest
    // is new ground and still rewards, moving the memory with it
    var climber = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    Sample(climber, /* windowMax= */ 2000, /* windowHits= */ 500, /* mainHits= */ 400,
        /* misses= */ 100);
    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(1);
    AssertThat(density.starvation.farthest).IsEqualTo(2000);
    AssertThat(density.starvation.farthestDown).IsFalse();

    ReprobeUp(climber, /* baseHitRate= */ 0.5);
    long adjustment = Sample(climber, /* windowMax= */ 1800, /* windowHits= */ 500,
        /* mainHits= */ 400, /* misses= */ 100);
    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(2);
    AssertThat(density.refractoryLeft).IsEqualTo(0);
    AssertThat(density.starvation.farthest).IsEqualTo(2000);
    AssertThat(adjustment).IsEqualTo(DensityStep(
        /* windowMax= */ 1800, /* windowHits= */ 500, /* mainHits= */ 400));

    // within the stable band of the farthest is still a repeat
    ReprobeUp(climber, /* baseHitRate= */ 0.5);
    Sample(climber, /* windowMax= */ 2100, /* windowHits= */ 500, /* mainHits= */ 400,
        /* misses= */ 100);
    AssertThat(density.starvation.rung).IsEqualTo(4);
    AssertThat(density.starvation.farthest).IsEqualTo(2100);

    ReprobeUp(climber, /* baseHitRate= */ 0.5);
    Sample(climber, /* windowMax= */ 2600, /* windowHits= */ 500, /* mainHits= */ 400,
        /* misses= */ 100);
    AssertThat(density.starvation.rung).IsEqualTo(1);
    AssertThat(density.starvation.farthest).IsEqualTo(2600);
  }

  [Fact]
  public void ProbeEnding_StarvationConfirm_TheOtherDirection_StartsTheMemoryOver() {
    // the memory is per direction: a down-walk's confirm after up-walk confirms is new ground, and
    // farthest for a down-walk is the smallest window
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var density = (DensityClimber) climber.tier;
    density.starvation.Remember(/* down= */ false, 2000);
    Sample(climber, /* windowMax= */ 2000, /* windowHits= */ 5, /* mainHits= */ 900,
        /* misses= */ 95);
    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(1);
    AssertThat(density.starvation.farthestDown).IsTrue();
    AssertThat(density.starvation.farthest).IsEqualTo(2000);

    ReprobeDown(climber, /* baseHitRate= */ 0.9);
    Sample(climber, /* windowMax= */ 2100, /* windowHits= */ 5, /* mainHits= */ 900,
        /* misses= */ 95);
    AssertThat(density.starvation.rung).IsEqualTo(2);
    AssertThat(density.starvation.farthest).IsEqualTo(2000);

    ReprobeDown(climber, /* baseHitRate= */ 0.9);
    Sample(climber, /* windowMax= */ 1500, /* windowHits= */ 5, /* mainHits= */ 900,
        /* misses= */ 95);
    AssertThat(density.starvation.rung).IsEqualTo(1);
    AssertThat(density.starvation.farthest).IsEqualTo(1500);
  }

  [Fact]
  public void UndoProbe_StarvationFailOrCrash_ForgetsTheFarthest() {
    // a walk that keeps nothing shows the terrain the memory was found on has moved; an audit's
    // endings leave the starvation ladder's memory alone
    var failed = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5);
    var failedDensity = (DensityClimber) failed.tier;
    Sample(failed, /* windowMax= */ 2000, /* windowHits= */ 500, /* mainHits= */ 400,
        /* misses= */ 100);
    AssertThat(failedDensity.starvation.farthest).IsEqualTo(2000);
    ReprobeUp(failed, /* baseHitRate= */ 0.5).samples = ProbeWalkBudget;
    Sample(failed, /* windowMax= */ 1500, /* windowHits= */ 0, /* mainHits= */ 500,
        /* misses= */ 500);
    AssertThat(failedDensity.walk).IsNull();
    AssertThat(failedDensity.starvation.farthest).IsEqualTo(-1);

    var crashed = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5);
    var crashedDensity = (DensityClimber) crashed.tier;
    Sample(crashed, /* windowMax= */ 2000, /* windowHits= */ 500, /* mainHits= */ 400,
        /* misses= */ 100);
    ReprobeUp(crashed, /* baseHitRate= */ 0.5);
    Sample(crashed, /* windowMax= */ 1500, /* windowHits= */ 0, /* mainHits= */ 100,
        /* misses= */ 900);
    AssertThat(crashedDensity.walk).IsNull();
    AssertThat(crashedDensity.starvation.crashStreak).IsEqualTo(1);
    AssertThat(crashedDensity.starvation.farthest).IsEqualTo(-1);

    var audited = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.5);
    var auditedDensity = (DensityClimber) audited.tier;
    Sample(audited, /* windowMax= */ 2000, /* windowHits= */ 500, /* mainHits= */ 400,
        /* misses= */ 100);
    InjectWalk(auditedDensity, /* isAudit= */ true, /* down= */ true, /* baseWindow= */ 2000,
        /* baseHitRate= */ 0.5, /* baseSmoothedRate= */ 0.5).samples = ProbeWalkBudget;
    audited.sample.previousHitRate = 0.5;
    Sample(audited, /* windowMax= */ 1800, /* windowHits= */ 400, /* mainHits= */ 100,
        /* misses= */ 500);
    AssertThat(auditedDensity.walk).IsNull();
    AssertThat(auditedDensity.starvation.farthest).IsEqualTo(2000);
  }

  [Fact]
  public void ProbeEnding_DeepReversedConfirm_GoalConfirmed_ParksAsAnAudit() {
    // A starvation walk that took the deepest commitment, whose confirm density reverses, and
    // that satisfies the audit's own confirm test is an audit in all but name: it is kept as a
    // park, since density would walk it home in the same sample, and the sample takes no parting
    // step. The base rate is far below what the walk earns, so the goal metric confirms it
    var climber = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.30,
        /* baseProbationDensity= */ 0.002);
    var density = (DensityClimber) climber.tier;
    density.starvation.rung = ProbeBackoffMax;
    var walk = WalkOf(density);
    walk.samples = ProbeCommitmentDeep;
    walk.aboveStreak = AuditConfirmStreak - 1;
    long adjustment = Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 30, /* mainHits= */ 940, /* misses= */ 30);
    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo(0);
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.freshLeft).IsEqualTo(AuditWaitInitial);
    AssertThat(density.anchor.window).IsEqualTo(2000);
    AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffMax);

    // without the goal metric's streak the same confirm hands back to density, as any reversed
    // confirm does
    var unconfirmed = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.30,
        /* baseProbationDensity= */ 0.002);
    var unconfirmedDensity = (DensityClimber) unconfirmed.tier;
    unconfirmedDensity.starvation.rung = ProbeBackoffMax;
    WalkOf(unconfirmedDensity).samples = ProbeCommitmentDeep;
    long steered = Sample(unconfirmed, /* windowMax= */ 2000,
        /* windowHits= */ 30, /* mainHits= */ 940, /* misses= */ 30);
    AssertThat(unconfirmedDensity.walk).IsNull();
    AssertThat(unconfirmedDensity.anchor.held).IsFalse();
    AssertThat(steered).IsEqualTo(DensityStep(
        /* windowMax= */ 2000, /* windowHits= */ 30, /* mainHits= */ 940));

    // and so does a walk short of the deepest commitment, streak or not
    var shallow = ProbingUp(/* stepSize= */ Stride, /* baseHitRate= */ 0.30,
        /* baseProbationDensity= */ 0.002);
    var shallowDensity = (DensityClimber) shallow.tier;
    WalkOf(shallowDensity).aboveStreak = AuditConfirmStreak - 1;
    long shallowStep = Sample(shallow, /* windowMax= */ 2000,
        /* windowHits= */ 30, /* mainHits= */ 940, /* misses= */ 30);
    AssertThat(shallowDensity.walk).IsNull();
    AssertThat(shallowDensity.anchor.held).IsFalse();
    AssertThat(shallowStep).IsEqualTo(DensityStep(
        /* windowMax= */ 2000, /* windowHits= */ 30, /* mainHits= */ 940));
  }

  [Fact]
  public void Audit_WalkFromPark_CrashScaleMove_KeepsThePark() {
    // an audit's walk out of a park is the park's own re-test: the crash-scale move it makes when
    // it walks into a valley is what the crash abort prices, and the undo returns to the park, so
    // it is not read as a workload shift that stands the park down (a shift while no walk is in
    // flight still does, as guardRail_crashScaleShift_releasesTheParkAndReturn pins)
    var climber = ArmAudit(/* windowMax= */ 2000);
    var density = (DensityClimber) climber.tier;
    density.anchor.held = true;
    density.anchor.freshLeft = 0;
    long walked = 2000 + climber.Adjustment();
    long undo = SteadySample(climber, walked, /* hitRate= */ 0.50);
    AssertThat(density.walk).IsNull();
    AssertThat(undo).IsEqualTo(2000 - walked);
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.window).IsEqualTo(2000);
  }

  [Fact]
  public void ProbeEnding_DownProbe_IgnoresProbationBaseline() {
    // Down-probes keep the average-density verdict, since the window has no marginal substructure,
    // so a poisoned baseline must not affect their adjudication.
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9,
        /* baseProbationDensity= */ 1.0e9);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 5, /* mainHits= */ 900, /* misses= */ 95);

    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(1);
    AssertThat(adjustment).IsEqualTo(DensityStep(
        /* windowMax= */ 2000, /* windowHits= */ 5, /* mainHits= */ 900));
  }

  [Fact]
  public void ProbeEnding_AdjudicationBar_IsFourTimesTheStarvationBar() {
    // the watched region may judge at exactly four times the bar; one hit fewer keeps walking
    var judged = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var judgedDensity = (DensityClimber) judged.tier;
    long verdict = Sample(judged, /* windowMax= */ 2000,
        /* windowHits= */ 850, /* mainHits= */ 16, /* misses= */ 134);
    AssertThat(judgedDensity.walk).IsNull();
    AssertThat(verdict).IsEqualTo((long) (MaxStepFraction * Maximum));

    var walking = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var walkingDensity = (DensityClimber) walking.tier;
    long stride = Sample(walking, /* windowMax= */ 2000,
        /* windowHits= */ 850, /* mainHits= */ 15, /* misses= */ 135);
    AssertThat(walkingDensity.walk).IsNotNull();
    AssertThat(stride).IsEqualTo((long) (StepDecayRate * -Stride));
  }

  [Fact]
  public void ProbeEnding_NeutralVerdict_Fails() {
    // "no better, no worse" must fail: an early design that kept neutral positions let the
    // density arm walk the window home and the probe re-fire, forever. Equal densities give a
    // signed verdict of exactly zero, which does not confirm the probed direction.
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.9);
    var density = (DensityClimber) climber.tier;
    long adjustment = Sample(climber, /* windowMax= */ 2000,
        /* windowHits= */ 250, /* mainHits= */ 774, /* misses= */ 0);

    AssertThat(density.walk).IsNull();
    AssertThat(adjustment).IsEqualTo((long) (MaxStepFraction * Maximum));
    AssertThat(density.undoRemaining).IsEqualTo(5000 - (long) (MaxStepFraction * Maximum));
    AssertThat(density.starvation.rung).IsEqualTo(2 * ProbeBackoffInitial);
  }

  [Fact]
  public void ProbeEnding_LadderCapBinds() {
    var climber = ProbingDown(/* stepSize= */ -Stride, /* baseHitRate= */ 0.5);
    var density = (DensityClimber) climber.tier;
    density.starvation.rung = ProbeBackoffMax;
    WalkOf(density).samples = ProbeWalkBudget;
    long undo = Sample(climber, /* windowMax= */ 5000,
        /* windowHits= */ 495, /* mainHits= */ 5, /* misses= */ 500);

    AssertThat(undo).IsEqualTo(7000 - 5000);
    AssertThat(density.walk).IsNull();
    AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffMax);
  }

  /* --------------- Tiers --------------- */

  [Fact]
  public void MediumTier_UsesSketchPeriodAndStandardDecay() {
    var climber = new WindowClimber();
    climber.Resized(2048);
    double seed = climber.tier.step.size;

    for (int i = 0; i < 500; i++) {
      climber.RecordHit(/* inWindow= */ false, /* inProbation= */ false);
      climber.RecordMiss();
    }
    climber.DetermineAdjustment(2048, /* windowMaximum= */ 200,
        ProtectedMax(2048, 200), /* sketchSampleSize= */ 1001);
    AssertThat(climber.Adjustment()).IsEqualTo(0);
    AssertThat(climber.sample.hits).IsEqualTo(500);

    climber.RecordMiss();
    climber.DetermineAdjustment(2048, /* windowMaximum= */ 200,
        ProtectedMax(2048, 200), /* sketchSampleSize= */ 1001);
    AssertThat(climber.Adjustment()).IsEqualTo((long) seed);
    AssertThat(climber.sample.hits).IsEqualTo(0);

    for (int i = 0; i < 505; i++) {
      climber.RecordHit(/* inWindow= */ false, /* inProbation= */ false);
    }
    for (int i = 0; i < 496; i++) {
      climber.RecordMiss();
    }
    climber.DetermineAdjustment(2048, /* windowMaximum= */ 200,
        ProtectedMax(2048, 200), /* sketchSampleSize= */ 1001);
    AssertThat(climber.Adjustment()).IsEqualTo((long) seed);
    AssertThat(climber.tier.step.size).IsWithin(1.0e-9).Of(StepDecayRate * seed);
  }

  [Fact]
  public void MediumTier_ReversesOnWorse_AndRestartsOnCrashScaleChange() {
    // the reactive climber's two remaining decisions: a worsening sub-crash sample reverses the
    // bold driver, and a crash-scale change re-anneals the decayed step to its full magnitude
    var climber = new WindowClimber();
    climber.Resized(2048);
    double seed = climber.tier.step.size;
    AssertThat(seed).IsEqualTo(-StepPercent * 2048);

    ReactiveSample(climber, /* hits= */ 500, /* misses= */ 501);
    AssertThat(climber.Adjustment()).IsEqualTo((long) seed);

    ReactiveSample(climber, /* hits= */ 480, /* misses= */ 521);
    AssertThat(climber.Adjustment()).IsEqualTo((long) -seed);
    AssertThat(climber.tier.step.size).IsWithin(1.0e-9).Of(StepDecayRate * -seed);

    ReactiveSample(climber, /* hits= */ 590, /* misses= */ 411);
    AssertThat(climber.Adjustment()).IsEqualTo((long) (StepDecayRate * -seed));
    AssertThat(climber.tier.step.size).IsEqualTo(-seed);
  }

  /** Completes one full sample in the medium tier (sketch period 1001). */
  private static void ReactiveSample(WindowClimber climber, int hits, int misses) {
    for (int i = 0; i < hits; i++) {
      climber.RecordHit(/* inWindow= */ false, /* inProbation= */ false);
    }
    for (int i = 0; i < misses; i++) {
      climber.RecordMiss();
    }
    climber.DetermineAdjustment(2048, /* windowMaximum= */ 200,
        ProtectedMax(2048, 200), /* sketchSampleSize= */ 1001);
  }

  /* --------------- The floor-blocked walk (wall-sit) scenario --------------- */

  [Fact]
  public void Walk_FloorBlockedDownProbe_NeverStridesUpAndUndoesOnExpiry() {
    var climber = new WindowClimber();
    long maximum = 1_048_576;
    climber.Resized(maximum);
    var density = (DensityClimber) climber.tier;
    density.starvation.rung = ProbeBackoffMax;
    double floor = WindowFloorFraction * maximum;
    double stride = StepPercent * maximum;
    double baseWindow = 800_000;

    // the walk arms from a dead sample past the midpoint, the one down trigger left now that a
    // starved main beside a large window steers instead of probing
    long windowMax = (long) baseWindow;
    long arming = BigSample(climber, maximum, windowMax, /* hits= */ 0);
    double expected = -Math.Min(MaxStepFraction * maximum, 4 * stride);
    AssertThat(arming).IsEqualTo((long) expected);
    AssertThat(WalkOf(density).down).IsTrue();
    windowMax += arming;

    int moving = 1;
    int blocked = 0;
    for (int i = 0; (i < ProbeWalkBudget) && (density.walk != null); i++) {
      int hits = (blocked == 4) ? 540 : 500;  // an improving sub-crash jitter mid-wall
      long adjustment = BigSample(climber, maximum, windowMax, hits);
      if (density.walk != null) {
        AssertThat(adjustment).IsAtMost(0);
        if (adjustment == 0) {
          AssertThat(windowMax).IsAtLeast((long) floor);
          AssertThat(windowMax).IsAtMost((long) Math.Ceiling(floor) + 1);
          blocked++;
        } else {
          moving++;
        }
        windowMax += adjustment;
      } else {
        // the restore honors the maximum step; the remainder continues on the following samples
        double cap = MaxStepFraction * maximum;
        AssertThat(adjustment).IsAtMost((long) cap);
        windowMax += adjustment;
        for (int j = 0; (j < ProbeWalkBudget) && (density.undoRemaining != 0); j++) {
          long chunk = BigSample(climber, maximum, windowMax, /* hits= */ 500);
          AssertThat(Math.Abs(chunk)).IsAtMost((long) cap);
          windowMax += chunk;
        }
        AssertThat(density.undoRemaining).IsEqualTo(0);
      }
    }
    AssertThat(density.walk).IsNull();
    AssertThat((double) windowMax).IsWithin(4.0).Of(baseWindow);
    AssertThat(moving).IsAtMost(5);
    AssertThat(blocked).IsAtLeast(10);
    AssertThat(density.starvation.rung).IsEqualTo(ProbeBackoffMax);
  }

  [Fact]
  public void Walk_FloorBlockedAudit_ChargesTheClockForEvidenceItNeverGathered() {
    // The sibling above pins the starvation half of a floor-blocked walk. This is the audit half,
    // and it is the costlier one: strides that clamp at the rail still spend a sample apiece and
    // the walk still ends at its budget, and since there is no BLOCKED ending the expiry is
    // priced as a completed failure. At the deepest rung that doubles the wait between audits on
    // evidence the walk never gathered. Pinned as the current contract, so a BLOCKED ending would
    // have to move these numbers deliberately.
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    density.audit.rung = ProbeBackoffMax;
    density.auditClock.waitSamples = AuditWaitMax / 2;
    density.auditClock.stillSamples = AuditWaitMax;
    density.rates.smoothed = 0.50;
    density.rates.deviation = 0.001;
    climber.sample.previousHitRate = 0.50;

    // Both regions earn, so no blind corner pre-empts the clock; the window sits past the upper
    // corner, where the direction rule chooses down and the walk runs into the floor.
    long windowMax = 7000;
    windowMax += Sample(climber, windowMax,
        /* windowHits= */ 250, /* mainHits= */ 250, /* misses= */ 500);
    AssertThat(WalkOf(density).isAudit).IsTrue();
    AssertThat(WalkOf(density).down).IsTrue();

    long lowest = windowMax;
    int blocked = 0;
    for (int i = 0; (i < ProbeWalkBudget) && (density.walk != null); i++) {
      long adjustment = Sample(climber, windowMax,
          /* windowHits= */ 250, /* mainHits= */ 250, /* misses= */ 500);
      if ((density.walk != null) && (adjustment == 0)) {
        blocked++;
      }
      windowMax += adjustment;
      lowest = Math.Min(lowest, windowMax);
    }

    // it reached the rail, stood there for most of its budget, and was charged a failure for it
    AssertThat(density.walk).IsNull();
    AssertThat(blocked).IsAtLeast(ProbeWalkBudget / 2);
    AssertThat(lowest).IsAtMost((long) Math.Ceiling(Floor) + 1);
    AssertThat(density.audit.rung).IsEqualTo(ProbeBackoffMax);
    AssertThat(density.auditClock.waitSamples).IsEqualTo(AuditWaitMax);
  }

  /* --------------- Helpers --------------- */

  private static WindowClimber MakeClimber() {
    var climber = new WindowClimber();
    climber.Resized(Maximum);
    return climber;
  }

  /**
   * The walk in flight, asserting that there is one. Never hold the result across a sample: an
   * ending discards the walk, so a carried local reports the state it died in.
   */
  private static Walk WalkOf(DensityClimber density) {
    var walk = density.walk;
    AssertThat(walk).IsNotNull();
    return walk!;
  }

  /**
   * Puts a walk in flight, as an arm would have left it, so a scenario can start from a base the
   * machine would need many samples to reach. The returned walk is the handle for the counters a
   * scenario also wants to pre-set; the probation baseline is the zero a fresh climber carries,
   * since only an up-probe's starvation verdict reads it.
   */
  private static Walk InjectWalk(DensityClimber density, bool isAudit,
      bool down, long baseWindow, double baseHitRate, double baseSmoothedRate) {
    var walk = new Walk(isAudit ? density.audit : density.starvation, isAudit, down,
        baseWindow, /* baseRequestCount= */ 1000, baseHitRate, baseSmoothedRate,
        /* baseProbationDensity= */ 0.0);
    density.walk = walk;
    return walk;
  }

  /**
   * Copies the active walk with a colder smoothed-rate baseline, the state an arm carries when its
   * rate lags a warming sample, preserving the strides already taken. The bases stay because
   * the verdict studies rely on the frozen-at-arm property, so this replaces the walk rather than
   * mutating it.
   */
  private static Walk RebaseReference(WindowClimber climber, double reference) {
    var density = (DensityClimber) climber.tier;
    var armed = WalkOf(density);
    var walk = new Walk(armed.ladder, armed.isAudit, armed.down, armed.baseWindow,
        armed.baseRequestCount, armed.baseHitRate, reference, armed.baseProbationDensity);
    walk.samples = armed.samples;
    walk.belowBarStreak = armed.belowBarStreak;
    walk.aboveStreak = armed.aboveStreak;
    walk.beatBase = armed.beatBase;
    density.walk = walk;
    return walk;
  }

  private static WindowClimber ProbingUp(double stepSize, double baseHitRate) {
    return ProbingUp(stepSize, baseHitRate, /* baseProbationDensity= */ 0.0);
  }

  /** Puts a fresh up-probe in flight on a climber whose ladder already carries a history. */
  private static Walk ReprobeUp(WindowClimber climber, double baseHitRate) {
    return Reprobe(climber, /* down= */ false, /* baseWindow= */ 1024, Stride, baseHitRate);
  }

  /** Puts a fresh down-probe in flight on a climber whose ladder already carries a history. */
  private static Walk ReprobeDown(WindowClimber climber, double baseHitRate) {
    return Reprobe(climber, /* down= */ true, /* baseWindow= */ 7000, -Stride, baseHitRate);
  }

  private static Walk Reprobe(WindowClimber climber, bool down,
      long baseWindow, double stepSize, double baseHitRate) {
    var density = (DensityClimber) climber.tier;
    var walk = InjectWalk(density, /* isAudit= */ false, down, baseWindow, baseHitRate,
        /* baseSmoothedRate= */ 0.0);
    walk.samples = 1;
    climber.tier.step.size = stepSize;
    climber.sample.previousHitRate = baseHitRate;
    density.refractoryLeft = 0;
    density.undoRemaining = 0;
    return walk;
  }

  /**
   * Arms an audit at a quiet equilibrium and walks it to a confirmed park at the improved rate,
   * returning the climber parked below the position (the first interior audit explores down).
   */
  private static WindowClimber ConfirmedAuditPark(
      long windowMax, double baseRate, double walkedRate) {
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < (AuditWaitInitial + 2); i++) {
      SteadySample(climber, windowMax, baseRate);
      if (density.walk != null) {
        break;
      }
    }
    AssertThat(WalkOf(density).isAudit).IsTrue();
    AssertThat(WalkOf(density).down).IsTrue();
    long walked = windowMax + climber.Adjustment();
    for (int i = 0; i < ProbeWalkBudget; i++) {
      walked += SteadySample(climber, walked, walkedRate);
      if (density.walk == null) {
        break;
      }
    }
    AssertThat(density.walk).IsNull();
    AssertThat(density.anchor.held).IsTrue();
    AssertThat(density.anchor.window).IsEqualTo(walked);
    return climber;
  }

  /** Holds the position still at the rate until the next audit arms, asserting it does. */
  private static void ArmNextAudit(WindowClimber climber, long windowMax, double hitRate) {
    var density = (DensityClimber) climber.tier;
    for (int i = 0; i < (2 * AuditWaitInitial); i++) {
      SteadySample(climber, windowMax, hitRate);
      if (density.walk != null) {
        break;
      }
    }
    AssertThat(WalkOf(density).isAudit).IsTrue();
  }

  private static WindowClimber ProbingUp(
      double stepSize, double baseHitRate, double baseProbationDensity) {
    return Probing(/* down= */ false, stepSize, baseHitRate,
        /* baseWindow= */ 1024, baseProbationDensity);
  }

  private static WindowClimber ProbingDown(double stepSize, double baseHitRate) {
    return ProbingDown(stepSize, baseHitRate, /* baseProbationDensity= */ 0.0);
  }

  private static WindowClimber ProbingDown(
      double stepSize, double baseHitRate, double baseProbationDensity) {
    return Probing(/* down= */ true, stepSize, baseHitRate,
        /* baseWindow= */ 7000, baseProbationDensity);
  }

  /**
   * Arms a down probe whose first sample fails on the density verdict, leaving a retreat longer
   * than one capped stride so that the undo drains across the samples that follow.
   */
  private static WindowClimber FailingDownProbe() {
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    InjectWalk(density, /* isAudit= */ false, /* down= */ true, /* baseWindow= */ 7000,
        /* baseHitRate= */ 0.70, /* baseSmoothedRate= */ 0.0).samples = 1;
    climber.sample.previousHitRate = 0.70;
    density.rates.deviation = 0.001;
    density.rates.smoothed = 0.70;
    climber.tier.step.size = -Stride;
    return climber;
  }

  private static WindowClimber Probing(bool down, double stepSize,
      double baseHitRate, long baseWindow, double baseProbationDensity) {
    var climber = MakeClimber();
    var density = (DensityClimber) climber.tier;
    var walk = new Walk(density.starvation, /* isAudit= */ false, down, baseWindow,
        /* baseRequestCount= */ 1000, baseHitRate, /* baseSmoothedRate= */ 0.0,
        baseProbationDensity);
    walk.samples = 1;
    density.walk = walk;
    climber.tier.step.size = stepSize;
    climber.sample.previousHitRate = baseHitRate;
    return climber;
  }

  /** Completes one thousand-request sample and returns the committed adjustment. */
  private static long Sample(WindowClimber climber,
      long windowMax, int windowHits, int mainHits, int misses) {
    return SampleWithProbation(climber, windowMax,
        windowHits, mainHits, /* probationHits= */ 0, misses);
  }

  /** Completes one sample whose main hits include the given probation-attributed share. */
  private static long SampleWithProbation(WindowClimber climber,
      long windowMax, int windowHits, int mainHits, int probationHits, int misses) {
    for (int i = 0; i < windowHits; i++) {
      climber.RecordHit(/* inWindow= */ true, /* inProbation= */ false);
    }
    for (int i = 0; i < (mainHits - probationHits); i++) {
      climber.RecordHit(/* inWindow= */ false, /* inProbation= */ false);
    }
    for (int i = 0; i < probationHits; i++) {
      climber.RecordHit(/* inWindow= */ false, /* inProbation= */ true);
    }
    for (int i = 0; i < misses; i++) {
      climber.RecordMiss();
    }
    int requests = windowHits + mainHits + misses;
    climber.DetermineAdjustment(Maximum, windowMax, ProtectedMax(Maximum, windowMax),
        /* sketchSampleSize= */ requests);
    return climber.Adjustment();
  }

  /** Completes a thousand-request sample with the given window hits (main earns nothing). */
  private static long BigSample(WindowClimber climber, long maximum, long windowMax, int hits) {
    for (int i = 0; i < hits; i++) {
      climber.RecordHit(/* inWindow= */ true, /* inProbation= */ false);
    }
    for (int i = 0; i < (1000 - hits); i++) {
      climber.RecordMiss();
    }
    climber.DetermineAdjustment(maximum, windowMax, ProtectedMax(maximum, windowMax),
        /* sketchSampleSize= */ 1000);
    return climber.Adjustment();
  }

  /**
   * The protected region's maximum for the main space left by the window (80% of main). This is a
   * per-position simplification: the cache sets protected once per resize and conserves window plus
   * protected, so its probation stays fixed while this helper's varies with the window.
   */
  private static long ProtectedMax(long maximum, long windowMax) {
    return ((maximum - windowMax) * 4) / 5;
  }

  /** The probation capacity implied by {@link #protectedMax}. */
  private static long ProbationCapacity(long windowMax) {
    return Maximum - windowMax - ProtectedMax(Maximum, windowMax);
  }

  /** Mirrors the density arm's proportional step for an expected-value computation. */
  private static long DensityStep(long windowMax, int windowHits, int mainHits) {
    double windowDensity = windowHits / (double) windowMax;
    double mainDensity = mainHits / (double) (Maximum - windowMax);
    double error = Math.Log((windowDensity + DensityEpsilon)
        / (mainDensity + DensityEpsilon));
    double magnitude = Math.Min(MaxStepFraction * Maximum,
        Math.Abs(error) * DensityGain * Maximum);
    double step = (error >= 0) ? magnitude : -magnitude;
    double clamped = ((step < 0) && ((windowMax + step) < Floor))
        ? Math.Min(0.0, Floor - windowMax)
        : step;
    return (long) clamped;
  }
}
