using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Espresso.Internal;

/// <summary>
/// A probabilistic multiset for estimating the popularity of an element within a time window. The
/// maximum frequency of an element is limited to 15 (4-bits) and an aging process periodically
/// halves the popularity of all elements.
/// <para>
/// The hot methods are generic over their element type so value-type keys are hashed through a
/// constrained call to <see cref="object.GetHashCode"/> with <b>no boxing</b>.
/// </para>
/// </summary>
internal sealed class FrequencySketch
{
    /*
     * A 4-bit CountMinSketch with periodic aging providing the popularity history for the TinyLFU
     * admission policy. The counter matrix is a single long[] holding 16 counters per slot, with an
     * item's counters constrained to a 64-byte block to keep the memory accesses within one L1 cache
     * line. Frequencies are aged periodically (the reset operation) by halving every counter.
     */

    internal const long ResetMask = 0x7777777777777777L;
    internal const long OneMask = 0x1111111111111111L;
    internal const int MinSketchSize = 256;

    internal int sampleSize;
    internal int blockMask;
    internal long[]? table;
    internal int size;

    /// <summary>
    /// Creates a lazily initialized frequency sketch, requiring <see cref="EnsureCapacity"/> be
    /// called when the maximum size of the cache has been determined.
    /// </summary>
    public FrequencySketch()
    {
    }

    /// <summary>
    /// Initializes and increases the capacity of this instance, if necessary, to ensure that it can
    /// accurately estimate the popularity of elements given the maximum size of the cache. This
    /// operation forgets all previous counts when resizing.
    /// </summary>
    public void EnsureCapacity(long maximumSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumSize);
        int maximum = Math.Max((int)Math.Min(maximumSize, int.MaxValue >>> 1), MinSketchSize);
        if (table != null && table.Length >= maximum)
        {
            return;
        }

        sampleSize = (int)Math.Min(10L * maximum, int.MaxValue);
        table = new long[Common.CeilingPowerOfTwo(maximum)];
        blockMask = (table.Length >>> 3) - 1;
        size = 0;
    }

    /// <summary>
    /// Returns if the sketch has not yet been initialized, requiring that <see cref="EnsureCapacity"/>
    /// is called before it begins to track frequencies.
    /// </summary>
    public bool IsNotInitialized => table == null;

    /// <summary>
    /// Returns the estimated number of occurrences of an element, up to the maximum (15).
    /// </summary>
    public int Frequency<T>(T e) where T : notnull
    {
        long[]? t = table;
        if (t == null)
        {
            return 0;
        }

        int frequency = int.MaxValue;
        int blockHash = Spread(e.GetHashCode());
        int counterHash = Rehash(blockHash);
        int block = (blockHash & blockMask) << 3;
        for (int i = 0; i < 4; i++)
        {
            int h = counterHash >>> (i << 3);
            int index = (h >>> 1) & 15;
            int offset = h & 1;
            int slot = block + offset + (i << 1);
            int count = (int)((t[slot] >>> (index << 2)) & 0xfL);
            frequency = Math.Min(frequency, count);
        }
        return frequency;
    }

    /// <summary>
    /// Increments the popularity of the element if it does not exceed the maximum (15). The
    /// popularity of all elements will be periodically down sampled when the observed events exceed a
    /// threshold. This process provides a frequency aging to allow expired long term entries to fade
    /// away.
    /// </summary>
    public void Increment<T>(T e) where T : notnull
    {
        if (table == null)
        {
            return;
        }

        int blockHash = Spread(e.GetHashCode());
        int counterHash = Rehash(blockHash);
        int block = (blockHash & blockMask) << 3;

        // Loop unrolling improves throughput.
        int h0 = counterHash;
        int h1 = counterHash >>> 8;
        int h2 = counterHash >>> 16;
        int h3 = counterHash >>> 24;

        int index0 = (h0 >>> 1) & 15;
        int index1 = (h1 >>> 1) & 15;
        int index2 = (h2 >>> 1) & 15;
        int index3 = (h3 >>> 1) & 15;

        int slot0 = block + (h0 & 1);
        int slot1 = block + (h1 & 1) + 2;
        int slot2 = block + (h2 & 1) + 4;
        int slot3 = block + (h3 & 1) + 6;

        bool added =
              IncrementAt(slot0, index0)
            | IncrementAt(slot1, index1)
            | IncrementAt(slot2, index2)
            | IncrementAt(slot3, index3);

        if (added && ++size == sampleSize)
        {
            Reset();
        }
    }

    /// <summary>Applies a supplemental hash function to defend against a poor quality hash.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Spread(int x)
    {
        x ^= x >>> 17;
        x *= unchecked((int)0xed5ad4bb);
        x ^= x >>> 11;
        x *= unchecked((int)0xac4c1b51);
        x ^= x >>> 15;
        return x;
    }

    /// <summary>Applies another round of hashing for additional randomization.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Rehash(int x)
    {
        x *= unchecked(0x31848bab);
        x ^= x >>> 14;
        return x;
    }

    /// <summary>
    /// Increments the specified counter by 1 if it is not already at the maximum value (15).
    /// </summary>
    internal bool IncrementAt(int i, int j)
    {
        int offset = j << 2;
        long mask = 0xfL << offset;
        if ((table![i] & mask) != mask)
        {
            table[i] += 1L << offset;
            return true;
        }
        return false;
    }

    /// <summary>Reduces every counter by half of its original value.</summary>
    internal void Reset()
    {
        long count = 0;
        long[] t = table!;
        for (int i = 0; i < t.Length; i++)
        {
            count += BitOperations.PopCount((ulong)(t[i] & OneMask));
            t[i] = (t[i] >>> 1) & ResetMask;
        }
        size = (int)((size - (count >>> 2)) >>> 1);
    }
}
