using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

/// <summary>
/// A stop, remove or dispose that lands while a tunnel is starting (the start blocks outside
/// the set's lock) must win: the listener it was creating is closed, never orphaned.
/// <see cref="SshTunnelSet.BeforeListen"/> runs at the point a real start blocks.
/// </summary>
public sealed class SshTunnelSetTests : IDisposable
{
    private readonly TestSshHost _host = new("host");
    private readonly SshClient _client;
    private readonly List<TunnelStatus> _changes = [];

    public SshTunnelSetTests()
    {
        _client = new SshClient("127.0.0.1", _host.Port, "test", "test123");
        _client.Connect();
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    private SshTunnelSet Set(Action<SshTunnelSet, Tunnel>? beforeListen = null)
    {
        SshTunnelSet? set = null;
        set = new SshTunnelSet(_client, status => { lock (_changes) _changes.Add(status); })
        {
            BeforeListen = beforeListen is null ? null : tunnel => beforeListen(set!, tunnel),
        };
        return set;
    }

    private static Tunnel NewTunnel() => new()
    {
        BindPort = TestSshHost.FreePort(),
        DestinationHost = "127.0.0.1",
        DestinationPort = 9,
    };

    private static bool IsListening(int port)
    {
        using var probe = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            probe.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    [Fact]
    public void StartsAndStops()
    {
        using var set = Set();
        var tunnel = NewTunnel();

        Assert.Equal(TunnelState.Listening, set.Start(tunnel).State);
        Assert.True(IsListening(tunnel.BindPort));

        set.Stop(tunnel.Id);

        Assert.False(IsListening(tunnel.BindPort));
        Assert.Equal(TunnelState.Stopped, Assert.Single(set.Snapshot()).State);
        Assert.Equal([TunnelState.Listening, TunnelState.Stopped], _changes.Select(change => change.State));
    }

    [Fact]
    public void StopWhileStartingClosesTheNewListener()
    {
        using var set = Set((set, tunnel) => set.Stop(tunnel.Id));
        var tunnel = NewTunnel();

        var status = set.Start(tunnel);

        Assert.Equal(TunnelState.Stopped, status.State);
        Assert.False(IsListening(tunnel.BindPort));
        Assert.Equal(TunnelState.Stopped, Assert.Single(set.Snapshot()).State);
        Assert.Equal([TunnelState.Stopped], _changes.Select(change => change.State));
    }

    [Fact]
    public void RemoveWhileStartingLeavesNoListenerAndNoEntry()
    {
        using var set = Set((set, tunnel) => set.Remove(tunnel.Id));
        var tunnel = NewTunnel();

        var status = set.Start(tunnel);

        Assert.Equal(TunnelState.Stopped, status.State);
        Assert.False(IsListening(tunnel.BindPort));
        Assert.Empty(set.Snapshot());
    }

    [Fact]
    public void DisposeWhileStartingClosesTheListenerSilently()
    {
        using var set = Set((set, _) => set.Dispose());
        var tunnel = NewTunnel();

        var status = set.Start(tunnel);

        Assert.Equal(TunnelState.Stopped, status.State);
        Assert.False(IsListening(tunnel.BindPort));
        Assert.Empty(_changes);
    }

    [Fact]
    public void ASecondStartOfATunnelThatIsStartingIsRefused()
    {
        TunnelStatus? second = null;
        using var set = Set((set, tunnel) => second ??= set.Start(tunnel));
        var tunnel = NewTunnel();

        var first = set.Start(tunnel);

        Assert.Equal(TunnelState.Listening, first.State);
        Assert.Equal(TunnelState.Failed, second?.State);
        Assert.Equal(SshTunnelSet.AlreadyStarting, second?.Error);
        Assert.Equal(first, Assert.Single(set.Snapshot()));
        Assert.True(IsListening(tunnel.BindPort));
    }

    [Fact]
    public void RestartingReplacesTheListenerOnTheSamePort()
    {
        using var set = Set();
        var tunnel = NewTunnel();
        Assert.Equal(TunnelState.Listening, set.Start(tunnel).State);

        // The old listener is closed before the new one binds the same port.
        Assert.Equal(TunnelState.Listening, set.Start(tunnel).State);

        Assert.Single(set.Snapshot());
        Assert.True(IsListening(tunnel.BindPort));
    }

    [Fact]
    public void StartAfterDisposeFails()
    {
        var set = Set();
        set.Dispose();

        var status = set.Start(NewTunnel());

        Assert.Equal(TunnelState.Failed, status.State);
        Assert.Equal(SshTunnelSet.NotConnected, status.Error);
    }
}
