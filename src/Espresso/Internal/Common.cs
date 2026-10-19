using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Espresso.Internal;

/// <summary>
/// Internal numeric/argument helpers used across the cache internals.
/// </summary>
internal static class Common
{
    internal static long SaturatedToNanos(TimeSpan duration)
    {
        long ticks = duration.Ticks;
        return ticks > long.MaxValue / 100L ? long.MaxValue
            : ticks < long.MinValue / 100L ? long.MinValue
            : ticks * 100L;
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> when the state expression is false.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RequireState(bool expression)
    {
        if (!expression)
        {
            throw new InvalidOperationException();
        }
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> with a message when the state is false.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RequireState(bool expression, string message)
    {
        if (!expression)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Converts a double to a long by truncating toward zero, saturating at
    /// <see cref="long.MinValue"/>/<see cref="long.MaxValue"/>, and mapping NaN to zero. A plain cast
    /// leaves out-of-range and NaN conversions platform-dependent on .NET 8.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToLongSaturating(double value)
    {
        if (double.IsNaN(value))
        {
            return 0L;
        }
        if (value >= 9.2233720368547758E18) // 2^63
        {
            return long.MaxValue;
        }
        if (value <= -9.2233720368547758E18) // -2^63
        {
            return long.MinValue;
        }
        return (long)value;
    }

    /// <summary>
    /// Returns the smallest power of two greater than or equal to <paramref name="x"/>,
    /// else the maximum. (Hacker's Delight, ch.3.)
    /// </summary>
    public static int CeilingPowerOfTwo(int x)
    {
        if (x > 1 << 30)
        {
            return 1 << 30;
        }
        // A shift count is masked to 5 bits, so -nlz gives 32 - nlz.
        return 1 << (-BitOperations.LeadingZeroCount((uint)(x - 1)) & 31);
    }

    /// <summary>
    /// Returns the smallest power of two greater than or equal to <paramref name="x"/>,
    /// else the maximum. (Hacker's Delight, ch.3.)
    /// </summary>
    public static long CeilingPowerOfTwo(long x)
    {
        if (x > 1L << 62)
        {
            return 1L << 62;
        }
        return 1L << (-BitOperations.LeadingZeroCount((ulong)(x - 1)) & 63);
    }
}
