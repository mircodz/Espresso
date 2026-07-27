using System.Diagnostics;

namespace Espresso.Profiling;

/// <summary>
/// A minimal, allocation-light driver that pounds Espresso's hot path in a tight loop so a sampling
/// profiler (dotnet-trace → speedscope) can attribute self-time to the real cache methods without any
/// benchmark-harness noise in the stacks. Not a benchmark — the printed rate is only a sanity check.
///
/// Usage:
///   dotnet run -c Release -- [scenario] [seconds] [threads]
///   scenario ∈ { read, write, readwrite, getoradd }   (default readwrite)
/// </summary>
public static class Program
{
    private const int WorkingSet = 1 << 15;   // 32768, matching Caffeine's GetPutBenchmark
    private const int Mask = WorkingSet - 1;

    public static void Main(string[] args)
    {
        string scenario = args.Length > 0 ? args[0] : "readwrite";
        int seconds = args.Length > 1 ? int.Parse(args[1]) : 20;
        int threads = args.Length > 2 ? int.Parse(args[2]) : Environment.ProcessorCount;

        var cache = Espresso.NewBuilder<int, string>().Build();

        // Precompute the key stream (masked-counter indexing, no hot-loop RNG) and prepopulate to a
        // 100% hit rate for the read paths — same shape as Caffeine's methodology.
        var keys = new int[WorkingSet];
        var values = new string[WorkingSet];
        var rng = new Random(42);
        for (int i = 0; i < WorkingSet; i++)
        {
            keys[i] = rng.Next(WorkingSet);
            values[i] = "v" + i;
            cache.Put(keys[i], values[i]);
        }

        Console.WriteLine($"scenario={scenario} threads={threads} seconds={seconds} pid={Environment.ProcessId}");
        Console.WriteLine("Attach:  dotnet-trace collect -p " + Environment.ProcessId +
                          " --format Speedscope --duration 00:00:" + seconds.ToString("D2"));
        Console.WriteLine("Starting in 2s...");
        Thread.Sleep(2000);

        long deadline = Stopwatch.GetTimestamp() + (long)(seconds * (double)Stopwatch.Frequency);
        long totalOps = 0;
        var workers = new Thread[threads];

        for (int t = 0; t < threads; t++)
        {
            int seed = t;
            workers[t] = new Thread(() =>
            {
                int idx = seed * 2654435761u.GetHashCode();
                long ops = 0;
                switch (scenario)
                {
                    case "read":
                        while (Stopwatch.GetTimestamp() < deadline)
                        {
                            for (int i = 0; i < 1024; i++) { _ = cache.GetIfPresent(keys[idx++ & Mask]); }
                            ops += 1024;
                        }
                        break;

                    case "write":
                        while (Stopwatch.GetTimestamp() < deadline)
                        {
                            for (int i = 0; i < 1024; i++) { int k = idx++ & Mask; cache.Put(keys[k], values[k]); }
                            ops += 1024;
                        }
                        break;

                    case "getoradd":
                        while (Stopwatch.GetTimestamp() < deadline)
                        {
                            for (int i = 0; i < 1024; i++)
                            {
                                int k = keys[idx++ & Mask];
                                _ = cache.Get(k, static key => "v" + key);
                            }
                            ops += 1024;
                        }
                        break;

                    default: // readwrite: 75/25
                        while (Stopwatch.GetTimestamp() < deadline)
                        {
                            for (int i = 0; i < 1024; i++)
                            {
                                int k = idx++ & Mask;
                                if ((i & 3) == 3) { cache.Put(keys[k], values[k]); }
                                else { _ = cache.GetIfPresent(keys[k]); }
                            }
                            ops += 1024;
                        }
                        break;
                }

                Interlocked.Add(ref totalOps, ops);
            })
            { IsBackground = false, Name = "prof-" + t };
            workers[t].Start();
        }

        foreach (var w in workers) w.Join();

        double mops = totalOps / (double)seconds / 1_000_000.0;
        Console.WriteLine($"done: {totalOps:N0} ops  (~{mops:0.0} Mops aggregate, sanity only)");
    }
}
