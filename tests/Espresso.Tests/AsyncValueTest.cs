using System;
using System.Threading.Tasks;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Unit tests for <see cref="AsyncValue"/>, the readiness/unwrap helpers the async cache uses to reason
/// about entries whose stored value is a <see cref="Task{V}"/>.
/// </summary>
public sealed class AsyncValueTest
{
    private static Task<string> Faulted()
        => Task.FromException<string>(new InvalidOperationException("boom"));

    private static Task<string> Canceled()
    {
        var tcs = new TaskCompletionSource<string>();
        tcs.SetCanceled();
        return tcs.Task;
    }

    [Fact]
    public void IsReady_CompletedSuccessfully_True()
    {
        Assert.True(AsyncValue.IsReady(Task.FromResult("x")));
    }

    [Fact]
    public void IsReady_NullOrIncompleteOrFailed_False()
    {
        Assert.False(AsyncValue.IsReady(null));
        Assert.False(AsyncValue.IsReady(new TaskCompletionSource<string>().Task)); // in-flight
        Assert.False(AsyncValue.IsReady(Faulted()));
        Assert.False(AsyncValue.IsReady(Canceled()));
    }

    [Fact]
    public void GetIfReady_ReadyNonNull_ReturnsValue()
    {
        Assert.Equal("x", AsyncValue.GetIfReady(Task.FromResult<string>("x")));
    }

    [Fact]
    public void GetIfReady_NullFutureOrInFlightOrFailed_ReturnsNull()
    {
        Assert.Null(AsyncValue.GetIfReady<string>(null));
        Assert.Null(AsyncValue.GetIfReady(new TaskCompletionSource<string>().Task));
        Assert.Null(AsyncValue.GetIfReady(Faulted()));
    }

    [Fact]
    public void GetIfReady_CompletedWithNull_ReturnsNull()
    {
        Assert.Null(AsyncValue.GetIfReady(Task.FromResult<string?>(null)));
    }

    [Fact]
    public void GetWhenSuccessful_Success_ReturnsValue()
    {
        Assert.Equal("x", AsyncValue.GetWhenSuccessful(Task.FromResult<string>("x")));
    }

    [Fact]
    public void GetWhenSuccessful_NullFuture_ReturnsNull()
    {
        Assert.Null(AsyncValue.GetWhenSuccessful<string>(null));
    }

    [Fact]
    public void GetWhenSuccessful_Faulted_ReturnsNull()
    {
        Assert.Null(AsyncValue.GetWhenSuccessful(Faulted()));
    }

    [Fact]
    public void GetWhenSuccessful_Canceled_ReturnsNull()
    {
        Assert.Null(AsyncValue.GetWhenSuccessful(Canceled()));
    }

    [Fact]
    public async Task GetWhenSuccessful_BlocksUntilCompletion()
    {
        var tcs = new TaskCompletionSource<string>();
        var reader = Task.Run(() => AsyncValue.GetWhenSuccessful(tcs.Task));

        Assert.False(reader.IsCompleted); // still blocked on the in-flight future
        tcs.SetResult("done");

        Assert.Equal("done", await reader);
    }
}
