using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Espresso.Internal;

namespace Espresso.Benchmarks;

/// <summary>
/// Compares Espresso's <see cref="ConcurrentHashMap{TKey,TValue}"/> against the BCL
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> on the operations the cache uses.
/// Value-type keys (<see cref="int"/>) exercise the box-free hashing path.
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class MapBenchmarks
{
    private const int KeySpace = 100_000;

    [Params(1, 4, 8)]
    public int Threads;

    [Params(50_000)]
    public int OpsPerThread;

    private ConcurrentHashMap<int, string> _chm = null!;
    private ConcurrentDictionary<int, string> _cd = null!;
    private string[] _values = null!;

    [GlobalSetup]
    public void Setup()
    {
        _values = new string[KeySpace];
        _chm = new ConcurrentHashMap<int, string>(KeySpace);
        _cd = new ConcurrentDictionary<int, string>(concurrencyLevel: Environment.ProcessorCount, capacity: KeySpace);
        for (int i = 0; i < KeySpace; i++)
        {
            _values[i] = "v" + i;
            _chm.Put(i, _values[i]);
            _cd[i] = _values[i];
        }
    }

    private void Run(Action<int> body)
    {
        Parallel.For(0, Threads, t =>
        {
            var rng = new Random(t * 7919 + 1);
            for (int i = 0; i < OpsPerThread; i++)
            {
                body(rng.Next(KeySpace));
            }
        });
    }

    // ---------------- Get-heavy ----------------

    [Benchmark(Baseline = true), BenchmarkCategory("Get")]
    public void Get_ConcurrentDictionary() => Run(k => _cd.TryGetValue(k, out _));

    [Benchmark, BenchmarkCategory("Get")]
    public void Get_Espresso() => Run(k => _chm.GetOrDefault(k));

    // ---------------- Put-heavy ----------------

    [Benchmark, BenchmarkCategory("Put")]
    public void Put_ConcurrentDictionary() => Run(k => _cd[k] = _values[k]);

    [Benchmark, BenchmarkCategory("Put")]
    public void Put_Espresso() => Run(k => _chm.Put(k, _values[k]));

    // ---------------- Mixed 80/20 read/write ----------------

    [Benchmark, BenchmarkCategory("Mixed")]
    public void Mixed_ConcurrentDictionary() => Run(k =>
    {
        if ((k & 7) < 6) { _cd.TryGetValue(k, out _); }
        else { _cd[k] = _values[k]; }
    });

    [Benchmark, BenchmarkCategory("Mixed")]
    public void Mixed_Espresso() => Run(k =>
    {
        if ((k & 7) < 6) { _chm.GetOrDefault(k); }
        else { _chm.Put(k, _values[k]); }
    });

    // ---------------- ComputeIfAbsent (all hits) ----------------

    [Benchmark, BenchmarkCategory("ComputeIfAbsent")]
    public void ComputeIfAbsent_ConcurrentDictionary() => Run(k => _cd.GetOrAdd(k, static key => "v" + key));

    [Benchmark, BenchmarkCategory("ComputeIfAbsent")]
    public void ComputeIfAbsent_Espresso() => Run(k => _chm.ComputeIfAbsent(k, static key => "v" + key));

    // ---------------- Insert churn (isolates the striped size counter) ----------------
    // Insert-then-remove of fresh keys keeps AddCount(+1)/AddCount(-1) hot on every op, unlike the
    // Put benchmarks above which overwrite existing keys and never touch the counter.

    [Benchmark, BenchmarkCategory("InsertChurn")]
    public void InsertChurn_ConcurrentDictionary() => RunPerThread((t, k) =>
    {
        _cd[k] = _values[k % KeySpace];
        _cd.TryRemove(k, out _);
    });

    [Benchmark, BenchmarkCategory("InsertChurn")]
    public void InsertChurn_Espresso() => RunPerThread((t, k) =>
    {
        _chm.Put(k, _values[k % KeySpace]);
        _chm.Remove(k);
    });

    // Disjoint per-thread key ranges so ops always insert a fresh key (no cross-thread key sharing).
    private void RunPerThread(Action<int, int> body)
    {
        Parallel.For(0, Threads, t =>
        {
            int baseKey = KeySpace + t * OpsPerThread;
            for (int i = 0; i < OpsPerThread; i++)
            {
                body(t, baseKey + i);
            }
        });
    }
}