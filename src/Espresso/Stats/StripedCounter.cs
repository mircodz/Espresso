using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Espresso.Stats;

/// <summary>
/// A counter spread over padded cells, one per thread slot, so concurrent increments from different
/// threads do not contend on one cache line. A thread takes a slot on its first increment and keeps it
/// for its lifetime; threads beyond the cell count share cells, which stays correct. A read sums the
/// cells, so a sum taken during concurrent increments reflects each cell at a slightly different moment.
/// </summary>
internal sealed class StripedCounter
{
    // 128 bytes per cell keeps neighbouring values on separate cache lines, including 128-byte lines.
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct Cell
    {
        [FieldOffset(64)] public long Value;
    }

    [ThreadStatic] private static int t_slot;
    private static int s_nextSlot;

    private readonly Cell[] _cells;
    private readonly int _mask;

    public StripedCounter()
    {
        int count = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(Environment.ProcessorCount, 1, 64));
        _cells = new Cell[count];
        _mask = count - 1;
    }

    // Kept out of line: inlining the thread-slot lookup into every caller bloats the hot read paths.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Add(long value)
    {
        int slot = t_slot;
        if (slot == 0)
        {
            slot = AssignSlot();
        }
        Interlocked.Add(ref _cells[slot & _mask].Value, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AssignSlot()
    {
        int slot;
        do
        {
            slot = Interlocked.Increment(ref s_nextSlot);
        }
        while (slot == 0);
        t_slot = slot;
        return slot;
    }

    public long Sum()
    {
        long sum = 0;
        for (int i = 0; i < _cells.Length; i++)
        {
            sum += Volatile.Read(ref _cells[i].Value);
        }
        return sum;
    }
}
