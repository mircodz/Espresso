using System;

namespace Espresso.Internal;

/// <summary>
/// A hill climber that adapts the size of the admission window to balance the cache's recency and
/// frequency regions.
/// <para>
/// All mutable state is guarded by the cache's eviction lock. Every double-to-integral conversion
/// goes through <see cref="Common.ToLongSaturating"/>, which truncates, saturates, and maps NaN to
/// zero on every platform; the other floating-point operations used here
/// (<see cref="Math.Min(double, double)"/>, <see cref="Math.Max(double, double)"/>,
/// <see cref="Math.CopySign(double, double)"/>, and <see cref="Math.Abs(double)"/>) propagate NaN and
/// order signed zeros as IEEE 754 specifies on .NET Core 3.0+.
/// </para>
/// </summary>
internal sealed class WindowClimber
{
    /*
     * W-TinyLFU divides capacity between an admission window, which favors recency, and a main
     * region, where TinyLFU filters new arrivals by frequency. The best split depends on the
     * workload, so the climber adjusts it using the cache's hit statistics.
     *
     * Small caches compare hit rates across samples to choose a direction. Larger caches have enough
     * hits to compare the regions' hit densities (hits per unit of capacity) within a single sample,
     * reducing sensitivity to workload changes between samples.
     *
     * Density alone cannot identify a good split: it measures only resident entries, and a region
     * with few hits provides little information. The density tier uses probes to explore these
     * starved regions and periodic audits to test whether a different split improves the hit rate. An
     * anchor records a well-performing position to return to if later adjustments reduce the hit
     * rate.
     */

    /// <summary>The change in hit rate large enough to trigger adaptation to a new workload.</summary>
    internal const double RestartThreshold = 0.05d;

    internal readonly Sample sample;
    internal long adjustment;
    internal Climber tier;

    /// <summary>Creates a climber whose tier is initialized by <see cref="Resized"/>.</summary>
    public WindowClimber()
    {
        sample = new Sample();
        tier = null!;
    }

    /// <summary>Selects the tier and resets adaptation state for the new maximum size.</summary>
    public void Resized(long maximum)
    {
        tier = DensityClimber.AppliesTo(maximum)
            ? new DensityClimber(maximum)
            : new ReactiveClimber(maximum);
        sample.Reset();
        adjustment = 0;
    }

    /// <summary>Discards the current sample and the previous hit rate.</summary>
    public void ResetSample()
    {
        sample.Reset();
    }

    /// <summary>Records a cache miss.</summary>
    public void RecordMiss()
    {
        sample.RecordMiss();
    }

    /// <summary>Records a cache hit on an entry residing in the window or main space.</summary>
    public void RecordHit(bool inWindow, bool inProbation)
    {
        sample.RecordHit(inWindow, inProbation);
    }

    /// <summary>Returns a positive value to grow the admission window and a negative one to shrink it.</summary>
    public long Adjustment()
    {
        return adjustment;
    }

    /// <summary>Retains the portion of the adjustment that could not be applied.</summary>
    public void CarryOver(long remaining)
    {
        adjustment = remaining;
    }

    /// <summary>Updates the adjustment when a full sample is available, retaining any carry-over otherwise.</summary>
    public void DetermineAdjustment(long maximum, long windowMaximum,
        long mainProtectedMaximum, int sketchSampleSize)
    {
        if (sample.RequestCount() >= tier.SamplePeriod(maximum, sketchSampleSize))
        {
            adjustment = tier.Climb(sample, maximum, windowMaximum, mainProtectedMaximum);
            sample.Close();
        }
    }

    /// <summary>
    /// Discards a full sample gathered while the cache was too empty to adapt from, keeping the hit
    /// rate from before the gap for the next comparison.
    /// </summary>
    public void DiscardSample(long maximum, int sketchSampleSize)
    {
        if (sample.RequestCount() >= tier.SamplePeriod(maximum, sketchSampleSize))
        {
            sample.Discard();
        }
    }

    /// <summary>A strategy for adjusting the admission window from sampled cache statistics.</summary>
    internal abstract class Climber
    {
        internal readonly Step step;

        protected Climber(long maximum, bool grow)
        {
            step = new Step(maximum, grow);
        }

        /// <summary>Returns the number of requests in an adaptation sample.</summary>
        public abstract long SamplePeriod(long maximum, int sketchSampleSize);

        /// <summary>Returns the amount to adapt the window by.</summary>
        public abstract long Climb(Sample sample, long maximum, long windowMax, long mainProtectedMax);
    }

    /// <summary>
    /// A hill climber that adjusts the window according to changes in hit rate. It reverses direction
    /// when the hit rate falls and reduces the step size as it converges. Large changes in hit rate
    /// restore the initial step size so it can adapt to a new workload.
    /// <para>
    /// Small caches use this strategy because their per-region hit counts are too low for reliable
    /// density estimates. The smallest caches start by growing because their initial window contains
    /// only a few entries. They use longer samples and slower step decay to reduce sensitivity to
    /// noise.
    /// </para>
    /// </summary>
    internal sealed class ReactiveClimber : Climber
    {
        /// <summary>The maximum size at which adaptation uses longer samples and slower step decay.</summary>
        internal const long SlowAdaptThreshold = 512L;
        /// <summary>Maximum factor by which the sample period may grow in the slow-adapt regime.</summary>
        internal const double SlowAdaptRatioCap = 4.0d;
        /// <summary>The slower decay rate used to preserve useful steps in very small caches.</summary>
        internal const double SlowAdaptDecayRate = 0.995d;

        internal ReactiveClimber(long maximum)
            : base(maximum, IsSlowAdapting(maximum))
        {
        }

        public override long SamplePeriod(long maximum, int sketchSampleSize)
        {
            if (!IsSlowAdapting(maximum))
            {
                return sketchSampleSize;
            }
            double initialStep = Step.StepPercent * maximum;
            double magnitude = Math.Max(step.Magnitude(), initialStep / SlowAdaptRatioCap);
            if (magnitude == 0.0)
            {
                return sketchSampleSize;
            }
            double ratio = Math.Max(1.0, Math.Min(SlowAdaptRatioCap, initialStep / magnitude));
            return (ratio == 1.0) ? sketchSampleSize : Common.ToLongSaturating(sketchSampleSize * ratio);
        }

        public override long Climb(Sample sample, long maximum, long windowMax, long mainProtectedMax)
        {
            double hitRateChange = sample.HitRateChange();
            double amount = step.Heading(/* forward: */ hitRateChange >= 0);
            step.Commit((Math.Abs(hitRateChange) >= RestartThreshold)
                ? Math.CopySign(Step.RestartMagnitude(maximum), amount)
                : (DecayRate(maximum) * amount));
            return Common.ToLongSaturating(amount);
        }

        /// <summary>Returns whether this maximum falls in the slow-adapt regime.</summary>
        internal static bool IsSlowAdapting(long maximum)
        {
            return maximum <= SlowAdaptThreshold;
        }

        /// <summary>Returns the step decay rate.</summary>
        private static double DecayRate(long maximum)
        {
            return IsSlowAdapting(maximum) ? SlowAdaptDecayRate : Step.StepDecayRate;
        }
    }

