using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

/// <summary>When the surface's input counts as live, across a disconnect and reconnect.</summary>
public class SurfaceConnectionTests
{
    [Fact]
    public void StartsLive()
    {
        var c = new SurfaceConnection();
        Assert.True(c.IsLive);
        Assert.False(c.RequestReconnect()); // Enter on a live terminal is just Enter
    }

    [Fact]
    public void EnterWhileDisconnectedAsksToReconnectButStaysNotLive()
    {
        var c = new SurfaceConnection();
        c.Disconnected();
        Assert.True(c.RequestReconnect());
        Assert.Equal(SurfaceLinkState.Reconnecting, c.State);
        Assert.False(c.IsLive);
    }

    [Fact]
    public void OnlyTheHostsReportMakesItLiveAgain()
    {
        var c = new SurfaceConnection();
        c.Disconnected();
        c.RequestReconnect();
        c.Connected();
        Assert.True(c.IsLive);
    }

    [Fact]
    public void AFailedReconnectLeavesItDisconnected()
    {
        var c = new SurfaceConnection();
        c.Disconnected();
        c.RequestReconnect();
        c.Disconnected();
        Assert.Equal(SurfaceLinkState.Disconnected, c.State);
        Assert.False(c.IsLive);
    }

    [Fact]
    public void EnterWhileReconnectingAsksAgain()
    {
        // A host that declined the first request never reports back; Enter must still retry.
        var c = new SurfaceConnection();
        c.Disconnected();
        Assert.True(c.RequestReconnect());
        Assert.True(c.RequestReconnect());
        Assert.False(c.IsLive);
    }
}
