using System;
using System.Threading;
using System.Threading.Tasks;

namespace Espresso.Tests;

/// <summary>
/// Starts work that blocks (parks in a hook, waits on an event or a lock) on its own thread, so it starts
/// promptly regardless of thread-pool availability while other test classes run in parallel.
/// </summary>
internal static class DedicatedThread
{
    public static Task Run(Action action)
        => Task.Factory.StartNew(action, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static Task<T> Run<T>(Func<T> function)
        => Task.Factory.StartNew(function, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
