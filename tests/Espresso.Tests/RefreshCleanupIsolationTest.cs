using System;
using System.Reflection;
using System.Threading;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

/// <summary>
/// Nothing a refresh does can escape the work item it runs in. In particular, releasing the refresh
/// registration (which hashes the user's key) is inside the catch-all: on the thread pool an exception
/// escaping a work item terminates the process. A rejected submission does not fail the reader either.
/// </summary>
public class RefreshCleanupIsolationTest
{
    private const long Second = 1_000_000_000L;

    private sealed class Clock
    {
        public long Now;
        public long Read() => Interlocked.Read(ref Now);
    }

    private sealed class Switch
    {
        public volatile bool ThrowOnHash;
    }

    /// <summary>A key whose hash throws once its shared switch is flipped.</summary>
    private sealed class FlakyKey : IEquatable<FlakyKey>
    {
        private readonly int _id;
        private readonly Switch _switch;

        public FlakyKey(int id, Switch @switch)
        {
            _id = id;
            _switch = @switch;
        }

        public override int GetHashCode()
            => _switch.ThrowOnHash ? throw new InvalidOperationException("hash boom") : _id;

        public bool Equals(FlakyKey? other) => other != null && other._id == _id;
        public override bool Equals(object? obj) => Equals(obj as FlakyKey);
    }

    /// <summary>Runs work inline and records anything that escapes it (as the thread pool would crash on).</summary>
    private sealed class EscapeRecordingExecutor : IExecutor
    {
        public int Escaped;

        public void Execute(Action command)
        {
            try { command(); }
            catch { Interlocked.Increment(ref Escaped); }
        }
    }

    private sealed class RejectingExecutor : IExecutor
    {
        public required Switch Switch;
        public volatile bool Reject;

        public void Execute(Action command)
        {
            if (!Reject)
            {
                command();
                return;
            }
            Switch.ThrowOnHash = true; // the release of the registration after the rejection will fail
            throw new InvalidOperationException("rejected");
        }
    }

    [Fact]
    public void RefreshWorkItem_ContainsAFailingRegistrationRelease()
    {
        var clock = new Clock();
        var hashes = new Switch();
        var executor = new EscapeRecordingExecutor();
        var cache = Cache.NewBuilder<FlakyKey, string>()
            .Ticker(clock.Read).Executor(executor).RecordStats()
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .Build(_ =>
            {
                hashes.ThrowOnHash = true; // every later hash of the key fails, including the release
                return "reloaded";
            });

        cache.Put(new FlakyKey(1, hashes), "v");
        clock.Now = 2 * Second;
        Assert.Equal("v", cache.GetIfPresent(new FlakyKey(1, hashes))); // triggers the refresh inline

        Assert.Equal(0, executor.Escaped);
        hashes.ThrowOnHash = false;
        Assert.Equal(1, cache.Stats().LoadFailureCount);
    }

    [Fact]
    public void RejectedRefresh_WithAFailingRegistrationRelease_DoesNotFailTheReader()
    {
        var clock = new Clock();
        var hashes = new Switch();
        var executor = new RejectingExecutor { Switch = hashes };
        var cache = Cache.NewBuilder<FlakyKey, string>()
            .Ticker(clock.Read).Executor(executor)
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .Build(_ => "reloaded");

        cache.Put(new FlakyKey(1, hashes), "v");
        clock.Now = 2 * Second;
        executor.Reject = true; // the next submission is the refresh
        Assert.Equal("v", cache.GetIfPresent(new FlakyKey(1, hashes)));
        hashes.ThrowOnHash = false;
    }
}
