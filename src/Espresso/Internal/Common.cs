using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Espresso.Internal;

/// <summary>
/// Internal numeric/argument helpers used across the cache internals.
/// </summary>
internal static class Common
{
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
