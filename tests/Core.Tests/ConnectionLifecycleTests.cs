using System.Net;
using System.Net.Sockets;
using Resesh.Core.Local;
using Resesh.Core.Models;
using Resesh.Core.Sftp;
using Resesh.Core.Ssh;
using Resesh.Core.Telnet;

namespace Resesh.Core.Tests;

/// <summary>
/// Closing a tab while its connection is still being made must leave nothing open, and a
/// connection that is stopped locally must never report a remote close afterwards.
/// </summary>
public sealed class ConnectionLifecycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("connection-lifecycle-tests").FullName;
    private readonly KnownHostsStore _knownHosts;

    public ConnectionLifecycleTests()
    {
        _knownHosts = new KnownHostsStore(Path.Combine(_dir, "known_hosts.json"));
        _knownHosts.Load();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Session SessionFor(TestSshHost host) => new()
    {
        Name = host.Name,
        Host = "127.0.0.1",
        Port = host.Port,
        Username = "test",
        AuthMethod = AuthMethod.Password,
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
    public void Ssh_DisconnectBeforeConnect_NeverDials()
    {
        using var host = new TestSshHost("host");
        var ssh = new SshTerminalSession(_knownHosts) { HostKeyDecision = _ => true };
        ssh.Disconnect();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ssh.Connect(SessionFor(host), "test123", "xterm", 80, 24, openShell: false));
        Assert.Equal(0, host.Connections);
    }

    [Fact]
    public void Ssh_DisconnectWhileConnecting_TearsDownWhatTheConnectBuilt()
    {
        using var host = new TestSshHost("host");
        var tunnel = new Tunnel { BindPort = TestSshHost.FreePort(), DestinationHost = "127.0.0.1", DestinationPort = 9 };
        var listeningBeforeDisconnect = false;
        var ssh = new SshTerminalSession(_knownHosts) { HostKeyDecision = _ => true };
        var closed = false;
        ssh.Closed += _ => closed = true;

        // The factory runs after the client and its tunnels are up, before the shell opens:
        // the point a tab closing mid-connect used to leak the whole connection.
        Assert.ThrowsAny<OperationCanceledException>(() => ssh.Connect(SessionFor(host), "test123", "xterm", 80, 24,
            tunnels: [tunnel],
            bootstrapCommandFactory: session =>
            {
                listeningBeforeDisconnect = IsListening(tunnel.BindPort);
                session.Disconnect();
                return null;
            }));

        Assert.True(listeningBeforeDisconnect);
        Assert.False(IsListening(tunnel.BindPort));
        Assert.False(ssh.IsConnected);
        Assert.Null(ssh.RunCommand("whoami"));
        Assert.False(closed);
    }

    [Fact]
    public void Ssh_AFailedConnectCannotBeReused()
    {
        using var host = new TestSshHost("host");
        var ssh = new SshTerminalSession(_knownHosts) { HostKeyDecision = _ => true };

        Assert.ThrowsAny<SshSessionException>(() =>
            ssh.Connect(SessionFor(host), "wrong", "xterm", 80, 24, openShell: false));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ssh.Connect(SessionFor(host), "test123", "xterm", 80, 24, openShell: false));
    }

    [Fact]
    public void Ssh_ExecOnlyConnectionSaysSo()
    {
        using var host = new TestSshHost("host");
        using var ssh = new SshTerminalSession(_knownHosts) { HostKeyDecision = _ => true };

        ssh.Connect(SessionFor(host), "test123", "xterm", 80, 24, openShell: false);

        Assert.True(ssh.IsExecOnly);
        Assert.False(ssh.IsConnected);
        Assert.Equal("host", ssh.RunCommand("whoami")?.Output);
        Assert.Throws<InvalidOperationException>(() =>
            ssh.Connect(SessionFor(host), "test123", "xterm", 80, 24, openShell: false));
    }

    [Fact]
    public void Sftp_DisposeBeforeConnect_NeverDials()
    {
        using var host = new TestSshHost("host");
        var sftp = new SftpSession(_knownHosts);
        sftp.Dispose();

        Assert.ThrowsAny<OperationCanceledException>(() => sftp.Connect(SessionFor(host), "test123"));
        Assert.Equal(0, host.Connections);
    }

    [Fact]
    public void Sftp_CancelledConnect_NeverDials()
    {
        using var host = new TestSshHost("host");
        using var sftp = new SftpSession(_knownHosts);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            sftp.Connect(SessionFor(host), "test123", cancellationToken: new CancellationToken(canceled: true)));
        Assert.Equal(0, host.Connections);
        Assert.False(sftp.IsConnected);
    }

    private sealed class Route : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Telnet_DisposeBeforeConnect_ReleasesTheRoute()
    {
        var telnet = new TelnetTerminalSession();
        telnet.Dispose();
        var route = new Route();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            telnet.Connect("127.0.0.1", 23, "xterm", 80, 24, route: route));
        Assert.True(route.Disposed);
    }

    [Fact]
    public void Telnet_AFailedConnectReleasesTheRoute()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var telnet = new TelnetTerminalSession();
        var route = new Route();

        Assert.Throws<TelnetSessionException>(() => telnet.Connect("127.0.0.1", port, "xterm", 80, 24, route: route));
        Assert.True(route.Disposed);
    }

    [Fact]
    public void Telnet_ServerCloseAfterLocalStop_RaisesNothing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var telnet = new TelnetTerminalSession();
        var closed = 0;
        telnet.Closed += _ => Interlocked.Increment(ref closed);
        var accept = listener.AcceptSocketAsync();
        telnet.Connect("127.0.0.1", port, "xterm", 80, 24);
        using var server = accept.GetAwaiter().GetResult();
        Assert.True(telnet.IsConnected);

        telnet.Stop();
        server.Close();
        Thread.Sleep(200);

        Assert.Equal(0, Volatile.Read(ref closed));
        Assert.False(telnet.IsConnected);
    }

    [Fact]
    public void Telnet_ServerClose_RaisesClosedOnce()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var telnet = new TelnetTerminalSession();
        using var closed = new ManualResetEventSlim();
        var count = 0;
        telnet.Closed += _ =>
        {
            Interlocked.Increment(ref count);
            closed.Set();
        };
        var accept = listener.AcceptSocketAsync();
        telnet.Connect("127.0.0.1", port, "xterm", 80, 24);
        using (var server = accept.GetAwaiter().GetResult())
            server.Shutdown(SocketShutdown.Both);

        Assert.True(closed.Wait(TimeSpan.FromSeconds(5)));
        telnet.Stop();
        Assert.Equal(1, Volatile.Read(ref count));
        Assert.False(telnet.IsConnected);
    }

    [Fact]
    public void Local_StopBeforeStart_RefusesToLaunch()
    {
        var local = new LocalTerminalSession();
        local.Stop();
        var session = new Session
        {
            Kind = SessionKind.Local,
            Local = new LocalTarget { Executable = "cmd.exe", Arguments = ["/c", "exit"] },
        };

        Assert.ThrowsAny<OperationCanceledException>(() => local.Start(session, 80, 24));
        Assert.Equal(0, local.ProcessId);
        Assert.False(local.IsRunning);
    }
}
