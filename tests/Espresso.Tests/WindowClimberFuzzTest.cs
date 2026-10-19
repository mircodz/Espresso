using System;
using Espresso.Internal;
using Xunit;
using static Espresso.Internal.WindowClimber;
using static Espresso.Internal.WindowClimber.AuditClock;
using static Espresso.Tests.ClimberInvariants;
using static Espresso.Tests.FluentAssert;

namespace Espresso.Tests;

/// <summary>
/// Exercises the <see cref="WindowClimber"/> state machine with random sample sequences, window
/// positions, region geometries, and resizes. The caller-side realism is deliberate: the cache
/// applies a commanded adjustment through capped transfers, so each adjustment is applied partially
/// (or not at all, a rail) and the window teleports within its legal range, exactly the drift the
/// climber must tolerate. The oracle checks <see cref="ClimberInvariants"/> after every decision,
/// plus the deferral-ownership rules, which compare a sample against the one before it.
/// <para>
/// The driver replays over a fixed set of seeded pseudo-random inputs whose data provider is biased
/// toward range edges and small magnitudes, as a coverage-guided fuzzer's corpus tends to be.
/// </para>
/// </summary>
public sealed class WindowClimberFuzzTest
{
    private const int Seeds = 5_000;

    [Fact]
    public void Climb_RandomizedInputs_HoldInvariants()
    {
        for (int seed = 0; seed < Seeds; seed++)
        {
            Climb(new DataProvider(seed));
        }
    }

    private static void Climb(DataProvider data)
    {
        long maximum = PickMaximum(data);
        var climber = new WindowClimber();
        climber.Resized(maximum);
        AssertInvariants(climber, maximum);

        long windowMax = maximum / 100;
        long carryOver = climber.Adjustment();
        int operations = data.ConsumeInt(0, 200);
        for (int i = 0; i < operations; i++)
        {
            if (data.ConsumeInt(0, 7) == 0)
            {
                // an occasional user resize, sometimes crossing tiers, resets the machine wholesale
                maximum = PickMaximum(data);
                climber.Resized(maximum);
                windowMax = Math.Min(windowMax, maximum);
                carryOver = climber.Adjustment();
                AssertInvariants(climber, maximum);
                continue;
            }

            // teleport within the legal range occasionally: transfers lag commands, and expiration or
            // weight changes move the realized position independently of the climber's requests
            if (data.ConsumeInt(0, 3) == 0)
            {
                windowMax = data.ConsumeLong(0, maximum);
            }

            // discard the partial sample occasionally, as the uninitialized-sketch path does
            if (data.ConsumeInt(0, 15) == 0)
            {
                climber.ResetSample();
            }

            int requests = data.ConsumeInt(0, 5_000_000);
            int hits = data.ConsumeInt(0, requests);
            int windowHits = data.ConsumeInt(0, hits);
            int probationHits = data.ConsumeInt(0, hits - windowHits);
            Record(climber, hits, windowHits, probationHits, requests - hits);
            long pending = climber.sample.hits + climber.sample.misses;

            var density = climber.tier as DensityClimber;
            var probing = density?.walk;
            long anchorWindow = (density == null) ? -1 : density.anchor.window;
            int starvationRung = (density == null) ? 0 : density.starvation.rung;
            int auditRung = (density == null) ? 0 : density.audit.rung;
            int auditWait = (density == null) ? 0 : density.auditClock.waitSamples;

            long mainProtected = data.ConsumeLong(0, Math.Max(0, maximum - windowMax));
            int sketchSample = data.ConsumeInt(4, 1_000_000);
            climber.DetermineAdjustment(maximum, windowMax, mainProtected, sketchSample);
            AssertInvariants(climber, maximum);
            if (density != null)
            {
                CheckDeferralOwnership(density, probing, starvationRung, auditRung, auditWait);
                CheckRetestOwnership(density, anchorWindow);
            }

            // a sample completes when the accumulated counts crossed the period and were consumed,
            // detected from the counters, since a zero-fed call can complete an accumulated sample
            bool completed = (pending > 0)
                && (climber.sample.hits == 0) && (climber.sample.misses == 0);
            if (!completed)
            {
                // a sub-period sample must preserve the pending adjustment (the multi-cycle carry-over
                // that keeps a capped transfer draining until a fresh sample overwrites it)
                AssertWithMessage("A sub-period sample must not disturb the carry-over")
                    .That(climber.Adjustment()).IsEqualTo(carryOver);
            }
            carryOver = climber.Adjustment();

            // apply the command partially, as the transfer throttle would, and stay in legal range.
            // The percentage is split and the move is measured against the room it has, since at the
            // top tier the product and the sum both exceed a long and would snap the window to a rail
            long adjustment = climber.Adjustment();
            int percent = data.ConsumeInt(0, 100);
            long applied = ((adjustment / 100) * percent) + (((adjustment % 100) * percent) / 100);
            windowMax = (applied >= 0)
                ? (windowMax + Math.Min(applied, maximum - windowMax))
                : (windowMax + Math.Max(applied, -windowMax));
        }
    }

