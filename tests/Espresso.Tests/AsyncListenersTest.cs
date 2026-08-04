using System;
using System.Threading;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Unit tests for the async listener adapters (<see cref="AsyncRemovalListener{K,V}"/> and
/// <see cref="AsyncEvictionListener{K,V}"/>) that unwrap a <see cref="Task{V}"/> value before forwarding
/// to a user listener. Driven directly with completed/faulted/in-flight futures so every branch is
/// exercised deterministically.
/// </summary>
public sealed class AsyncListenersTest
{
    private sealed class Recorder : IRemovalListener<int, string>
    {
        public (int? Key, string? Value, RemovalCause Cause)? Last;
        public int Count;

        public void OnRemoval(int key, string? value, RemovalCause cause)
        {
            Last = (key, value, cause);
            Count++;
        }
    }

    // ----- AsyncEvictionListener: synchronous, fires only for a ready non-null future -----

    [Fact]
    public void EvictionListener_ReadyFuture_ForwardsUnwrappedValue()
    {
        var recorder = new Recorder();
        var listener = new AsyncEvictionListener<int, string>(recorder);

        listener.OnRemoval(1, Task.FromResult("v"), RemovalCause.Size);

        Assert.Equal((1, "v", RemovalCause.Size), recorder.Last);
    }

    [Fact]
    public void EvictionListener_InFlightFuture_DoesNotFire()
    {
        var recorder = new Recorder();
        var listener = new AsyncEvictionListener<int, string>(recorder);

        listener.OnRemoval(1, new TaskCompletionSource<string>().Task, RemovalCause.Size);

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void EvictionListener_FaultedFuture_DoesNotFire()
    {
        var recorder = new Recorder();
        var listener = new AsyncEvictionListener<int, string>(recorder);

        listener.OnRemoval(1, Task.FromException<string>(new InvalidOperationException()), RemovalCause.Size);

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void EvictionListener_NullFuture_DoesNotFire()
    {
        var recorder = new Recorder();
        var listener = new AsyncEvictionListener<int, string>(recorder);

        listener.OnRemoval(1, null, RemovalCause.Explicit);

        Assert.Equal(0, recorder.Count);
    }

    // ----- AsyncRemovalListener: delivered via executor once the future resolves -----

    [Fact]
    public void RemovalListener_ReadyFuture_ForwardsViaExecutor()
    {
        var recorder = new Recorder();
        var listener = new AsyncRemovalListener<int, string>(recorder, DirectExecutor.Instance);

        listener.OnRemoval(2, Task.FromResult("w"), RemovalCause.Explicit);

        Assert.Equal((2, "w", RemovalCause.Explicit), recorder.Last);
    }

    [Fact]
    public void RemovalListener_NullFuture_DoesNotFire()
    {
        var recorder = new Recorder();
        var listener = new AsyncRemovalListener<int, string>(recorder, DirectExecutor.Instance);

        listener.OnRemoval(2, null, RemovalCause.Explicit);

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void RemovalListener_FaultedFuture_DoesNotFire()
    {
        var recorder = new Recorder();
        var listener = new AsyncRemovalListener<int, string>(recorder, DirectExecutor.Instance);

        listener.OnRemoval(2, Task.FromException<string>(new InvalidOperationException()), RemovalCause.Size);

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void RemovalListener_FutureResolvingToNull_DoesNotFire()
    {
        var recorder = new Recorder();
        var listener = new AsyncRemovalListener<int, string>(recorder, DirectExecutor.Instance);

        listener.OnRemoval(2, Task.FromResult<string>(null!), RemovalCause.Size);

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task RemovalListener_InFlightFuture_FiresOnceResolved()
    {
        var recorder = new Recorder();
        var listener = new AsyncRemovalListener<int, string>(recorder, DirectExecutor.Instance);
        var tcs = new TaskCompletionSource<string>();

        listener.OnRemoval(3, tcs.Task, RemovalCause.Replaced);
        Assert.Equal(0, recorder.Count); // not yet resolved

        tcs.SetResult("late");
        await tcs.Task;

        Assert.Equal((3, "late", RemovalCause.Replaced), recorder.Last);
    }

    [Fact]
    public void RemovalListener_ThrowingDelegate_IsSwallowed()
    {
        var throwing = new ThrowingListener();
        var listener = new AsyncRemovalListener<int, string>(throwing, DirectExecutor.Instance);

        // A misbehaving listener must not surface to the cache.
        listener.OnRemoval(1, Task.FromResult("v"), RemovalCause.Size);

        Assert.True(throwing.WasCalled);
    }

    private sealed class ThrowingListener : IRemovalListener<int, string>
    {
        public bool WasCalled;

        public void OnRemoval(int key, string? value, RemovalCause cause)
        {
            WasCalled = true;
            throw new InvalidOperationException("listener boom");
        }
    }
}