    /// <summary>
    /// A hill climber that shifts capacity toward the region with more hits per unit of capacity.
    /// Comparing regions within the same sample reduces sensitivity to workload changes between
    /// samples.
    /// <para>
    /// Hit density describes only resident entries and can favor a split with a poor overall hit rate.
    /// Probes explore regions with too few hits to measure, while audits test whether moving away from
    /// the current split improves the hit rate. An anchor records a known good position for recovery
    /// after an unproductive adjustment.
    /// </para>
    /// </summary>
    internal sealed class DensityClimber : Climber
    {
        /// <summary>The cache size threshold between reactive and density feedback.</summary>
        internal const long DensityThreshold = 4096L;
        /// <summary>A longer sample period stabilize density estimates at the cost of slower adaptation.</summary>
        internal const long SampleMultiplier = 4L;
        /// <summary>The step per unit of log density-ratio error, as a fraction of the maximum.</summary>
        internal const double DensityGain = 0.03d;
        /// <summary>The samples covered by a retreat stride, including its arrival sample.</summary>
        internal const int RetreatCover = 2;

        internal readonly AuditClock auditClock;
        internal readonly Ladder starvation;
        internal readonly Anchor anchor;
        internal readonly Ladder audit;
        internal readonly Rates rates;

        internal Walk? walk;

        internal long undoRemaining;
        internal int refractoryLeft;
        internal int retreatLeft;

        internal DensityClimber(long maximum)
            : base(maximum, /* grow: */ false)
        {
            auditClock = new AuditClock();
            starvation = new Ladder();
            anchor = new Anchor();
            audit = new Ladder();
            rates = new Rates();
        }

        /// <summary>Returns whether the cache is large enough to use density feedback.</summary>
        internal static bool AppliesTo(long maximum)
        {
            return maximum > DensityThreshold;
        }

        public override long SamplePeriod(long maximum, int sketchSampleSize)
        {
            long period = SampleMultiplier * maximum; // unchecked: an overflow wraps and is detected below
            if ((period / SampleMultiplier) != maximum)
            {
                period = long.MaxValue;
            }
            return Math.Min(period, sketchSampleSize);
        }

        public override long Climb(Sample sample, long maximum, long windowMax, long mainProtectedMax)
        {
            // A readonly struct passed by reference, so a sample allocates nothing
            var reading = new Reading(sample, maximum, windowMax, mainProtectedMax);
            double amount = Route(in reading);
            auditClock.Tick(windowMax, Reading.StableBand(maximum));
            return Common.ToLongSaturating(amount);
        }

        /// <summary>Returns the next adjustment, giving probes and recovery priority over density steering.</summary>
        private double Route(in Reading reading)
        {
            AgeParkShield();
            AgeRetreatCover();

            if (IsWorkloadShift(in reading) && anchor.StandDown(in reading))
            {
                // Discard the old workload's rate history along with its anchor
                rates.Reset();
            }
            UpdateRateReferences(in reading);

            var walk = this.walk;
            if (walk != null)
            {
                var ending = ProbeEndingOf(walk, in reading);
                if (ending == ProbeEnding.Walking)
                {
                    return WalkStep(walk, /* entry: */ false, in reading);
                }
                else if (ending != ProbeEnding.Confirmed)
                {
                    return UndoProbe(walk, ending, in reading);
                }
                else if (KeepConfirmedPosition(walk, in reading))
                {
                    return anchor.returning ? StrideHome(in reading) : 0.0;
                }
                // A starvation confirm resumes density steering in the same sample
                return Steer(reading.SteeringError(), in reading);
            }
            else if (HasPendingUndo())
            {
                return UndoStride(in reading);
            }
            else if (anchor.returning)
            {
                return StrideHome(in reading);
            }
            else if (anchor.IsRetestDue(in reading))
            {
                RetestReturn(in reading);
                return 0.0;
            }
            else if (reading.HasBlindCorner())
            {
                return IsBackingOff() ? HoldOrAudit(in reading) : ArmStarvationProbe(in reading);
            }
            else if (anchor.VetoTriggered(in reading, rates))
            {
                return StrideHome(in reading);
            }
            else if (auditClock.IsDue())
            {
                return ArmEquilibriumAudit(in reading);
            }
            else if (anchor.held)
            {
                // Density would move away from this better position. Wait for an audit to explore again
                return 0.0;
            }
            return Steer(reading.SteeringError(), in reading);
        }

        /// <summary>Ages the protection against workload shifts while parked, pausing during a walk.</summary>
        private void AgeParkShield()
        {
            if (IsShielded())
            {
                anchor.AgeShield();
            }
        }

        /// <summary>
        /// Ages the protection against rate changes caused by a retreat. Each stride renews it through
        /// the arrival sample.
        /// </summary>
        private void AgeRetreatCover()
        {
            if (retreatLeft > 0)
            {
                retreatLeft--;
            }
        }

        /// <summary>
        /// Returns whether a recently confirmed position is protected from workload shifts. Walks
        /// suspend this protection so a shift can still invalidate the position during exploration.
        /// </summary>
        private bool IsShielded()
        {
            return (walk == null) && anchor.IsShielded();
        }

        /// <summary>
        /// Returns whether the hit-rate change indicates a new workload. Audits of a parked position
        /// and returns can cause large rate changes themselves, so their results are judged separately.
        /// </summary>
        private bool IsWorkloadShift(in Reading reading)
        {
            return (Math.Abs(reading.hitRateChange) >= RestartThreshold)
                && !IsShielded() && !IsParkTest() && !IsReturnTest();
        }

        /// <summary>Returns whether a retreat or anchor retest is suppressing workload-change detection.</summary>
        private bool IsReturnTest()
        {
            return (retreatLeft > 0) || ((anchor.retestClaim >= 0) && !anchor.returning);
        }

        /// <summary>Returns whether an audit is testing a parked position.</summary>
        private bool IsParkTest()
        {
            return anchor.held && (walk != null) && walk.isAudit;
        }

        /// <summary>Updates the smoothed hit rate and anchor, using the first sample to initialize the rates.</summary>
        private void UpdateRateReferences(in Reading reading)
        {
            if (rates.IsUnseeded())
            {
                rates.Seed(reading.hitRate);
            }
            else
            {
                rates.Update(reading.hitRate);
                anchor.Track(in reading, rates, /* probing: */ IsProbing());
            }
        }

        /// <summary>
        /// Returns whether a probe or its retreat is in progress. A capped retreat may continue across
        /// several samples after the walk ends.
        /// </summary>
        private bool IsProbing()
        {
            return (walk != null) || HasPendingUndo();
        }

        /* --------------- Exceptional Scenarios --------------- */

        /// <summary>Returns whether part of a probe's retreat remains to be applied.</summary>
        private bool HasPendingUndo()
        {
            return undoRemaining != 0;
        }

        /// <summary>
        /// Returns the next stride toward the probe's starting position. Account for the truncated
        /// command, since subtracting fractional strides would leave the retreat short of its target.
        /// </summary>
        private double UndoStride(in Reading reading)
        {
            double stride = reading.CappedStride(undoRemaining);
            undoRemaining -= Common.ToLongSaturating(stride);
            retreatLeft = RetreatCover;
            return step.Commit(stride);
        }

        /// <summary>Returns a capped stride of a return towards the anchor.</summary>
        private double StrideHome(in Reading reading)
        {
            return step.Commit(anchor.StrideHome(in reading));
        }

        /// <summary>
        /// Discards the anchor after a return if its old hit rate is no longer achievable. A return can
        /// reach an obsolete anchor without a large enough rate change to trigger workload detection.
        /// </summary>
        private void RetestReturn(in Reading reading)
        {
            if (anchor.RetestFails(rates))
            {
                // Subsequent anchors must use rate measurements from the new workload
                rates.Reset();
                anchor.StandDown(in reading);
            }
        }

        /// <summary>Returns whether starvation probes are waiting after a retreat.</summary>
        private bool IsBackingOff()
        {
            return refractoryLeft > 0;
        }

