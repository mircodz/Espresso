using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Espresso;

/// <summary>
/// A source of nanosecond-precision time readings for a cache (expiration and load timing). The value
/// is only meaningful relative to another reading, not as wall-clock time.
/// <para>
/// This is a single sealed type (rather than an interface with multiple implementations) so the field
/// that holds it is a concrete type: the JIT can then devirtualize and inline <see cref="Read"/> on the
/// read hot path instead of paying an interface dispatch per operation.
/// </para>
/// <para>
/// <see cref="System"/> — the default — reads the OS tick counter (<see cref="Environment.TickCount64"/>),
/// which is several times cheaper than the high-resolution clock but coarse (~15&#160;ms resolution). Since a
/// cache reads the clock on every access, that cost matters and expiration is a coarse policy anyway. Use
/// <see cref="HighResolution"/> when sub-15&#160;ms expiration accuracy is required.
/// </para>
/// </summary>
public sealed class Ticker
{
    private static readonly double NanosPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    private enum Source : byte { Coarse, HighRes, Zero, Func }

    private readonly Source _source;
    private readonly Func<long>? _read; // only used when _source == Func

    private Ticker(Source source, Func<long>? read = null)
    {
        _source = source;
        _read = read;
    }

    /// <summary>
    /// The default ticker: the OS tick counter converted to nanoseconds. Cheap to read (a memory-mapped
    /// counter, no syscall) at the cost of ~15&#160;ms resolution.
    /// </summary>
    public static readonly Ticker System = new(Source.Coarse);

    /// <summary>A high-resolution monotonic ticker (<see cref="Stopwatch"/>); precise but costlier to read.</summary>
    public static readonly Ticker HighResolution = new(Source.HighRes);

    /// <summary>A ticker that always returns zero; disables time-based behavior.</summary>
    public static readonly Ticker Disabled = new(Source.Zero);

    /// <summary>Creates a ticker that reads the current value from <paramref name="read"/>.</summary>
    public static Ticker FromFunc(Func<long> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new Ticker(Source.Func, read);
    }

    /// <summary>Returns the current value of the ticker in nanoseconds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Read() => _source switch
    {
        Source.Coarse => Environment.TickCount64 * 1_000_000L,          // ms -> ns
        Source.HighRes => (long)(Stopwatch.GetTimestamp() * NanosPerTick),
        Source.Zero => 0L,
        _ => _read!(),
    };
}
