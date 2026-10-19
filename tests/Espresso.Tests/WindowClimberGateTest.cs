using System.Collections.Generic;
using Xunit;
using static Espresso.Tests.FluentAssert;

namespace Espresso.Tests;

/// <summary>
/// Behavioral pins for the window climber on adversarial workload shapes. Each cell drives a real
/// cache single-threaded with a seeded synthetic stream and asserts a hit-rate bar sized to separate
/// the healthy machine from a named failure mode, with several points of margin for the admission
/// tiebreak's noise.
/// <para>
/// The stream is drawn from a seeded 48-bit linear congruential generator, so each cell replays an
/// exact request sequence; the hit rates still vary slightly between runs because the admission
/// tiebreak is randomized.
/// </para>
/// </summary>
public sealed class WindowClimberGateTest
{
    private const int Maximum = 8_192;
    private static readonly object Present = new();

    /// <summary>
    /// The whisper trap: a hot core lives in main, a reuse band at distance 3,000 is window-only,
    /// and a whisper of distance-100 pairs keeps the window earning just above the starvation bar so
    /// no blind-corner probe arms. Pure density clamps the window at the floor forever (~11 points
    /// below achievable); the equilibrium audit is what escapes. Guards audit-clock liveness and the
    /// sighted-false-equilibrium escape.
    /// </summary>
    [Fact]
    public void Whisper()
    {
        double hitRate = Run(new Workload(seed: 20_260_726, requests: 4_000_000,
            hotKeys: 3_000, hotRate: 0.631, bandRate: 0.1434,
            bandDistance: 3_000, whisperRate: 0.0038, whisperDistance: 100));
        // healthy ~66.8 (spread under 0.1); the floor pin scores ~55
        AssertWithMessage("whisper trap escape").That(hitRate).IsGreaterThan(62.0);
    }

    /// <summary>
    /// The whisper base at a constant, louder whisper dose: the position-jam family's dose-matched
    /// control. The healthy machine is deterministic here while a steering or stillness-clock
    /// regression lands well below. Guards density steering health and the stillness clock under
    /// ordinary (unjammed) traffic.
    /// </summary>
    [Fact]
    public void PositionJamControl()
    {
        double hitRate = Run(new Workload(seed: 20_260_801, requests: 4_000_000,
            hotKeys: 3_000, hotRate: 0.631, bandRate: 0.1434,
            bandDistance: 3_000, whisperRate: 0.015_75, whisperDistance: 100));
        // healthy ~66.9 (spread under 0.1); a jammed or missteered machine scores ~56
        AssertWithMessage("position-jam control").That(hitRate).IsGreaterThan(62.0);
    }

    /// <summary>
    /// Demoflood: the hot core saturates the protected region, so an up-walk's demotions enrich live
    /// probation and a probe verdict judged against the live rate is an absorbing false veto. The
    /// frozen-at-arm baseline escapes. Guards the probe machine's adjudication.
    /// </summary>
    [Fact]
    public void Demoflood()
    {
        double hitRate = Run(new Workload(seed: 7, requests: 2_621_440,
            hotKeys: 6_160, hotRate: 0.70, bandRate: 0.20,
            bandDistance: 2_867, whisperRate: 0.0, whisperDistance: 0));
        // healthy ~71.3 (spread under 0.2); the live-baseline false veto scores ~62-64
        AssertWithMessage("demoflood adjudication").That(hitRate).IsGreaterThan(67.0);
    }

    /// <summary>
    /// The moat: a hot set large enough to be capacity-limited digs a hit-rate valley between the
    /// resting window and a window-only reuse band beyond it, and a whisper keeps the corner sighted
    /// so only the audit layer may act. Every walk crosses terrain that dips below the rate it armed
    /// on, so the crash abort decides whether the far bank is reachable at all. Guards the audit's
    /// escalated crash persistence and the retention of the position it finds.
    /// </summary>
    [Fact]
    public void Moat()
    {
        double hitRate = Run(new Workload(seed: 20_260_801, requests: 4_000_000,
            hotKeys: 4_000, hotRate: 0.55, bandRate: 0.20,
            bandDistance: 4_000, whisperRate: 0.0038, whisperDistance: 100));
        // healthy ~58.2 (spread under 0.2); with the audit layer off the valley is absorbing and the
        // machine rests on the near bank at 46.2 (deterministic)
        AssertWithMessage("moat valley crossing").That(hitRate).IsGreaterThan(53.0);
    }