        /// <summary>
        /// Starts a due audit or holds the window during backoff. Starvation backoff must not delay
        /// audits, since a persistently starved window would otherwise remain untested.
        /// </summary>
        private double HoldOrAudit(in Reading reading)
        {
            return auditClock.IsDue() ? ArmEquilibriumAudit(in reading) : HoldInRefractory(in reading);
        }

        /// <summary>
        /// Holds the window during backoff, except to raise it to the minimum size. Sparse hits can
        /// produce an extreme density ratio, so steering on this sample would defeat the backoff.
        /// Leave the previous step intact for the next walk.
        /// </summary>
        private double HoldInRefractory(in Reading reading)
        {
            refractoryLeft--;
            return reading.AtLeastFloor(0.0);
        }

        /// <summary>Starts a starvation probe and returns its first step.</summary>
        private double ArmStarvationProbe(in Reading reading)
        {
            var armed = ArmProbe(in reading, reading.ShouldProbeDown(), /* isAudit: */ false);
            return WalkStep(armed, /* entry: */ true, in reading);
        }

        /// <summary>
        /// Starts an audit and returns its first step. Audits explore stable positions where density
        /// steering and starvation probes would otherwise make no progress.
        /// </summary>
        private double ArmEquilibriumAudit(in Reading reading)
        {
            var armed = ArmProbe(in reading, auditClock.ChooseDirection(
                in reading, audit.Stride(in reading), rates.smoothed, anchor.held), /* isAudit: */ true);
            auditClock.Restart();
            return WalkStep(armed, /* entry: */ true, in reading);
        }

        /// <summary>
        /// Records a confirmed position and returns whether the window should remain there. Audit
        /// confirmations suspend density steering, which could undo the improvement. Starvation probes
        /// normally resume steering; <see cref="Walk.IsAuditGrade"/> identifies those with enough
        /// evidence to park as well.
        /// </summary>
        private bool KeepConfirmedPosition(Walk walk, in Reading reading)
        {
            bool park = walk.isAudit || walk.IsAuditGrade(in reading);
            long position = walk.VerdictWindow(in reading);
            anchor.Plant(position, rates.smoothed);
            if (park)
            {
                anchor.Park(AuditClock.AuditWaitInitial);
                if (position != reading.windowMax)
                {
                    anchor.BeginReturn();
                }
            }
            else
            {
                anchor.Release();
            }
            return park;
        }

        /* --------------- Probe Walk --------------- */

        /// <summary>
        /// Starts a probe from the current position and its smoothed hit rate. The anchor's rate may
        /// describe another position or an old workload, so using it could reject real improvements.
        /// </summary>
        private Walk ArmProbe(in Reading reading, bool down, bool isAudit)
        {
            var ladder = isAudit ? audit : starvation;
            var armed = new Walk(ladder, isAudit, down, reading.windowMax,
                reading.requestCount, reading.hitRate, rates.smoothed, reading.probationDensity);
            walk = armed;
            return armed;
        }

        /// <summary>
        /// Returns the next step of the probe. Crossing back through its starting position ends the
        /// probe as a failure; continuing would explore in the opposite direction.
        /// </summary>
        private double WalkStep(Walk walk, bool entry, in Reading reading)
        {
            double stride = NextStride(walk, entry, in reading);
            if (walk.CrossesBase(reading.windowMax + stride))
            {
                EndWalk();
                walk.ladder.crashStreak = 0;
                return UndoProbe(walk, ProbeEnding.Failed, in reading);
            }
            else if ((stride < 0) && ((reading.windowMax + stride) < reading.floor))
            {
                stride = reading.FlooredDescent();
            }
            walk.samples++;
            return step.Commit(stride);
        }

        /// <summary>
        /// Returns the next probe step. While an audit waits to distinguish a crash from noise, keep
        /// its direction and decay the step. Reversing during that wait could cross the starting
        /// position and turn a temporary dip into a completed failure.
        /// </summary>
        private double NextStride(Walk walk, bool entry, in Reading reading)
        {
            double magnitude = walk.ladder.Stride(in reading);
            if (entry)
            {
                return walk.Direction() * magnitude;
            }
            else if (walk.isAudit && (walk.belowBarStreak > 0))
            {
                double held = Step.Decayed(step.size);
                return Step.IsFrozen(held) ? step.AtMinimum() : held;
            }
            double bar = walk.ReversalBar(rates);
            double hitRateChange = reading.hitRateChange;
            double stride = step.Heading(/* forward: */ hitRateChange > -bar);
            stride = (Math.Abs(hitRateChange) >= bar)
                ? Math.CopySign(magnitude, stride)
                : Step.Decayed(stride);
            return Step.IsFrozen(stride) ? (walk.Direction() * magnitude) : stride;
        }

        /// <summary>Checks for a crash, then evaluates the probe's progress.</summary>
        private ProbeEnding ProbeEndingOf(Walk walk, in Reading reading)
        {
            bool belowBar = (reading.hitRate <= (walk.baseHitRate - walk.CrashBar(rates)));
            walk.belowBarStreak = belowBar ? (walk.belowBarStreak + 1) : 0;
            if (walk.ShouldCrashAbort(belowBar))
            {
                // A workload change can resemble probe damage. Retry the first crash without increasing
                // backoff; repeated crashes suggest the probe itself is harmful
                EndWalk();
                walk.ladder.Crash();
                return ProbeEnding.Crashed;
            }
            bool above = (reading.hitRate > (walk.baseSmoothedRate + Rates.VetoMarginMin));
            walk.aboveStreak = above ? (walk.aboveStreak + 1) : 0;
            walk.RememberBest(above, in reading);
            walk.beatBase |= (reading.hitRate >= walk.baseHitRate);
            return walk.isAudit ? AuditEnding(walk) : StarvationEnding(walk, in reading);
        }

        /// <summary>
        /// Evaluates an audit by hit rate. Density already favors the original split, so using it to
        /// judge the audit would reject the alternatives the audit is meant to test.
        /// </summary>
        private ProbeEnding AuditEnding(Walk walk)
        {
            if (walk.IsConfirmed())
            {
                EndWalk();
                walk.ladder.Reset();
                refractoryLeft = 0;
                starvation.Reward();
                auditClock.Settle(walk.down, rates.smoothed);
                return ProbeEnding.Confirmed;
            }
            else if (walk.IsBudgetSpent())
            {
                EndWalk();
                walk.ladder.crashStreak = 0;
                return ProbeEnding.Failed;
            }
            return ProbeEnding.Walking;
        }

        /// <summary>
        /// Evaluates a starvation probe by density. A confirmation only resets the retry ladder if it
        /// makes new progress that steering will keep. Reversed or repeated confirmations escalate the
        /// ladder so later probes can explore beyond the same short excursion.
        /// </summary>
        private ProbeEnding StarvationEnding(Walk walk, in Reading reading)
        {
            if (walk.CanAdjudicate(in reading, starvation.CommitmentDepth()))
            {
                EndWalk();
                walk.ladder.crashStreak = 0;
                if ((walk.VerdictSignal(in reading) * walk.Direction()) > 0.0)
                {
                    if (walk.IsReversedBy(in reading)
                        || walk.ladder.IsRepeat(walk.down, reading.windowMax, reading.band))
                    {
                        walk.ladder.Escalate();
                    }
                    else
                    {
                        walk.ladder.Reward();
                    }
                    walk.ladder.Remember(walk.down, reading.windowMax);
                    refractoryLeft = 0;
                    return ProbeEnding.Confirmed;
                }
                return ProbeEnding.Failed;
            }
            else if (walk.IsBudgetSpent())
            {
                EndWalk();
                walk.ladder.crashStreak = 0;
                return ProbeEnding.Failed;
            }
            return ProbeEnding.Walking;
        }

