using System;
using Espresso.Internal;
using static Espresso.Internal.WindowClimber;
using static Espresso.Internal.WindowClimber.Anchor;
using static Espresso.Internal.WindowClimber.AuditClock;
using static Espresso.Internal.WindowClimber.DensityClimber;
using static Espresso.Internal.WindowClimber.Ladder;
using static Espresso.Internal.WindowClimber.Reading;
using static Espresso.Internal.WindowClimber.Step;
using static Espresso.Internal.WindowClimber.Walk;
using static Espresso.Tests.FluentAssert;

namespace Espresso.Tests;

/// <summary>
/// The window climber's state-machine invariants, asserted from the climber alone. Both the
/// randomized climber test and the cache-level climber tests run this, so both share one
/// definition.
/// </summary>
internal static class ClimberInvariants
{
    /// <summary>Asserts every invariant that holds of the climber at rest between samples.</summary>
    public static void AssertInvariants(WindowClimber climber, long maximum)
    {
        AssertWithMessage("counters are non-negative")
            .That(climber.sample.hits).IsAtLeast(0);
        AssertWithMessage("counters are non-negative")
            .That(climber.sample.misses).IsAtLeast(0);
        AssertWithMessage("region hits cannot exceed the sample's hits")
            .That(climber.sample.windowHits + climber.sample.probationHits)
            .IsAtMost(climber.sample.hits);

        // the cap is a double the climber never truncates, so recomputing it in entries here needs one
        // of headroom. The bound is stricter than the adjudicated reality: a negative policyWeight
        // inflates the transfer loop's quota and carries the excess back, a sanctioned transient that
        // no subject-validated test drives.
        long bound = Common.ToLongSaturating(Math.Max(MinInitialStep, (MaxStepFraction * maximum))) + 1;
        AssertWithMessage("the adjustment honors the maximum step")
            .That(Math.Abs(climber.Adjustment())).IsAtMost(bound);
        AssertWithMessage("the step size honors the maximum step")
            .That(Math.Abs(climber.tier.step.size)).IsAtMost((double)bound);
        AssertWithMessage("the step size is finite")
            .That(double.IsFinite(climber.tier.step.size)).IsTrue();

        var density = climber.tier as DensityClimber;
        AssertWithMessage("the tier is the one bound for this maximum")
            .That(density != null).IsEqualTo(DensityClimber.AppliesTo(maximum));
        if (density == null)
        {
            return;
        }

        AssertWithMessage("the starvation rung stays within its schedule")
            .That(density.starvation.rung).IsAtLeast(1);
        AssertWithMessage("the starvation rung stays within its schedule")
            .That(density.starvation.rung).IsAtMost(ProbeBackoffMax);
        AssertWithMessage("the countdown is non-negative")
            .That(density.refractoryLeft).IsAtLeast(0);
        AssertWithMessage("a retreat's cover stays within its span")
            .That(density.retreatLeft).IsAtLeast(0);
        AssertWithMessage("a retreat's cover stays within its span")
            .That(density.retreatLeft).IsAtMost(RetreatCover);
        AssertWithMessage("the refractory countdown is bounded by its own rung")
            .That(density.refractoryLeft).IsAtMost(density.starvation.rung);

        var walk = density.walk;
        if (walk != null)
        {
            AssertWithMessage("a walk is bounded by its budget")
                .That(walk.samples).IsAtLeast(0);
            AssertWithMessage("a walk is bounded by its budget")
                .That(walk.samples).IsAtMost(ProbeWalkBudget);
            AssertWithMessage("a below-bar run is adjudicated at the audit persistence")
                .That(walk.belowBarStreak).IsAtLeast(0);
            // the streak is incremented before the abort test, and the abort ends the walk, so a walk
            // that is still visible has not yet reached the persistence
            AssertWithMessage("a below-bar run is adjudicated at the audit persistence")
                .That(walk.belowBarStreak).IsAtMost(AuditCrashPersistence - 1);
            AssertWithMessage("a walk's ledger is the one its own layer owns")
                .That(walk.ladder).IsSameInstanceAs(walk.isAudit ? density.audit : density.starvation);
            AssertWithMessage("a walk's best sample is a window it stood on, or unset")
                .That(walk.bestWindow).IsAtLeast(-1L);
            AssertWithMessage("a walk's best sample is a window it stood on, or unset")
                .That(walk.bestWindow).IsAtMost(maximum);
            AssertWithMessage("a walk's best sample carries the rate that position earned")
                .That((walk.bestWindow < 0) == (walk.bestRate < 0.0)).IsTrue();
            AssertWithMessage("a walk's best rate is a rate")
                .That(walk.bestRate).IsAtMost(1.0);
            AssertWithMessage("the frozen probation baseline is a density")
                .That(walk.baseProbationDensity).IsAtLeast(0.0);
            AssertWithMessage("the frozen probation baseline is a density")
                .That(double.IsFinite(walk.baseProbationDensity)).IsTrue();
            // it re-expresses that baseline at the live sample's length, so it divides
            AssertWithMessage("the frozen sample length is positive")
                .That(walk.baseRequestCount).IsGreaterThan(0L);
        }

        AssertWithMessage("a ladder's memory is a window or unset")
            .That(density.starvation.farthest).IsAtLeast(-1L);
        AssertWithMessage("a ladder's memory is a window or unset")
            .That(density.starvation.farthest).IsAtMost(maximum);
        // the audit layer's endings are adjudicated by the goal metric and never consult the memory
        AssertWithMessage("the audit ladder keeps no memory")
            .That(density.audit.farthest).IsEqualTo(-1L);

        AssertWithMessage("the audit rung stays within its schedule")
            .That(density.audit.rung).IsAtLeast(ProbeBackoffInitial);
        AssertWithMessage("the audit rung stays within its schedule")
            .That(density.audit.rung).IsAtMost(ProbeBackoffMax);
        AssertWithMessage("each ledger's crash streak is non-negative")
            .That(density.audit.crashStreak).IsAtLeast(0);
        AssertWithMessage("each ledger's crash streak is non-negative")
            .That(density.starvation.crashStreak).IsAtLeast(0);
        AssertWithMessage("each ledger's crash streak saturates once it escalates")
            .That(density.audit.crashStreak).IsAtMost(ProbeCrashEscalation);
        AssertWithMessage("each ledger's crash streak saturates once it escalates")
            .That(density.starvation.crashStreak).IsAtMost(ProbeCrashEscalation);
        // the schedule's floor is the cold-start calibration seed; every retry after the first
        // audit is floored at the initial refractory by undoProbe
        AssertWithMessage("the audit wait stays within its schedule")
            .That(density.auditClock.waitSamples).IsAtLeast(AuditWaitFirst);
        AssertWithMessage("the audit wait stays within its schedule")
            .That(density.auditClock.waitSamples).IsAtMost(AuditWaitMax);
        // The clock may outrun the ladder only once the ladder is exhausted: `undoProbe` reaches the
        // doubling branch only at the deepest audit rung, and the two places that lower the rung (an
        // audit confirm, a resize) reset the wait in the same breath.
        AssertWithMessage("the audit clock outruns its ladder only at the deepest rung")
            .That((density.auditClock.waitSamples <= ProbeBackoffMax)
                || (density.audit.rung == ProbeBackoffMax)).IsTrue();
        AssertWithMessage("the stillness count is non-negative")
            .That(density.auditClock.stillSamples).IsAtLeast(0);
        if (!double.IsNaN(density.auditClock.settledRate))
        {
            AssertWithMessage("the settled rate is a rate")
                .That(density.auditClock.settledRate).IsAtLeast(0.0);
            AssertWithMessage("the settled rate is a rate")
                .That(density.auditClock.settledRate).IsAtMost(1.0);
        }

        AssertWithMessage("the fresh-park shield lives and dies with the park")
            .That((density.anchor.freshLeft == 0) || density.anchor.held).IsTrue();
        AssertWithMessage("the fresh-park shield stays within its schedule")
            .That(density.anchor.freshLeft).IsAtLeast(0);
        AssertWithMessage("the fresh-park shield stays within its schedule")
            .That(density.anchor.freshLeft).IsAtMost(AuditWaitInitial);
        AssertWithMessage("the veto return budget is non-negative")
            .That(density.anchor.returnLeft).IsAtLeast(0);
        AssertWithMessage("a walk and a veto return are mutually exclusive")
            .That((walk != null) && density.anchor.returning).IsFalse();
        AssertWithMessage("an in-progress return implies the park that follows it")
            .That(!density.anchor.returning || density.anchor.held).IsTrue();
        AssertWithMessage("a walk holds no undo remainder")
            .That((walk == null) || (density.undoRemaining == 0)).IsTrue();
        AssertWithMessage("a park defends a planted anchor")
            .That(!density.anchor.held || density.anchor.IsPlanted()).IsTrue();
        AssertWithMessage("a retest's settle lives and dies with the claim it judges")
            .That((density.anchor.settleLeft > 0) == (density.anchor.retestClaim >= 0)).IsTrue();
        AssertWithMessage("a retest's settle stays within its span")
            .That(density.anchor.settleLeft).IsAtMost(RetestSettle);
        AssertWithMessage("a retest judges a planted anchor")
            .That((density.anchor.retestClaim < 0) || density.anchor.IsPlanted()).IsTrue();
        if (density.anchor.retestClaim >= 0)
        {
            AssertWithMessage("a retest's frozen claim is a rate")
                .That(density.anchor.retestClaim).IsAtMost(1.0);
        }

        AssertWithMessage("the deviation estimate is non-negative and finite")
            .That(density.rates.deviation).IsAtLeast(0.0);
        AssertWithMessage("the deviation estimate is non-negative and finite")
            .That(double.IsFinite(density.rates.deviation)).IsTrue();
        if (!double.IsNaN(density.rates.smoothed))
        {
            AssertWithMessage("the smoothed rate is a rate")
                .That(density.rates.smoothed).IsAtLeast(0.0);
            AssertWithMessage("the smoothed rate is a rate")
                .That(density.rates.smoothed).IsAtMost(1.0);
        }
        if (density.anchor.IsPlanted())
        {
            AssertWithMessage("the anchor's claim is a rate")
                .That(density.anchor.rate).IsAtLeast(0.0);
            AssertWithMessage("the anchor's claim is a rate")
                .That(density.anchor.rate).IsAtMost(1.0);
        }
    }
}