    /// <summary>Drives the workload through a real cache, single-threaded, and returns the hit rate.</summary>
    private static double Run(Workload workload)
    {
        var cache = Cache.NewBuilder<long, object>()
            .Executor(DirectExecutor.Instance)
            .InitialCapacity(Maximum)
            .MaximumSize(Maximum)
            .Build();
        long hits = 0;
        for (long i = 0; i < workload.Requests; i++)
        {
            long key = workload.Next(i);
            if (cache.GetIfPresent(key) == null)
            {
                cache.Put(key, Present);
            }
            else
            {
                hits++;
            }
        }
        return (100.0 * hits) / workload.Requests;
    }

    /// <summary>
    /// A seeded stream mixing a uniformly-hot core, a reuse band whose pairs re-arrive at a fixed
    /// distance, an optional whisper of short-distance pairs, and one-shot noise; scheduled
    /// re-touches take priority at their position, colliding entries probing forward.
    /// </summary>
    private sealed class Workload
    {
        private readonly Lcg48Random _random;
        public readonly long Requests;
        private readonly int _hotKeys;
        private readonly double _hotRate;
        private readonly double _bandRate;
        private readonly int _bandDistance;
        private readonly double _whisperRate;
        private readonly int _whisperDistance;
        private readonly Dictionary<long, long> _pending = new();
        private long _bandId;
        private long _whisperId;
        private long _noiseId;

        public Workload(int seed, long requests, int hotKeys, double hotRate, double bandRate,
            int bandDistance, double whisperRate, int whisperDistance)
        {
            _random = new Lcg48Random(seed);
            Requests = requests;
            _hotKeys = hotKeys;
            _hotRate = hotRate;
            _bandRate = bandRate;
            _bandDistance = bandDistance;
            _whisperRate = whisperRate;
            _whisperDistance = whisperDistance;
        }

        public long Next(long position)
        {
            if (_pending.Remove(position, out long due))
            {
                return due;
            }
            double draw = _random.NextDouble();
            if (draw < _hotRate)
            {
                return 1_000_000L + _random.NextInt(_hotKeys);
            }
            else if (draw < (_hotRate + _bandRate))
            {
                long key = 5_000_000 + _bandId;
                _bandId++;
                Schedule(position + _bandDistance, key);
                return key;
            }
            else if (draw < (_hotRate + _bandRate + _whisperRate))
            {
                long key = 3_000_000 + _whisperId;
                _whisperId++;
                Schedule(position + _whisperDistance, key);
                return key;
            }
            long noise = 9_000_000 + _noiseId;
            _noiseId++;
            return noise;
        }

        private void Schedule(long position, long key)
        {
            long slot = position;
            while (_pending.ContainsKey(slot))
            {
                slot++;
            }
            _pending[slot] = key;
        }
    }

    /// <summary>A 48-bit linear congruential generator, so that a seed yields a fixed stream.</summary>
    private sealed class Lcg48Random
    {
        private const long Multiplier = 0x5DEECE66DL;
        private const long Addend = 0xBL;
        private const long Mask = (1L << 48) - 1;
        private const double DoubleUnit = 1.0 / (1L << 53);

        private long _seed;

        public Lcg48Random(long seed) => _seed = (seed ^ Multiplier) & Mask;

        private int Next(int bits)
        {
            _seed = ((_seed * Multiplier) + Addend) & Mask;
            return (int)((ulong)_seed >> (48 - bits));
        }

        public double NextDouble() => (((long)Next(26) << 27) + Next(27)) * DoubleUnit;

        public int NextInt(int bound)
        {
            int r = Next(31);
            int m = bound - 1;
            if ((bound & m) == 0)
            {
                return (int)((bound * (long)r) >> 31);
            }
            for (int u = r; u - (r = u % bound) + m < 0; u = Next(31))
            {
            }
            return r;
        }
    }
}