        /// <summary>
        /// Begins the retreat from a failed or aborted walk and updates its retry schedule. Only
        /// starvation probes reset the starvation backoff; an audit's retreat leaves an existing wait
        /// intact.
        /// </summary>
        private double UndoProbe(Walk walk, ProbeEnding ending, in Reading reading)
        {
            bool crashed = (ending == ProbeEnding.Crashed);
            bool failed = !crashed || walk.ladder.CrashEscalates();
            if (failed)
            {
                walk.ladder.Escalate();
            }
            if (walk.isAudit)
            {
                auditClock.Reschedule(failed, crashed, audit.rung);
            }
            else
            {
                refractoryLeft = starvation.rung;
                starvation.Forget();
            }
            retreatLeft = RetreatCover;
            return ReturnToBase(walk, in reading);
        }

        /// <summary>Returns the stride back to the walk's starting window.</summary>
        private double ReturnToBase(Walk walk, in Reading reading)
        {
            long amount = (walk.baseWindow - reading.windowMax);
            double stride = reading.CappedStride(amount);
            undoRemaining = (amount - Common.ToLongSaturating(stride));
            return step.Commit(stride);
        }

        /// <summary>Ends the walk.</summary>
        private void EndWalk()
        {
            walk = null;
        }

        /// <summary>Returns a proportional density adjustment, respecting the step cap and minimum window.</summary>
        internal double Steer(double error, in Reading r)
        {
            double magnitude = Math.Min(r.MaxStep(), Math.Abs(error) * DensityGain * r.maximum);
            double stride = (error >= 0) ? magnitude : -magnitude;
            double clamped = ((stride < 0) && ((r.windowMax + stride) < r.floor))
                ? r.FlooredDescent()
                : stride;
            return step.Commit(r.AtLeastFloor(clamped));
        }
    }

    /// <summary>The current sample's hit and miss counts, with the previous sample's hit rate.</summary>
    internal sealed class Sample
    {
        internal double previousHitRate;
        internal long probationHits;
        internal long windowHits;
        internal long misses;
        internal long hits;

        /// <summary>
        /// Records a cache hit. Callers must exclude zero-weight entries, whose hits would inflate
        /// density without consuming capacity.
        /// </summary>
        internal void RecordHit(bool inWindow, bool inProbation)
        {
            hits++;
            if (inWindow)
            {
                windowHits++;
            }
            else if (inProbation)
            {
                probationHits++;
            }
        }

        /// <summary>
        /// Records a cache miss, including zero-weight entries. Their misses still represent demand,
        /// so excluding their hits can make the sampled hit rate lower than the user-visible rate.
        /// </summary>
        internal void RecordMiss()
        {
            misses++;
        }

        /// <summary>Returns the requests seen so far.</summary>
        internal long RequestCount()
        {
            return hits + misses;
        }

        /// <summary>Returns the ratio of requests which were hits.</summary>
        internal double HitRate()
        {
            return (double)hits / RequestCount();
        }

        /// <summary>Returns the change in hit rate since the previous sample.</summary>
        internal double HitRateChange()
        {
            return HitRate() - previousHitRate;
        }

        /// <summary>Clears the sample, retaining its hit rate for the next comparison.</summary>
        internal void Close()
        {
            Close(HitRate());
        }

        /// <summary>Discards the sample, retaining the previous hit rate for the next comparison.</summary>
        internal void Discard()
        {
            Close(previousHitRate);
        }

        /// <summary>Discards the sample and cross-sample memory.</summary>
        internal void Reset()
        {
            Close(0.0);
        }

        /// <summary>Clears the counters and sets the reference hit rate.</summary>
        private void Close(double hitRate)
        {
            previousHitRate = hitRate;
            probationHits = 0;
            windowHits = 0;
            misses = 0;
            hits = 0;
        }
    }

    /// <summary>
    /// The measurements and region bounds used for one density adjustment. An immutable struct,
    /// passed by readonly reference, so that a sample allocates nothing.
    /// </summary>
    internal readonly struct Reading
    {
        /// <summary>The minimum hit count used for steering, as a fraction of the starvation threshold.</summary>
        internal const double SteeringFloorFraction = 0.125d;
        /// <summary>The band within which two positions count as the same, as a fraction of the maximum.</summary>
        internal const double StableBandFraction = 0.02d;
        /// <summary>The density law's lower bound on the window, as a fraction of the maximum.</summary>
        internal const double WindowFloorFraction = 0.02d;
        /// <summary>The density climber's largest single step, as a fraction of the maximum.</summary>
        internal const double MaxStepFraction = 0.30d;
        /// <summary>The smoothing constant that keeps a starved region's density ratio defined.</summary>
        internal const double DensityEpsilon = 1e-9d;
        /// <summary>The minimum hit count needed to measure a region, even for short samples.</summary>
        internal const long MinStarvationBar = 4L;
        /// <summary>The right-shift of the sample's request count that sets the starvation bar.</summary>
        internal const int MinSignalShift = 10;

        internal readonly double probationDensity;
        internal readonly double hitRateChange;
        internal readonly double windowDensity;
        internal readonly double mainDensity;
        internal readonly double hitRate;
        internal readonly double floor;

        internal readonly bool windowStarved;
        internal readonly bool mainStarved;

        internal readonly long requestCount;
        internal readonly long windowHits;
        internal readonly long windowMax;
        internal readonly long mainHits;
        internal readonly long maximum;
        internal readonly long band;
        internal readonly long bar;

        internal Reading(Sample sample, long maximum, long windowMax, long mainProtectedMax)
        {
            mainHits = (sample.hits - sample.windowHits);
            bar = Math.Max(MinStarvationBar, sample.RequestCount() >> MinSignalShift);
            mainDensity = mainHits / (double)Math.Max(1L, maximum - windowMax);
            windowDensity = sample.windowHits / (double)Math.Max(1L, windowMax);
            probationDensity = sample.probationHits / (double)Math.Max(
                1L, maximum - windowMax - mainProtectedMax);
            windowStarved = (sample.windowHits < bar);
            hitRateChange = sample.HitRateChange();
            floor = WindowFloorFraction * maximum;
            requestCount = sample.RequestCount();
            mainStarved = (mainHits < bar);
            windowHits = sample.windowHits;
            hitRate = sample.HitRate();
            band = StableBand(maximum);
            this.windowMax = windowMax;
            this.maximum = maximum;
        }

        /// <summary>Returns the band, in entries, within which a position counts as the same place.</summary>
        internal static long StableBand(long maximum)
        {
            return Math.Max(1L, Common.ToLongSaturating(StableBandFraction * maximum));
        }

        /// <summary>Returns the smallest window that leaves main at most a quarter of the cache.</summary>
        internal long UpperCorner()
        {
            return maximum - (maximum >>> 2);
        }

        /// <summary>Returns the largest single move any command may make at this size.</summary>
        internal double MaxStep()
        {
            return MaxStepFraction * maximum;
        }

        /// <summary>Returns a stride towards a target this far away, capped at the maximum step.</summary>
        internal double CappedStride(double amount)
        {
            double cap = MaxStep();
            return (Math.Abs(amount) > cap) ? Math.CopySign(cap, amount) : amount;
        }

        /// <summary>Returns the initial step size for this cache.</summary>
        internal double RestartMagnitude()
        {
            return Step.RestartMagnitude(maximum);
        }

        /// <summary>Returns a descent limited by the floor, preserving negative zero to retain its direction.</summary>
        internal double FlooredDescent()
        {
            return Math.Min(-0.0d, floor - windowMax);
        }

        /// <summary>Increases the stride if needed to raise an undersized window to the floor.</summary>
        internal double AtLeastFloor(double stride)
        {
            return (windowMax < floor) ? Math.Max(stride, floor - windowMax) : stride;
        }

        /// <summary>Returns whether neither region has enough hits for a reliable density estimate.</summary>
        internal bool IsDeadSample()
        {
            return windowStarved && mainStarved;
        }

        /// <summary>
        /// Returns whether sparse hits require a starvation probe. A starved main beside a large window
        /// is left to equilibrium audits; probing it could shrink a productive window.
        /// </summary>
        internal bool HasBlindCorner()
        {
            return IsDeadSample() || (windowStarved && (windowMax <= (maximum >>> 2)));
        }

        /// <summary>Returns whether the probe should shrink the window to move away from the nearer bound.</summary>
        internal bool ShouldProbeDown()
        {
            return IsDeadSample() && (windowMax >= (maximum >>> 1));
        }

        /// <summary>Returns the log density ratio used by probe verdicts.</summary>
        internal double Error()
        {
            return Math.Log((windowDensity + DensityEpsilon) / (mainDensity + DensityEpsilon));
        }

        /// <summary>Returns the log density ratio with minimum hit counts to limit sparse-sample steps.</summary>
        internal double SteeringError()
        {
            double windowFloor = (SteeringFloorFraction * bar) / Math.Max(1L, windowMax);
            double mainFloor = (SteeringFloorFraction * bar) / Math.Max(1L, maximum - windowMax);
            return Math.Log(Math.Max(windowDensity, windowFloor) / Math.Max(mainDensity, mainFloor));
        }
    }

