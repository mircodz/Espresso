using System;
using System.Threading;

namespace Espresso;

/// <summary>
/// Runs asynchronous tasks for a cache: removal notifications, maintenance draining, and (later)
/// asynchronous loads and refreshes. The default delegates to the thread pool.
/// </summary>
public interface IExecutor
{
    /// <summary>Schedules the action to run.</summary>
    void Execute(Action command);
}

/// <summary>The default <see cref="IExecutor"/>, backed by the .NET thread pool.</summary>
public sealed class ThreadPoolExecutor : IExecutor
{
    /// <summary>The shared singleton instance.</summary>
    public static readonly ThreadPoolExecutor Instance = new();

    private ThreadPoolExecutor() { }

    /// <inheritdoc/>
    public void Execute(Action command)
        => ThreadPool.UnsafeQueueUserWorkItem(static a => a(), command, preferLocal: false);
}

/// <summary>
/// An <see cref="IExecutor"/> that runs the task inline on the calling thread. Useful for tests that
/// need deterministic, synchronous maintenance.
/// </summary>
public sealed class DirectExecutor : IExecutor
{
    /// <summary>The shared singleton instance.</summary>
    public static readonly DirectExecutor Instance = new();

    private DirectExecutor() { }

    /// <inheritdoc/>
    public void Execute(Action command) => command();
}

/// <summary>Adapts a callback to <see cref="IExecutor"/>.</summary>
internal sealed class FuncExecutor(Action<Action> execute) : IExecutor
{
    public void Execute(Action command) => execute(command);
}
