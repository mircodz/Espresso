using System;
using System.Numerics;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class FrequencySketchTest
{
    private readonly int _item = Random.Shared.Next();

    private static FrequencySketch MakeSketch(long maximumSize)
    {
        var sketch = new FrequencySketch();
        sketch.EnsureCapacity(maximumSize);
        return sketch;
    }

    [Fact]
    public void Construct()
    {
        var sketch = new FrequencySketch();
        Assert.Null(sketch.table);
        Assert.True(sketch.IsNotInitialized);

        sketch.Increment(_item);
        Assert.Equal(0, sketch.Frequency(_item));
    }

    [Fact]
    public void EnsureCapacity_Negative()
    {
        var sketch = MakeSketch(512);
        Assert.Throws<ArgumentOutOfRangeException>(() => sketch.EnsureCapacity(-1));
    }

    [Fact]
    public void EnsureCapacity_Smaller()
    {
        var sketch = MakeSketch(512);
        int size = sketch.table!.Length;
        sketch.EnsureCapacity(size / 2);
        Assert.Equal(size, sketch.table!.Length);
        Assert.Equal(10 * size / 2, sketch.sampleSize);
        Assert.Equal((size >> 3) - 1, sketch.blockMask);
    }

    [Fact]
    public void EnsureCapacity_Shrink_ResetReachable()
    {
        var sketch = MakeSketch(1_024);
        sketch.size = 5_000;

        // The table is retained but the aging period tracks the new maximum, with the observed
        // count clamped so that the equality reset test remains reachable
        sketch.EnsureCapacity(256);
        Assert.Equal(1_024, sketch.table!.Length);
        Assert.Equal(2_560, sketch.sampleSize);
        Assert.Equal(2_559, sketch.size);

        // the next observed addition reaches the sample threshold and triggers the aging cycle
        sketch.Increment(_item);
        Assert.Equal(1_279, sketch.size);
    }

    [Fact]
    public void EnsureCapacity_Shrink_DenseTable_AgesOnSchedule()
    {
        var sketch = MakeSketch(1_024);
        Array.Fill(sketch.table!, FrequencySketch.OneMask);
        sketch.size = 5_000;

        sketch.EnsureCapacity(256);
        Assert.Equal(2_559, sketch.size);

        // The retained table's counter mass is denominated in the old maximum and outweighs the
        // observations the reset corrects against it. An underflow here stalls aging for as many
        // additions as it went negative by, restoring the old size's cadence that the retrack removes.
        sketch.Increment(_item);
        Assert.Equal(0, sketch.size);
        Assert.Equal(1, sketch.Frequency(_item));
    }

    [Fact]
    public void EnsureCapacity_RepeatedRetrack_ResetReachable()
    {
        var sketch = MakeSketch(1_024);
        for (int i = 0; i < 15; i++)
        {
            sketch.Increment(_item);
        }
        Assert.Equal(15, sketch.Frequency(_item));

        // A weighted cache retracks on every addition as its entry count wobbles, so a repeated
        // clamp must not discard the aging progress and starve the periodic reset
        for (int i = 0; i < 15_000; i++)
        {
            sketch.Increment(i);
            sketch.EnsureCapacity(1_024 - (i & 1));
        }
        Assert.True(sketch.Frequency(_item) < 15);
    }

    [Fact]
    public void EnsureCapacity_Retrack_KeepsAgingProgress()
    {
        var sketch = MakeSketch(1_024);
        for (int i = 0; i < 15; i++)
        {
            sketch.Increment(_item);
        }
        int size = sketch.size;

        // a retrack may only clamp the observed count to keep the reset reachable, never advance it
        // towards the sample threshold, else a weighted cache's per-addition retracks age the sketch
        // on nearly every increment and admission degrades towards random
        sketch.EnsureCapacity(512);
        Assert.Equal(5_120, sketch.sampleSize);
        Assert.Equal(size, sketch.size);
        Assert.Equal(15, sketch.Frequency(_item));
    }

    [Fact]
    public void EnsureCapacity_Larger()
    {
        var sketch = MakeSketch(512);
        int size = sketch.table!.Length;
        sketch.EnsureCapacity(2L * size);
        Assert.Equal(2 * size, sketch.table!.Length);
        Assert.Equal(10 * 2 * size, sketch.sampleSize);
        Assert.Equal(((2 * size) >> 3) - 1, sketch.blockMask);
    }

    [Fact]
    public void EnsureCapacity_Maximum()
    {
        var sketch = MakeSketch(512);
        int size = int.MaxValue / 10 + 1;
        sketch.EnsureCapacity(size);
        Assert.Equal(int.MaxValue, sketch.sampleSize);
        Assert.Equal(Common.CeilingPowerOfTwo(size), sketch.table!.Length);
        Assert.Equal((sketch.table!.Length >> 3) - 1, sketch.blockMask);
    }

    [Fact]
    public void EnsureCapacity_ExactMatch()
    {
        var sketch = MakeSketch(512);
        int size = sketch.table!.Length;
        long[] table = sketch.table!;
        sketch.EnsureCapacity(size);
        Assert.Same(table, sketch.table);
    }

    [Fact]
    public void Spread_KnownValues()
    {
        Assert.Equal(0, FrequencySketch.Spread(0));
        Assert.NotEqual(1, FrequencySketch.Spread(1));
        Assert.NotEqual(FrequencySketch.Spread(int.MaxValue), FrequencySketch.Spread(int.MaxValue - 1));
    }

    [Fact]
    public void IncrementAt_Saturated_ReturnsFalse()
    {
        var sketch = MakeSketch(512);
        for (int i = 0; i < 15; i++)
        {
            Assert.True(sketch.IncrementAt(0, 0));
        }
        Assert.False(sketch.IncrementAt(0, 0));
    }

    [Fact]
    public void Increment_Once()
    {
        var sketch = MakeSketch(512);
        sketch.Increment(_item);
        Assert.Equal(1, sketch.Frequency(_item));
    }

    [Fact]
    public void Increment_Max()
    {
        var sketch = MakeSketch(512);
        for (int i = 0; i < 20; i++)
        {
            sketch.Increment(_item);
        }
        Assert.Equal(15, sketch.Frequency(_item));
    }

    [Fact]
    public void Increment_Distinct()
    {
        var sketch = MakeSketch(512);
        sketch.Increment(_item);
        sketch.Increment(_item + 1);
        Assert.Equal(1, sketch.Frequency(_item));
        Assert.Equal(1, sketch.Frequency(_item + 1));
        Assert.Equal(0, sketch.Frequency(_item + 2));
    }

    [Fact]
    public void Increment_Zero()
    {
        var sketch = MakeSketch(512);
        sketch.Increment(0);
        Assert.Equal(1, sketch.Frequency(0));
    }

    [Fact]
    public void Reset()
    {
        bool reset = false;
        var sketch = new FrequencySketch();
        sketch.EnsureCapacity(64);

        for (int i = 1; i < 20 * sketch.table!.Length; i++)
        {
            sketch.Increment(i);
            if (sketch.size != i)
            {
                reset = true;
                break;
            }
        }
        Assert.True(reset);
        Assert.True(sketch.size <= sketch.sampleSize / 2);
    }

    [Fact]
    public void Full()
    {
        var sketch = MakeSketch(512);
        sketch.sampleSize = int.MaxValue;
        for (int i = 0; i < 100_000; i++)
        {
            sketch.Increment(i);
        }
        foreach (long slot in sketch.table!)
        {
            Assert.Equal(64, BitOperations.PopCount((ulong)slot));
        }

        sketch.Reset();
        foreach (long slot in sketch.table!)
        {
            Assert.Equal(FrequencySketch.ResetMask, slot);
        }
    }
}