    /// <summary>The signed adjustment used to continue, reverse, or decay the climber's next step.</summary>
    internal sealed class Step
    {
        /// <summary>Lower bound on the initial step size so that small caches have an opportunity to adapt.</summary>
        internal const double MinInitialStep = 2.0d;
        /// <summary>The initial step size as a fraction of the cache's maximum size.</summary>
        internal const double StepPercent = 0.0625d;
        /// <summary>The multiplier used to reduce the step size toward convergence.</summary>
        internal const double StepDecayRate = 0.98d;

        internal double size;

        /// <summary>Creates a step with the initial magnitude and direction.</summary>
        internal Step(long maximum, bool grow)
        {
            double magnitude = RestartMagnitude(maximum);
            size = grow ? magnitude : -magnitude;
        }

        /// <summary>Records and returns the step for this sample.</summary>
        internal double Commit(double step)
        {
            size = step;
            return step;
        }

        /// <summary>Returns the last step, repeated or reversed.</summary>
        internal double Heading(bool forward)
        {
            return forward ? size : -size;
        }

        /// <summary>Returns the step's size, ignoring direction.</summary>
        internal double Magnitude()
        {
            return Math.Abs(size);
        }

        /// <summary>Returns the minimum step size in the current direction.</summary>
        internal double AtMinimum()
        {
            return Math.CopySign(MinInitialStep, size);
        }

        /// <summary>Returns the initial step size for the given maximum.</summary>
        internal static double RestartMagnitude(long maximum)
        {
            return Math.Max(StepPercent * maximum, MinInitialStep);
        }

        /// <summary>Returns the step reduced by one decay factor.</summary>
        internal static double Decayed(double step)
        {
            return StepDecayRate * step;
        }

        /// <summary>
        /// Returns whether the step needs to be restarted. In the density tier, the floor clamp can
        /// reduce a step this far, but decay alone cannot do so within a walk's budget.
        /// </summary>
        internal static bool IsFrozen(double step)
        {
            return Math.Abs(step) < MinInitialStep;
        }
    }

    /// <summary>A bounded sequence of window adjustments used by a starvation probe or equilibrium audit.</summary>
    internal sealed class Walk
    {
        /*
         * The crash and reversal thresholds test different losses: the drop from the starting hit rate,
         * and the drop since the previous sample. A starvation probe scales both thresholds with the
         * workload's deviation, subject to a floor and cap. The deviation stays live so the walk can
         * tolerate its own transient effects; freezing it at the start aborts useful walks.
         *
         * An audit's crash threshold stays absolute. Widening it with the deviation allows too many
         * audits to confirm and hold positions that density would reject. At low hit rates the
         * threshold is capped at a fraction of the starting rate, since an absolute threshold could
         * exceed the entire rate and never detect a loss.
         *
         * The audit's reversal threshold also accounts for deviation to avoid reversing on ordinary
         * sample noise. The absolute cap prevents excessive tolerance on noisy workloads. A reversal
         * through the starting window counts as a failed walk and increases the retry wait.
         *
         * Starvation probes abort on the first sample below the crash threshold. An audit retry after a
         * crash requires consecutive such samples, allowing it to cross a temporary dip. Extending this
         * duration tolerates brief losses without raising the allowed drop.
         *
         * Audit confirmation requires a streak of raw samples above the starting smoothed rate, after a
         * minimum number of steps. Scaling this margin by deviation, as the guard rail does, would
         * obscure the window's small contribution to the total rate. A false confirmation can be
         * corrected by the next audit; a false veto repeatedly returns to the anchor. The walk must
         * also match its starting raw rate at least once, since that sample may already exceed the
         * smoothed reference. Equality is allowed so a perfect starting rate can still be confirmed.
         *
         * A starvation probe compares densities once the watched region has enough hits. An up-probe
         * uses the initial probation density: growing the window displaces probation entries, while
         * main's average overvalues that capacity by including the protected core. The baseline is
         * frozen because the walk's own demotions inflate live probation density and can prevent any
         * confirmation. A down-probe uses average densities; the window has no comparable subdivision.
         *
         * Every walk has a sample budget. Neither the density verdict nor the hit-rate thresholds
         * guarantee termination, and a walk may continue making small losses below those thresholds.
         */

        /// <summary>
        /// The scale shared by the audit's crash and reversal thresholds. The crash threshold uses the
        /// starting rate; the reversal threshold uses the larger of that rate and the noise band.
        /// </summary>
        internal const double AuditBarFraction = 0.15d;
        /// <summary>The consecutive below-threshold samples required to abort an audit retry after a crash.</summary>
        internal const int AuditCrashPersistence = 3;
        /// <summary>The consecutive raw samples above the frozen reference that confirm an audit.</summary>
        internal const int AuditConfirmStreak = 4;
        /// <summary>The minimum samples before an audit can confirm.</summary>
        internal const int AuditCommitment = 5;
        /// <summary>The hit count required for a density verdict, as a multiple of the starvation threshold.</summary>
        internal const long ProbeExitBarMultiple = 4L;
        /// <summary>The starvation probe's maximum crash threshold, in multiples of the restart threshold.</summary>
        internal const double ProbeBarCap = 3.0d;
        /// <summary>The maximum samples before an unconfirmed walk fails.</summary>
        internal const int ProbeWalkBudget = 16;

        internal readonly double baseProbationDensity;
        internal readonly double baseSmoothedRate;
        internal readonly long baseRequestCount;
        internal readonly double baseHitRate;
        internal readonly long baseWindow;

        internal readonly bool isAudit;
        internal readonly Ladder ladder;
        internal readonly bool down;

        internal int belowBarStreak;
        internal int aboveStreak;
        internal int samples;

        internal bool beatBase;
        internal double bestRate;
        internal long bestWindow;

