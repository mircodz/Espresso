using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Espresso.Internal;

namespace Espresso.Benchmarks;

/// <summary>
/// Benchmarks for the growable MPSC write buffer under a producer/consumer split — the shape the
/// cache uses (many threads recording writes, the maintenance thread draining).
/// </summary>
[MemoryDiagnoser]
public class MpscQueueBenchmarks
{
    private sealed class Item { }

    private static readonly Item Element = new();

    [Params(1, 4, 8)]
    public int Producers;

    [Params(50_000)]
    public int OpsPerProducer;

    /// <summary>Producers offer concurrently while one consumer drains everything.</summary>
    [Benchmark]
    public void OfferPoll_OneConsumer()
    {
        var queue = new MpscGrowableArrayQueue<Item>(4, 1 << 20);
        int total = Producers * OpsPerProducer;

        var consumer = Task.Run(() =>
        {
            int drained = 0;
            while (drained < total)
            {
                if (queue.Poll() != null) drained++;
            }
        });

        Parallel.For(0, Producers, _ =>
        {
            for (int i = 0; i < OpsPerProducer; i++)
            {
                while (!queue.Offer(Element)) { }
            }
        });

        consumer.Wait();
    }
}