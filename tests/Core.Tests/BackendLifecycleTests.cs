using Resesh.Core.Backend;

namespace Resesh.Core.Tests;

/// <summary>The shared connect/close/stop state machine, with each race driven in an explicit order.</summary>
public sealed class BackendLifecycleTests
{
    private static BackendLifecycle Open()
    {
        var lifecycle = new BackendLifecycle();
        Assert.True(lifecycle.TryBeginConnect());
        Assert.True(lifecycle.TryPublishOpen());
        return lifecycle;
    }

    [Fact]
    public void ConnectsOnceThenOpens()
    {
        var lifecycle = new BackendLifecycle();
        Assert.Equal(BackendLifecycleState.NotStarted, lifecycle.State);

        Assert.True(lifecycle.TryBeginConnect());
        Assert.False(lifecycle.TryBeginConnect());
        Assert.Equal(BackendLifecycleState.Connecting, lifecycle.State);
        Assert.False(lifecycle.IsOpen);

        Assert.True(lifecycle.TryPublishOpen());
        Assert.False(lifecycle.TryPublishOpen());
        Assert.True(lifecycle.IsOpen);
        Assert.False(lifecycle.TryBeginConnect());
        Assert.IsType<InvalidOperationException>(lifecycle.CannotConnect());
    }

    [Fact]
    public void PublishingWithoutConnectingFails()
    {
        var lifecycle = new BackendLifecycle();
        Assert.False(lifecycle.TryPublishOpen());
        Assert.Equal(BackendLifecycleState.NotStarted, lifecycle.State);
    }

    [Fact]
    public void StopDuringConnectMakesThePublishFail()
    {
        var lifecycle = new BackendLifecycle();
        lifecycle.TryBeginConnect();

        Assert.True(lifecycle.BeginStop(out var previous));
        Assert.Equal(BackendLifecycleState.Connecting, previous);

        // The connect thread finds out here and tears down what it built.
        Assert.False(lifecycle.TryPublishOpen());
        Assert.True(lifecycle.IsStopped);
        Assert.IsAssignableFrom<OperationCanceledException>(lifecycle.CannotConnect());
    }

    [Fact]
    public void StopBeforeConnectRefusesTheConnect()
    {
        var lifecycle = new BackendLifecycle();

        Assert.True(lifecycle.BeginStop(out var previous));
        Assert.Equal(BackendLifecycleState.NotStarted, previous);
        Assert.False(lifecycle.TryBeginConnect());
        Assert.IsAssignableFrom<OperationCanceledException>(lifecycle.CannotConnect());
    }

    [Fact]
    public void RemoteCloseIsRaisedOnceByTheFirstDetector()
    {
        var lifecycle = Open();
        var raised = 0;

        Assert.True(lifecycle.TryRaiseRemoteClose(() => raised++));
        Assert.False(lifecycle.TryRaiseRemoteClose(() => raised++));

        Assert.Equal(1, raised);
        Assert.Equal(BackendLifecycleState.RemoteClosed, lifecycle.State);
        Assert.False(lifecycle.IsOpen);
        Assert.False(lifecycle.IsStopped);
    }

    [Fact]
    public void RemoteCloseAfterStopIsNotRaised()
    {
        var lifecycle = Open();
        Assert.True(lifecycle.BeginStop());

        Assert.False(lifecycle.TryRaiseRemoteClose(() => Assert.Fail("Closed raised after a local stop")));
        Assert.True(lifecycle.IsStopped);
    }

    [Fact]
    public void StopAfterRemoteCloseStillTearsDown()
    {
        var lifecycle = Open();
        lifecycle.TryRaiseRemoteClose(() => { });

        Assert.True(lifecycle.BeginStop(out var previous));
        Assert.Equal(BackendLifecycleState.RemoteClosed, previous);
        Assert.True(lifecycle.IsStopped);
    }

    [Fact]
    public void RemoteCloseWhileConnectingIsNotRaised()
    {
        var lifecycle = new BackendLifecycle();
        lifecycle.TryBeginConnect();

        Assert.False(lifecycle.TryRaiseRemoteClose(() => Assert.Fail("Closed raised while connecting")));
        Assert.True(lifecycle.TryPublishOpen());
    }

    [Fact]
    public void OnlyTheFirstStopWins()
    {
        var lifecycle = Open();

        Assert.True(lifecycle.BeginStop());
        Assert.False(lifecycle.BeginStop(out var previous));
        Assert.Equal(BackendLifecycleState.Stopped, previous);
    }

    [Fact]
    public void AStopFromInsideTheClosedHandlerDoesNotDeadlock()
    {
        var lifecycle = Open();
        var stoppedInside = false;

        lifecycle.TryRaiseRemoteClose(() => stoppedInside = lifecycle.BeginStop());

        Assert.True(stoppedInside);
        Assert.True(lifecycle.IsStopped);
    }

    [Fact]
    public async Task StopWaitsForAClosedAlreadyBeingRaised()
    {
        var lifecycle = Open();
        using var inHandler = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var raising = Task.Run(() => lifecycle.TryRaiseRemoteClose(() =>
        {
            inHandler.Set();
            release.Wait();
        }));
        Assert.True(inHandler.Wait(TimeSpan.FromSeconds(5)));

        var stopping = Task.Run(() => lifecycle.BeginStop());
        await Task.Delay(100);
        Assert.False(stopping.IsCompleted);

        release.Set();
        Assert.True(await raising);
        Assert.True(await stopping);
    }

    [Fact]
    public async Task RemoteCloseAndStopRacingNeverRaiseAfterTheStopReturns()
    {
        for (var i = 0; i < 500; i++)
        {
            var lifecycle = Open();
            var stopReturned = 0;
            var raisedLate = false;
            var raised = false;
            BackendLifecycleState previous = default;
            using var start = new Barrier(2);
            var remote = Task.Run(() =>
            {
                start.SignalAndWait();
                raised = lifecycle.TryRaiseRemoteClose(() => raisedLate = Volatile.Read(ref stopReturned) != 0);
            });
            var local = Task.Run(() =>
            {
                start.SignalAndWait();
                lifecycle.BeginStop(out previous);
                Volatile.Write(ref stopReturned, 1);
            });
            await Task.WhenAll(remote, local);

            Assert.False(raisedLate);
            // Exactly one won: either Closed was raised before the stop, or the stop came first.
            Assert.Equal(raised ? BackendLifecycleState.RemoteClosed : BackendLifecycleState.Open, previous);
            Assert.True(lifecycle.IsStopped);
        }
    }
}
