using System.Diagnostics;

namespace Espresso;

/// <summary>
/// A source of nanosecond-precision time readings for a cache (expiration and load timing). The
/// value is only meaningful relative to another reading, not as wall-clock time.
/// </summary>
public interface ITicker
{
    /// <summary>Returns the current value of the ticker in nanoseconds.</summary>
    long Read();
}

/// <summary>The default <see cref="ITicker"/>, backed by a high-resolution monotonic clock.</summary>
public sealed class SystemTicker : ITicker
{
    /// <summary>The shared singleton instance.</summary>
    public static readonly SystemTicker Instance = new();

    private static readonly double NanosPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    private SystemTicker() { }

    /// <inheritdoc/>
    public long Read() => (long)(Stopwatch.GetTimestamp() * NanosPerTick);
}

/// <summary>A ticker that always returns zero; disables time-based behavior.</summary>
public sealed class DisabledTicker : ITicker
{
    /// <summary>The shared singleton instance.</summary>
    public static readonly DisabledTicker Instance = new();

    private DisabledTicker() { }

    /// <inheritdoc/>
    public long Read() => 0L;
}

/// <summary>Adapts a function to <see cref="ITicker"/>.</summary>
internal sealed class FuncTicker(System.Func<long> read) : ITicker
{
    public long Read() => read();
}
