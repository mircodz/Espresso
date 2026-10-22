using System;
using System.Runtime.CompilerServices;

namespace Espresso.Internal;

/// <summary>
/// A per-thread hash that spreads threads across striped structures: the read buffer's stripes and
/// the hash map's size-counter cells. It is seeded lazily from the managed thread id and advanced by
/// an xorshift when a thread collides with another, so colliding threads migrate to other stripes.
/// <para>
/// The thread-static is declared on this non-generic class on purpose. A thread-static declared on a
/// generic type that is shared across reference-type instantiations is resolved through a runtime
/// helper on every access, which costs several nanoseconds on the read path; on a non-generic type the
/// JIT inlines it as a plain thread-local load.
/// </para>
/// </summary>
internal static class ThreadProbe
{
    [ThreadStatic] private static int t_probe;

    /// <summary>Returns this thread's probe, initializing it on first use. Never zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Get()
    {
        int probe = t_probe;
        return probe != 0 ? probe : Initialize();
    }

    /// <summary>Advances the probe after a collision and stores it for this thread.</summary>
    public static int Advance(int probe)
    {
        probe ^= probe << 13;
        probe ^= probe >>> 17;
        probe ^= probe << 5;
        t_probe = probe;
        return probe;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Initialize()
    {
        int probe = (int)Mix64(Environment.CurrentManagedThreadId);
        if (probe == 0)
        {
            probe = 1; // xorshift maps a non-zero value to a non-zero value, so the probe stays valid
        }
        t_probe = probe;
        return probe;
    }

    /// <summary>Computes Stafford variant 13 of the 64-bit mix function.</summary>
    private static long Mix64(long z)
    {
        z = (z ^ (z >>> 30)) * unchecked((long)0xbf58476d1ce4e5b9L);
        z = (z ^ (z >>> 27)) * unchecked((long)0x94d049bb133111ebL);
        return z ^ (z >>> 31);
    }
}