        internal Walk(Ladder ladder, bool isAudit, bool down, long baseWindow, long baseRequestCount,
            double baseHitRate, double baseSmoothedRate, double baseProbationDensity)
        {
            this.baseProbationDensity = baseProbationDensity;
            this.baseSmoothedRate = baseSmoothedRate;
            this.baseRequestCount = baseRequestCount;
            this.baseHitRate = baseHitRate;
            this.baseWindow = baseWindow;
            this.isAudit = isAudit;
            this.ladder = ladder;
            bestWindow = -1;
            bestRate = -1.0;
            this.down = down;
        }

        /// <summary>
        /// Records the best position within the current confirmation streak. A broken streak clears the
        /// record, and ties favor the later position to avoid unnecessary backtracking.
        /// </summary>
        internal void RememberBest(bool above, in Reading r)
        {
            if (!above)
            {
                bestWindow = -1;
                bestRate = -1.0;
            }
            else if (r.hitRate >= bestRate)
            {
                bestWindow = r.windowMax;
                bestRate = r.hitRate;
            }
        }

        /// <summary>
        /// Returns the confirmed position. An audit may have passed a better position while collecting
        /// its confirmation streak, so it returns there if the difference exceeds the confirmation
        /// margin. A starvation probe evaluates the current sample and keeps its position.
        /// </summary>
        internal long VerdictWindow(in Reading r)
        {
            bool better = isAudit && (bestWindow >= 0)
                && (bestRate > (r.hitRate + Rates.VetoMarginMin));
            return better ? bestWindow : r.windowMax;
        }

        /// <summary>Returns the sign of the walk's direction.</summary>
        internal double Direction()
        {
            return down ? -1.0 : 1.0;
        }

        /// <summary>Returns whether the walk has exhausted its sample budget.</summary>
        internal bool IsBudgetSpent()
        {
            return samples >= ProbeWalkBudget;
        }

        /// <summary>Returns whether the proposed position crosses back through the starting window.</summary>
        internal bool CrossesBase(double position)
        {
            return down ? (position > baseWindow) : (position < baseWindow);
        }

        /// <summary>Returns the drop from the starting hit rate that makes a sample count as a crash.</summary>
        internal double CrashBar(Rates rates)
        {
            return isAudit
                ? Math.Min(RestartThreshold, AuditBarFraction * baseHitRate)
                : Math.Min(ProbeBarCap * RestartThreshold,
                    Math.Max(RestartThreshold, rates.NoiseBand()));
        }

        /// <summary>Returns the drop since the previous sample that reverses the walk's step.</summary>
        internal double ReversalBar(Rates rates)
        {
            return isAudit
                ? Math.Min(RestartThreshold,
                    AuditBarFraction * Math.Max(baseHitRate, rates.NoiseBand()))
                : CrashBar(rates);
        }

        /// <summary>
        /// Returns whether a starvation probe has enough hits and has reached the required depth for
        /// a density verdict.
        /// </summary>
        internal bool CanAdjudicate(in Reading r, int commitment)
        {
            long watched = down ? r.mainHits : r.windowHits;
            return (watched >= (ProbeExitBarMultiple * r.bar)) && (samples >= commitment);
        }

        /// <summary>Returns the density comparison, scaling the frozen baseline to the current sample length.</summary>
        internal double VerdictSignal(in Reading r)
        {
            if (down)
            {
                return r.Error();
            }
            double baseline = baseProbationDensity
                * ((double)r.requestCount / Math.Max(1L, baseRequestCount));
            return Math.Log((r.windowDensity + Reading.DensityEpsilon)
                / (baseline + Reading.DensityEpsilon));
        }

        /// <summary>Returns whether to abort on this loss, allowing an audit retry to tolerate brief dips.</summary>
        internal bool ShouldCrashAbort(bool belowBar)
        {
            bool tolerant = isAudit && ladder.HasCrashed();
            return belowBar && (!tolerant || (belowBarStreak >= AuditCrashPersistence));
        }

        /// <summary>Returns whether the walk meets the audit's depth, streak, and starting-rate tests.</summary>
        internal bool IsConfirmed()
        {
            return (samples >= AuditCommitment) && (aboveStreak >= AuditConfirmStreak) && beatBase;
        }

        /// <summary>Returns whether density steering would immediately move back from the confirmed position.</summary>
        internal bool IsReversedBy(in Reading r)
        {
            return (r.SteeringError() * Direction()) < 0.0;
        }

        /// <summary>
        /// Returns whether a deep starvation walk meets the audit's confirmation tests but would be
        /// reversed by density steering. This position must be held to retain the improvement.
        /// </summary>
        internal bool IsAuditGrade(in Reading r)
        {
            return (samples >= Ladder.ProbeCommitmentDeep) && IsReversedBy(in r) && IsConfirmed();
        }
    }

    /// <summary>
    /// Retry state for starvation probes or audits. Repeated unsuccessful walks increase both the wait
    /// and the extent of exploration. Each layer has its own ladder so failures in one do not delay
    /// exploration by the other.
    /// </summary>
    internal sealed class Ladder
    {
        /// <summary>The stride multiplier after one backoff doubling.</summary>
        internal const double ProbeStrideScaleMid = 2.0d;
        /// <summary>The stride multiplier at the maximum backoff.</summary>
        internal const double ProbeStrideScaleDeep = 4.0d;
        /// <summary>The minimum samples before a density verdict after one backoff doubling.</summary>
        internal const int ProbeCommitmentMid = 2;
        /// <summary>The minimum samples before a density verdict at the maximum backoff.</summary>
        internal const int ProbeCommitmentDeep = 10;
        /// <summary>The consecutive crashes before a retry escalates as it would after a failed walk.</summary>
        internal const int ProbeCrashEscalation = 2;
        /// <summary>The initial backoff, in samples, before escalation.</summary>
        internal const int ProbeBackoffInitial = 16;
        /// <summary>The maximum backoff rung, in samples.</summary>
        internal const int ProbeBackoffMax = 64;

        internal bool farthestDown;
        internal int crashStreak;
        internal long farthest;
        internal int rung;

        internal Ladder()
        {
            Reset();
        }

        /// <summary>Restores the initial backoff and clears the walk history.</summary>
        internal void Reset()
        {
            rung = ProbeBackoffInitial;
            crashStreak = 0;
            Forget();
        }

        /// <summary>Clears the farthest confirmed position.</summary>
        internal void Forget()
        {
            farthest = -1;
        }

        /// <summary>
        /// Returns whether this confirmation makes no further progress than an earlier walk in the same
        /// direction. Repeatedly recovering a lost position should not reset the backoff.
        /// </summary>
        internal bool IsRepeat(bool down, long window, long band)
        {
            if ((farthest < 0) || (down != farthestDown))
            {
                return false;
            }
            return down ? (window >= (farthest - band)) : (window <= (farthest + band));
        }

        /// <summary>Records a confirmed window that lies beyond the farthest, or in the other direction.</summary>
        internal void Remember(bool down, long window)
        {
            bool farther = (farthest < 0) || (down != farthestDown)
                || (down ? (window < farthest) : (window > farthest));
            if (farther)
            {
                farthestDown = down;
                farthest = window;
            }
        }

        /// <summary>Doubles the backoff, up to its maximum.</summary>
        internal void Escalate()
        {
            rung = Math.Min(ProbeBackoffMax, 2 * rung);
        }

        /// <summary>Records a consecutive crash, saturating at the escalation threshold.</summary>
        internal void Crash()
        {
            crashStreak = Math.Min(ProbeCrashEscalation, crashStreak + 1);
        }

        /// <summary>Resets the crash streak and shortens the next retry after a successful walk.</summary>
        internal void Reward()
        {
            crashStreak = 0;
            rung = 1;
        }