    /// <summary>
    /// A retest judges the claim frozen for the position its return set out for, so an anchor that
    /// moves abandons one. A retest still pending after a move must therefore be the move's own.
    /// </summary>
    private static void CheckRetestOwnership(DensityClimber density, long anchorWindow)
    {
        bool moved = (density.anchor.window != anchorWindow);
        AssertWithMessage("a retest that survives the anchor moving was armed by the move")
            .That(!moved || (density.anchor.retestClaim < 0)
                || (density.anchor.retestClaim == density.anchor.rate)).IsTrue();
    }

    /// <summary>
    /// A deferral is denominated in samples and indexed by the deferring layer's own completed
    /// experiments. A layer's ladder and schedule may therefore grow only on a sample that ended
    /// that layer's own walk, and the audit clock may pass its ladder only on a walk that reached a
    /// verdict rather than crashing out.
    /// </summary>
    private static void CheckDeferralOwnership(DensityClimber density,
        Walk? probing, int starvationRung, int auditRung, int auditWait)
    {
        bool endedAudit = (probing != null) && (density.walk != probing) && probing.isAudit;
        bool endedStarvation = (probing != null) && (density.walk != probing) && !probing.isAudit;

        AssertWithMessage("the starvation rung deepens only on a starvation ending")
            .That((density.starvation.rung <= starvationRung) || endedStarvation).IsTrue();
        AssertWithMessage("the audit rung deepens only on an audit ending")
            .That((density.audit.rung <= auditRung) || endedAudit).IsTrue();
        AssertWithMessage("the audit clock defers only on an audit ending")
            .That((density.auditClock.waitSamples <= auditWait) || endedAudit).IsTrue();
        AssertWithMessage("the audit clock passes its ladder only on a verdict, never a crash")
            .That((density.auditClock.waitSamples <= auditWait)
                || (density.auditClock.waitSamples
                    <= Math.Max(AuditWaitInitial, density.audit.rung))
                || (density.audit.crashStreak == 0)).IsTrue();
    }

    /// <summary>Returns a maximum size, weighted toward the tier boundaries and including the extremes.</summary>
    private static long PickMaximum(DataProvider data)
    {
        switch (data.ConsumeInt(0, 4))
        {
            case 0:
                return data.ConsumeLong(0, 512);                    // the slow-adapt tier
            case 1:
                return data.ConsumeLong(513, 4096);                 // the stock reactive tier
            case 2:
                return data.ConsumeLong(4097, 65_536);              // the density tier
            case 3:
                return data.ConsumeLong(65_537, 1L << 40);          // large weighted maxima
            default:
                return long.MaxValue - data.ConsumeLong(0, 1024);   // the overflow guard's territory
        }
    }

    /// <summary>Feeds a sample's counts, through the recording api when small enough to loop cheaply.</summary>
    private static void Record(WindowClimber climber,
        int hits, int windowHits, int probationHits, int misses)
    {
        if ((hits + misses) <= 4096)
        {
            for (int i = 0; i < windowHits; i++)
            {
                climber.RecordHit(inWindow: true, inProbation: false);
            }
            for (int i = 0; i < probationHits; i++)
            {
                climber.RecordHit(inWindow: false, inProbation: true);
            }
            for (int i = 0; i < (hits - windowHits - probationHits); i++)
            {
                climber.RecordHit(inWindow: false, inProbation: false);
            }
            for (int i = 0; i < misses; i++)
            {
                climber.RecordMiss();
            }
        }
        else
        {
            climber.sample.hits += hits;
            climber.sample.windowHits += windowHits;
            climber.sample.probationHits += probationHits;
            climber.sample.misses += misses;
        }
    }

    /// <summary>
    /// A seeded source of fuzz inputs: inclusive ranges, biased toward the
    /// range edges and toward small magnitudes so that short samples and rails are exercised often.
    /// </summary>
    private sealed class DataProvider
    {
        private readonly Random _random;

        public DataProvider(int seed) => _random = new Random(seed);

        public int ConsumeInt(int min, int max) => (int)ConsumeLong(min, max);

        public long ConsumeLong(long min, long max)
        {
            if (min >= max)
            {
                return min;
            }
            switch (_random.Next(8))
            {
                case 0:
                    return min;
                case 1:
                    return max;
                case 2:
                case 3:
                    // log-uniform offset from the minimum, so small values are well represented
                    double span = (double)max - min;
                    long offset = Common.ToLongSaturating(Math.Pow(span, _random.NextDouble()));
                    return min + Math.Clamp(offset, 0, max - min);
                default:
                    long uniform = Common.ToLongSaturating(_random.NextDouble() * ((double)max - min));
                    return min + Math.Clamp(uniform, 0, max - min);
            }
        }
    }
}
