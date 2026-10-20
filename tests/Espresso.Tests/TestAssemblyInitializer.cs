using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Espresso.Tests;

internal static class TestAssemblyInitializer
{
    private const int MinimumPoolThreads = 64;

    /// <summary>
    /// Raises the thread-pool floor before any test runs. Many tests block pool threads (parked loaders,
    /// listeners, barriers) while xUnit runs test classes in parallel; on a machine with few cores the
    /// default floor (one thread per core) lets the pool starve, and the hill-climbing injection can start
    /// a queued work item only after a test's wait timeout has already expired.
    /// </summary>
    [ModuleInitializer]
    internal static void RaiseThreadPoolFloor()
    {
        ThreadPool.GetMinThreads(out int workerThreads, out int ioThreads);
        ThreadPool.SetMinThreads(
            Math.Max(workerThreads, MinimumPoolThreads),
            Math.Max(ioThreads, MinimumPoolThreads));
    }
}