        /// <summary>
        /// Returns the minimum samples before a density verdict. Early retries can stop cheaply, while
        /// deeper retries continue past incidental hits that would otherwise end exploration too soon.
        /// </summary>
        internal int CommitmentDepth()
        {
            return (rung >= ProbeBackoffMax)
                ? ProbeCommitmentDeep
                : (rung >= (2 * ProbeBackoffInitial)) ? ProbeCommitmentMid : 0;
        }

        /// <summary>
        /// Returns the starting stride for this rung. The audit's direction check uses this same
        /// distance so it does not choose a direction with too little room for the first step.
        /// </summary>
        internal double Stride(in Reading r)
        {
            return Math.Min(r.MaxStep(), StrideScale() * r.RestartMagnitude());
        }

        /// <summary>
        /// Returns the stride multiplier. Deeper retries need larger steps as well as more samples to
        /// reach beyond broad regions of incidental hits.
        /// </summary>
        private double StrideScale()
        {
            return (rung >= ProbeBackoffMax)
                ? ProbeStrideScaleDeep
                : (rung >= (2 * ProbeBackoffInitial)) ? ProbeStrideScaleMid : 1;
        }

        /// <summary>Returns whether this layer's last walk crashed.</summary>
        internal bool HasCrashed()
        {
            return crashStreak >= 1;
        }

        /// <summary>Returns whether consecutive crashes require the same backoff as a failed walk.</summary>
        internal bool CrashEscalates()
        {
            return crashStreak >= ProbeCrashEscalation;
        }
    }

    /// <summary>
    /// The timing and direction of equilibrium audits. Stillness is measured by the window's actual
    /// position, since changing hit rates or adjustments blocked by a boundary can conceal a stable
    /// allocation that needs testing.
    /// </summary>
    internal sealed class AuditClock
    {
        /// <summary>The stillness samples before a routine equilibrium audit.</summary>
        internal const int AuditWaitInitial = 32;
        /// <summary>The shorter wait for the first audit after a resize.</summary>
        internal const int AuditWaitFirst = 4;
        /// <summary>The longest wait between audits.</summary>
        internal const int AuditWaitMax = 512;

        internal double settledRate;
        internal int stillSamples;
        internal int waitSamples;
        internal long lastWindow;
        internal bool down;

        internal AuditClock()
        {
            down = true;
            Reset();
        }

        /// <summary>Restores the initial schedule without changing the preferred direction.</summary>
        internal void Reset()
        {
            waitSamples = AuditWaitFirst;
            settledRate = double.NaN;
            stillSamples = 0;
            lastWindow = -1;
        }

        /// <summary>Restarts the stillness count when an audit begins.</summary>
        internal void Restart()
        {
            stillSamples = 0;
        }

        /// <summary>
        /// Restores the standard wait and continues in the confirmed walk's direction. Confirmation
        /// stopped the walk while it was improving, so the next audit tests for further improvement.
        /// </summary>
        internal void Settle(bool down, double rate)
        {
            waitSamples = AuditWaitInitial;
            settledRate = rate;
            this.down = down;
        }

        /// <summary>
        /// Updates the stillness count. A moving sample decrements the count rather than resetting it,
        /// so occasional movement cannot suppress audits indefinitely. The first sample counts as still
        /// because no adjustment precedes it.
        /// </summary>
        internal void Tick(long windowMax, long band)
        {
            bool samePlace = (lastWindow < 0) || (Math.Abs(windowMax - lastWindow) <= band);
            stillSamples = samePlace
                ? (Math.Min(int.MaxValue - 1, stillSamples) + 1)
                : Math.Max(0, stillSamples - 1);
            lastWindow = windowMax;
        }

        /// <summary>Returns whether the position has been still long enough for an audit.</summary>
        internal bool IsDue()
        {
            return stillSamples >= waitSamples;
        }

        /// <summary>
        /// Sets the next wait from the audit's backoff. A completed failure at the deepest rung doubles
        /// the wait. A crash uses the rung directly, since a workload shift calls for another audit
        /// without that extra delay.
        /// </summary>
        internal void Reschedule(bool failed, bool crashed, int rung)
        {
            waitSamples = (failed && !crashed && (rung >= Ladder.ProbeBackoffMax))
                ? Math.Min(AuditWaitMax, 2 * Math.Max(waitSamples, rung))
                : Math.Max(Ladder.ProbeBackoffInitial, rung);
        }

        /// <summary>
        /// Returns an audit direction with room for the first stride. The first audit after a
        /// confirmation continues the walk only while the position is held and its smoothed rate remains
        /// close to the confirmed rate. Otherwise, exploration alternates directions. A corner-forced
        /// direction leaves the alternation unchanged to avoid retracing it later.
        /// </summary>
        internal bool ChooseDirection(in Reading r, double stride, double rate, bool parked)
        {
            if (!double.IsNaN(settledRate))
            {
                if (!parked || (Math.Abs(rate - settledRate) >= RestartThreshold))
                {
                    down = !down;
                }
                settledRate = double.NaN;
            }
            if (r.windowMax <= Common.ToLongSaturating(2 * r.floor))
            {
                return false;
            }
            else if (r.windowMax >= r.UpperCorner())
            {
                return true;
            }
            double room = down ? (r.windowMax - r.floor) : (r.UpperCorner() - r.windowMax);
            if (room < stride)
            {
                down = !down;
            }
            bool chosen = down;
            down = !chosen;
            return chosen;
        }
    }

    /// <summary>
    /// A reference position and hit rate used to reject sustained regressions. It can hold a confirmed
    /// position or return to it after a loss. This supplements density steering, whose equilibrium
    /// need not maximize the hit rate.
    /// </summary>
    internal sealed class Anchor
    {
        /*
         * A confirmed position has a grace period for large hit-rate changes. That period belongs to
         * its hold and ends when the hold is released. A veto also holds the window, but does not renew
         * the grace period because returning to an old position is not new evidence for it.
         *
         * A return rechecks the reference rate on arrival. The retest freezes the rate at departure and
         * is valid only for that position; discarding or replacing the anchor cancels it.
         */

        /// <summary>The maximum samples allowed for a return to the anchor.</summary>
        internal const int VetoReturnBudget = 8;
        /// <summary>The settling samples at the anchor before its reference rate is retested.</summary>
        internal const int RetestSettle = 2;
        /// <summary>The shortfall samples that sustain a guard-rail veto.</summary>
        internal const int VetoStreak = 4;

        internal int shortfallStreak;
        internal double retestClaim;
        internal bool returning;
        internal int returnLeft;
        internal int settleLeft;
        internal int freshLeft;
        internal bool held;
        internal long window;
        internal double rate;

        internal Anchor()
        {
            Reset();
        }

        /// <summary>Clears the reference position and any pending return.</summary>
        internal void Reset()
        {
            shortfallStreak = 0;
            returnLeft = 0;
            EndReturn();
            Discard();
        }

        /// <summary>Discards the reference position, releasing its hold and cancelling any pending retest.</summary>
        internal void Discard()
        {
            Release();
            EndRetest();
            window = -1;
        }

        /// <summary>Returns whether a reference position has been recorded.</summary>
        internal bool IsPlanted()
        {
            return window >= 0;
        }

        /// <summary>Returns whether the window is within the tolerance band of the anchor.</summary>
        internal bool IsAt(long windowMax, long band)
        {
            return IsPlanted() && (Math.Abs(windowMax - window) <= band);
        }

        /// <summary>Returns whether the window is outside the tolerance band of an existing anchor.</summary>
        internal bool IsAwayFrom(long windowMax, long band)
        {
            return IsPlanted() && !IsAt(windowMax, band);
        }

