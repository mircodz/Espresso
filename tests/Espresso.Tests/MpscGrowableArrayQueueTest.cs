using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class MpscGrowableArrayQueueTest
{
    private const int NumProducers = 10;
    private const int Produce = 100;
    private const int PopulatedSize = 10;
    private const int FullSize = 32;

    private static MpscGrowableArrayQueue<string> MakePopulated(int items)
    {
        var queue = new MpscGrowableArrayQueue<string>(4, FullSize);
        for (int i = 0; i < items; i++)
        {
            Assert.True(queue.Offer("e" + i));
        }
        return queue;
    }

    // --- Constructor ---

    [Fact]
    public void Constructor_InitialCapacityTooSmall()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MpscGrowableArrayQueue<string>(1, 4));

    [Fact]
    public void Constructor_MaxCapacityTooSmall()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MpscGrowableArrayQueue<string>(4, 1));

    [Fact]
    public void Constructor_Inverted()
        => Assert.Throws<ArgumentException>(() => new MpscGrowableArrayQueue<string>(8, 4));

    [Fact]
    public void Constructor_Capacity()
    {
        var queue = new MpscGrowableArrayQueue<string>(4, 8);
        Assert.Equal(8, queue.Capacity);
    }

    // --- Size ---

    [Fact]
    public void Size_WhenEmpty() => Assert.Equal(0, MakePopulated(0).Count);

    [Fact]
    public void Size_WhenPopulated() => Assert.Equal(PopulatedSize, MakePopulated(PopulatedSize).Count);

    // --- Offer ---

    [Fact]
    public void Offer_WhenEmpty()
    {
        var queue = MakePopulated(0);
        Assert.True(queue.Offer("x"));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Offer_WhenPopulated()
    {
        var queue = MakePopulated(PopulatedSize);
        Assert.True(queue.Offer("x"));
        Assert.Equal(PopulatedSize + 1, queue.Count);
    }

    [Fact]
    public void Offer_WhenFull()
    {
        var queue = MakePopulated(FullSize);
        Assert.False(queue.Offer("x"));
        Assert.Equal(FullSize, queue.Count);
    }

    [Fact]
    public void Offer_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => MakePopulated(0).Offer(null!));

    // --- Poll ---

    [Fact]
    public void Poll_WhenEmpty() => Assert.Null(MakePopulated(0).Poll());

    [Fact]
    public void Poll_WhenPopulated()
    {
        var queue = MakePopulated(PopulatedSize);
        Assert.NotNull(queue.Poll());
        Assert.Equal(PopulatedSize - 1, queue.Count);
    }

    [Fact]
    public void Poll_ToEmpty()
    {
        var queue = MakePopulated(FullSize);
        while (queue.Poll() != null) { }
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Poll_PreservesFifoOrder()
    {
        var queue = MakePopulated(0);
        for (int i = 0; i < FullSize; i++)
        {
            Assert.True(queue.Offer("e" + i));
        }
        for (int i = 0; i < FullSize; i++)
        {
            Assert.Equal("e" + i, queue.Poll());
        }
        Assert.Null(queue.Poll());
    }

    // --- Peek ---

    [Fact]
    public void Peek_WhenEmpty() => Assert.Null(MakePopulated(0).Peek());

    [Fact]
    public void Peek_WhenPopulated()
    {
        var queue = MakePopulated(PopulatedSize);
        Assert.NotNull(queue.Peek());
        Assert.Equal(PopulatedSize, queue.Count);
    }

    [Fact]
    public void Peek_ToEmpty()
    {
        var queue = MakePopulated(FullSize);
        for (int i = 0; i < FullSize; i++)
        {
            Assert.NotNull(queue.Peek());
            Assert.NotNull(queue.Poll());
        }
        Assert.Null(queue.Peek());
    }

    // --- Growth (crossing chunk boundaries) ---

    [Fact]
    public void GrowsAcrossChunks_ThenDrainsInOrder()
    {
        // initialCapacity 4 -> chunks of 4; maxCapacity 32 forces several linked buffers.
        var queue = new MpscGrowableArrayQueue<string>(4, FullSize);
        var offered = new List<string>();
        for (int i = 0; i < FullSize; i++)
        {
            string v = "v" + i;
            Assert.True(queue.Offer(v));
            offered.Add(v);
        }
        Assert.False(queue.Offer("overflow"));

        var drained = new List<string>();
        string? e;
        while ((e = queue.Poll()) != null)
        {
            drained.Add(e);
        }
        Assert.Equal(offered, drained);
    }

    // --- Concurrency ---

    [Fact]
    public void OneProducer_OneConsumer()
    {
        var queue = new MpscGrowableArrayQueue<string>(4, 1 << 16);
        using var start = new Barrier(2);
        var producer = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < Produce; i++)
            {
                while (!queue.Offer("e" + i)) { }
            }
        });
        var consumer = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < Produce; i++)
            {
                while (queue.Poll() == null) { }
            }
        });
        Task.WaitAll(producer, consumer);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void ManyProducers_NoConsumer_CountMatchesAccepted()
    {
        var queue = new MpscGrowableArrayQueue<string>(4, 1 << 16);
        int count = 0;
        Parallel.For(0, NumProducers, _ =>
        {
            for (int i = 0; i < Produce; i++)
            {
                if (queue.Offer("e" + i))
                {
                    Interlocked.Increment(ref count);
                }
            }
        });
        Assert.Equal(count, queue.Count);
    }

    [Fact]
    public void ManyProducers_OneConsumer_DrainsAll()
    {
        var queue = new MpscGrowableArrayQueue<string>(4, 1 << 16);
        using var start = new Barrier(NumProducers + 1);
        var consumer = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < NumProducers * Produce; i++)
            {
                while (queue.Poll() == null) { }
            }
        });
        Parallel.For(0, NumProducers, _ =>
        {
            start.SignalAndWait();
            for (int i = 0; i < Produce; i++)
            {
                while (!queue.Offer("e" + i)) { }
            }
        });
        consumer.Wait();
        Assert.True(queue.IsEmpty);
    }
}
