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
        Assert.Equal(10 * size, sketch.sampleSize);
        Assert.Equal((size >> 3) - 1, sketch.blockMask);
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