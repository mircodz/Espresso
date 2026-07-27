using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Espresso.Internal;

namespace Espresso.Benchmarks;

/// <summary>
/// Benchmarks for the striped read buffer. Producers race to record reads; a single consumer drains.
/// The <see cref="Threads"/> sweep exposes how the dynamic striping scales under contention.
/// </summary>
[MemoryDiagnoser]
public class BoundedBufferBenchmarks
{
    // A trivial reference element; the buffer only stores/forwards the reference.
    private sealed class Item { }

    private static readonly Item Element = new();

    [Params(1, 4, 8)]
    public int Threads;

    [Params(100_000)]
    public int OpsPerThread;

    private BoundedBuffer<Item> _buffer = null!;

    [GlobalSetup]
    public void Setup() => _buffer = new BoundedBuffer<Item>();

    /// <summary>Pure producer throughput: offer, ignoring full/failed results (drops are benign).</summary>
    [Benchmark]
    public void Offer()
    {
        Parallel.For(0, Threads, _ =>
        {
            for (int i = 0; i < OpsPerThread; i++)
            {
                _buffer.Offer(Element);
            }
        });
    }

    /// <summary>Producers offer; when a producer sees FULL it attempts a drain (mirrors the cache).</summary>
    [Benchmark]
    public void OfferAndDrain()
    {
        var drainLock = new object();
        Parallel.For(0, Threads, _ =>
        {
            for (int i = 0; i < OpsPerThread; i++)
            {
                if (_buffer.Offer(Element) == BufferResult.Full
                    && System.Threading.Monitor.TryEnter(drainLock))
                {
                    try { _buffer.DrainTo(static _ => { }); }
                    finally { System.Threading.Monitor.Exit(drainLock); }
                }
            }
        });
    }
}