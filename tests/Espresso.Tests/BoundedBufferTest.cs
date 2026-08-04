using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class BoundedBufferTest
{
    private static readonly string Item = "x";

    [Fact]
    public void Offer_ConcurrentProducers_AlwaysValidResult()
    {
        var buffer = new BoundedBuffer<string>();
        int? invalid = null;
        Parallel.For(0, 10, _ =>
        {
            for (int i = 0; i < 100; i++)
            {
                int added = buffer.Offer(Item);
                if (added != BufferResult.Success && added != BufferResult.Full && added != BufferResult.Failed)
                {
                    invalid = added;
                }
            }
        });
        Assert.Null(invalid);
        Assert.True(buffer.Writes > 0);
        Assert.Equal(buffer.Size, buffer.Writes);
    }

    [Fact]
    public void Drain_ReadsEqualWrites()
    {
        var buffer = new BoundedBuffer<string>();
        for (int i = 0; i < BoundedBuffer<string>.BufferSize; i++)
        {
            int result = buffer.Offer(Item);
            Assert.True(result == BufferResult.Success || result == BufferResult.Full);
        }
        long read = 0;
        buffer.DrainTo(_ => read++);
        Assert.Equal(buffer.Reads, read);
        Assert.Equal(buffer.Writes, read);
    }

    [Fact]
    public void OfferAndDrain_Interleaved()
    {
        var buffer = new BoundedBuffer<string>();
        var drainLock = new object();
        int reads = 0;
        Parallel.For(0, 10, _ =>
        {
            for (int i = 0; i < 1000; i++)
            {
                bool shouldDrain = buffer.Offer(Item) == BufferResult.Full;
                if (shouldDrain && Monitor.TryEnter(drainLock))
                {
                    try { buffer.DrainTo(_ => Interlocked.Increment(ref reads)); }
                    finally { Monitor.Exit(drainLock); }
                }
                Thread.Yield();
            }
        });
        buffer.DrainTo(_ => Interlocked.Increment(ref reads));
        Assert.Equal(buffer.Reads, reads);
        Assert.Equal(buffer.Writes, reads);
    }

    [Fact]
    public void Full_ReturnsFullOnceCapacityReached()
    {
        var buffer = new BoundedBuffer<string>();
        int successes = 0;
        for (int i = 0; i < BoundedBuffer<string>.BufferSize * 4; i++)
        {
            if (buffer.Offer(Item) == BufferResult.Success)
            {
                successes++;
            }
        }
        // A single uncontended ring holds at most BufferSize before reporting Full.
        Assert.True(successes <= BoundedBuffer<string>.BufferSize);
        Assert.Equal(BufferResult.Full, buffer.Offer(Item));
    }

    [Fact]
    public void DrainThenRefill_WrapsAround()
    {
        var buffer = new BoundedBuffer<string>();
        for (int round = 0; round < 5; round++)
        {
            while (buffer.Offer(Item) == BufferResult.Success) { }
            var drained = new List<string>();
            buffer.DrainTo(drained.Add);
            Assert.NotEmpty(drained);
        }
        Assert.Equal(buffer.Reads, buffer.Writes);
    }

    [Fact]
    public void Disabled_AcceptsEverythingStoresNothing()
    {
        IBuffer<string> buffer = BufferResult.Disabled<string>();
        Assert.Equal(BufferResult.Success, buffer.Offer(Item));
        int drained = 0;
        buffer.DrainTo(_ => drained++);
        Assert.Equal(0, drained);
        Assert.Equal(0, buffer.Size);
    }
}