        /// <summary>
        /// Records a position and its reference rate. Any pending retest is cancelled because its frozen
        /// rate belongs to the previous anchor, even if the new anchor is at the current window.
        /// </summary>
        internal void Plant(long windowMax, double claimed)
        {
            window = windowMax;
            rate = claimed;
            EndRetest();
        }

        /// <summary>Refreshes the reference rate from a measurement at the anchor.</summary>
        internal void Resync(double claimed)
        {
            rate = claimed;
        }

        /// <summary>
        /// Refreshes the rate at the anchor or records a better position. A new position must be settled
        /// so its reference rate does not come from a walk or return still in progress. Refreshing at
        /// the anchor is always allowed; later samples gradually remove any transient measurements.
        /// </summary>
        internal void Track(in Reading r, Rates rates, bool probing)
        {
            bool settled = !probing && !returning;
            if (!IsPlanted())
            {
                if (settled)
                {
                    Plant(r.windowMax, rates.smoothed);
                }
            }
            else if (IsAt(r.windowMax, r.band))
            {
                Resync(rates.smoothed);
            }
            else if (settled && (rates.smoothed > (rate + rates.VetoMargin())))
            {
                Plant(r.windowMax, rates.smoothed);
                Release();
            }
        }

        /// <summary>Starts a hold with a grace period for large hit-rate changes after confirmation.</summary>
        internal void Park(int shield)
        {
            freshLeft = shield;
            held = true;
        }

        /// <summary>Starts a guard-rail hold without changing the remaining grace period.</summary>
        internal void Hold()
        {
            held = true;
        }

        /// <summary>Releases the hold and ends its grace period.</summary>
        internal void Release()
        {
            freshLeft = 0;
            held = false;
        }

        /// <summary>Returns whether a confirmed position is still held within its grace period.</summary>
        internal bool IsShielded()
        {
            return held && (freshLeft > 0);
        }

        /// <summary>Consumes one sample of the grace period.</summary>
        internal void AgeShield()
        {
            freshLeft--;
        }

        /// <summary>Begins a return and reports a veto after a sustained shortfall exceeds the noise margin.</summary>
        internal bool VetoTriggered(in Reading r, Rates rates)
        {
            if (IsAwayFrom(r.windowMax, r.band) && (rates.smoothed < (rate - rates.VetoMargin())))
            {
                shortfallStreak++;
                if (shortfallStreak >= VetoStreak)
                {
                    shortfallStreak = 0;
                    BeginReturn();
                    return true;
                }
            }
            else
            {
                shortfallStreak = 0;
            }
            return false;
        }

        /// <summary>
        /// Releases the hold and stops any return, reporting whether the anchor was discarded. Only
        /// a change at the anchor invalidates its rate; a change elsewhere may result from the
        /// controller's own movement.
        /// </summary>
        internal bool StandDown(in Reading r)
        {
            bool discarded = IsAt(r.windowMax, r.band);
            if (discarded)
            {
                Discard();
            }
            Release();
            EndReturn();
            shortfallStreak = 0;
            return discarded;
        }

        /// <summary>Begins a bounded return to the anchor and freezes its rate for the retest on arrival.</summary>
        internal void BeginReturn()
        {
            returnLeft = VetoReturnBudget;
            settleLeft = RetestSettle;
            retestClaim = rate;
            returning = true;
            Hold();
        }

        /// <summary>
        /// Returns a capped step toward the anchor. The return ends when the step reaches the anchor or
        /// uses the last budgeted sample.
        /// </summary>
        internal double StrideHome(in Reading r)
        {
            returnLeft--;
            double remaining = (window - r.windowMax);
            if ((Math.Abs(remaining) <= r.MaxStep()) || (returnLeft <= 0))
            {
                EndReturn();
            }
            return r.CappedStride(remaining);
        }

        /// <summary>Ends the return, including one that exhausted its budget before arrival.</summary>
        internal void EndReturn()
        {
            returning = false;
        }

        /// <summary>
        /// Returns whether the return has ended at the anchor with a retest pending. An incomplete
        /// return cannot test the reference rate and cancels the retest.
        /// </summary>
        internal bool IsRetestDue(in Reading r)
        {
            if ((retestClaim < 0) || returning)
            {
                return false;
            }
            else if (!IsAt(r.windowMax, r.band))
            {
                EndRetest();
                return false;
            }
            return true;
        }

        /// <summary>
        /// Returns whether the settled rate falls short of the reference frozen at departure. Using the
        /// live reference would hide the shortfall as measurements at the anchor refresh it.
        /// </summary>
        internal bool RetestFails(Rates rates)
        {
            if (--settleLeft > 0)
            {
                return false;
            }
            double claimed = retestClaim;
            EndRetest();
            return rates.smoothed < (claimed - rates.VetoMargin());
        }

        /// <summary>Clears a completed or cancelled retest.</summary>
        internal void EndRetest()
        {
            retestClaim = -1;
            settleLeft = 0;
        }
    }

    /// <summary>
    /// A smoothed hit rate and mean absolute deviation. The deviation determines the noise margin for
    /// guard-rail vetoes and starvation probes; audit confirmation uses a fixed margin instead.
    /// </summary>
    internal sealed class Rates
    {
        /// <summary>The shortfall margin as a multiple of the smoothed hit-rate deviation.</summary>
        internal const double VetoMarginScale = 3.0d;
        /// <summary>The floor on the guard rail's shortfall margin, in absolute hit rate.</summary>
        internal const double VetoMarginMin = 0.01d;
        /// <summary>The initial deviation estimate, allowing for cold-start variability.</summary>
        internal const double DeviationSeed = 0.05d;
        /// <summary>The smoothing constant for the goal-metric references (~5 sample memory).</summary>
        internal const double RateSmoothing = 0.2d;

        internal double deviation;
        internal double smoothed;

        internal Rates()
        {
            Reset();
        }

        /// <summary>Clears the smoothed rate and restores the initial deviation.</summary>
        internal void Reset()
        {
            smoothed = double.NaN;
            deviation = DeviationSeed;
        }

        /// <summary>Returns whether the initial hit rate has yet to be recorded.</summary>
        internal bool IsUnseeded()
        {
            return double.IsNaN(smoothed);
        }

        /// <summary>Starts the smoothed rate at this sample.</summary>
        internal void Seed(double hitRate)
        {
            smoothed = hitRate;
        }

        /// <summary>Updates both estimates, measuring deviation from the previous smoothed rate.</summary>
        internal void Update(double hitRate)
        {
            deviation += RateSmoothing * (Math.Abs(hitRate - smoothed) - deviation);
            smoothed += RateSmoothing * (hitRate - smoothed);
        }

        /// <summary>
        /// Returns a noise margin of three current deviations. Walks use the live estimate to tolerate
        /// their own transient effects; see the implementation notes in <see cref="Walk"/>.
        /// </summary>
        internal double NoiseBand()
        {
            return VetoMarginScale * deviation;
        }

        /// <summary>Returns the guard rail's noise margin, with a minimum for quiet workloads.</summary>
        internal double VetoMargin()
        {
            return Math.Max(VetoMarginMin, NoiseBand());
        }
    }

    /// <summary>The outcome of a walk sample.</summary>
    private enum ProbeEnding
    {
        /// <summary>The walk continues with another step.</summary>
        Walking,
        /// <summary>The walk's confirmation criteria were met.</summary>
        Confirmed,
        /// <summary>A hit-rate loss requires returning to the starting position.</summary>
        Crashed,
        /// <summary>The walk failed to confirm an improvement and requires a longer backoff.</summary>
        Failed,
    }
}
