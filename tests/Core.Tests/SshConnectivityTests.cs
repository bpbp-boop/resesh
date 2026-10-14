using System.Net;
using System.Net.Sockets;
using System.Text;
using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

/// <summary>Jump hosts, proxies and tunnels against loopback SSH servers.</summary>
public sealed class SshConnectivityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ssh-connectivity-tests").FullName;
    private readonly KnownHostsStore _knownHosts;

    public SshConnectivityTests()
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

    private SshTerminalSession Connect(TestSshHost target, params SshHop[] jumps) =>
        Connect(SessionFor(target), jumps);

    private SshTerminalSession Connect(Session target, IReadOnlyList<SshHop>? jumps = null, string? proxyPassword = null)
    {
        var session = new SshTerminalSession(_knownHosts) { HostKeyDecision = _ => true };
        try
        {
            session.Connect(target, "test123", "xterm", 80, 24, openShell: false, jumps: jumps, proxyPassword: proxyPassword);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static SshHop Hop(TestSshHost host, string password = "test123") => new(SessionFor(host), password);

    [Fact]
    public void ConnectsThroughAJumpHost()
    {
        using var bastion = new TestSshHost("bastion");
        using var target = new TestSshHost("target");

        using var session = Connect(target, Hop(bastion));

        Assert.Equal("target", session.RunCommand("whoami")?.Output);
        Assert.Equal("SSH-2.0-target", session.ServerBanner);
        // Once for the reachability probe, once for the connection itself.
        Assert.Equal([$"127.0.0.1:{target.Port}"], bastion.Forwarded.Distinct());
        // Keys are trusted under each host's real address, not the forwarded loopback port.
        Assert.NotNull(_knownHosts.Lookup("127.0.0.1", bastion.Port));
        Assert.NotNull(_knownHosts.Lookup("127.0.0.1", target.Port));
    }

    [Fact]
    public void ConnectsThroughAChainOfJumpHosts()
    {
        using var outer = new TestSshHost("outer");
        using var inner = new TestSshHost("inner");
        using var target = new TestSshHost("target");

        using var session = Connect(target, Hop(outer), Hop(inner));

        Assert.Equal("target", session.RunCommand("whoami")?.Output);
        Assert.Equal([$"127.0.0.1:{inner.Port}"], outer.Forwarded.Distinct());
        Assert.Equal([$"127.0.0.1:{target.Port}"], inner.Forwarded.Distinct());
    }

    private List<string> ConnectAskingForLogins(TestSshHost target, Func<SshHop, SshHop?> answer, params TestSshHost[] jumps)
    {
        var events = new List<string>();
        using var session = new SshTerminalSession(_knownHosts)
        {
            HostKeyDecision = info =>
            {
                events.Add($"key {info.Port}");
                return true;
            },
        };
        try
        {
            session.Connect(SessionFor(target), null, "xterm", 80, 24, openShell: false,
                jumps: jumps.Select(jump => new SshHop(SessionFor(jump), null)).ToList(),
                resolveLogin: hop =>
                {
                    events.Add($"login {hop.Session.Port}");
                    return answer(hop);
                });
            events.Add("connected");
        }
        catch (Exception ex) when (ex is SshSessionException or OperationCanceledException)
        {
            events.Add(ex is SshSessionException failure ? $"failed {failure.Kind}" : "cancelled");
        }
        return events;
    }

    [Fact]
    public void LoginsAreAskedForHopByHopAsTheConnectReachesEachHost()
    {
        using var outer = new TestSshHost("outer");
        using var inner = new TestSshHost("inner");
        using var target = new TestSshHost("target");

        var events = ConnectAskingForLogins(target, hop => hop with { Secret = "test123" }, outer, inner);

        Assert.Equal(
        [
            $"login {outer.Port}", $"key {outer.Port}",
            $"login {inner.Port}", $"key {inner.Port}",
            $"login {target.Port}", $"key {target.Port}",
            "connected",
        ], events);
    }

    [Fact]
    public void AJumpThatRefusesItsLoginStopsBeforeTheNextLoginIsAskedFor()
    {
        using var bastion = new TestSshHost("bastion");
        using var target = new TestSshHost("target");

        var events = ConnectAskingForLogins(target, hop => hop with { Secret = "wrong" }, bastion);

        Assert.Equal([$"login {bastion.Port}", $"key {bastion.Port}", "failed AuthenticationFailed"], events);
        Assert.Equal(0, target.Connections);
    }

    [Fact]
    public void DecliningAJumpLoginCancelsWithoutDiallingAnything()
    {
        using var bastion = new TestSshHost("bastion");
        using var target = new TestSshHost("target");

        var events = ConnectAskingForLogins(target, _ => null, bastion);

        Assert.Equal([$"login {bastion.Port}", "cancelled"], events);
        Assert.Equal(0, bastion.Connections);
    }

    [Fact]
    public void AJumpLoginThatCannotBeResolvedIsReportedAgainstTheJump()
    {
        using var bastion = new TestSshHost("bastion");
        using var target = new TestSshHost("target");
        using var session = new SshTerminalSession(_knownHosts) { HostKeyDecision = _ => true };

        var failure = Assert.Throws<SshSessionException>(() => session.Connect(
            SessionFor(target), null, "xterm", 80, 24, openShell: false,
            jumps: [new SshHop(SessionFor(bastion), null)],
            resolveLogin: _ => throw new InvalidOperationException("it has no username.")));

        Assert.Equal("Jump host bastion: it has no username.", failure.Message);
        Assert.NotNull(failure.JumpSessionId);
    }

    [Fact]
    public void JumpHostLoginFailureNamesTheJump()
    {
        using var bastion = new TestSshHost("bastion");
        using var target = new TestSshHost("target");
        var hop = Hop(bastion, password: "wrong");

        var failure = Assert.Throws<SshSessionException>(() => Connect(target, hop));

        Assert.Equal(SshFailureKind.AuthenticationFailed, failure.Kind);
        Assert.Equal(hop.Session.Id, failure.JumpSessionId);
        Assert.StartsWith("Jump host bastion:", failure.Message);
        Assert.Equal(0, target.Connections);
    }

    [Fact]
    public void TargetLoginFailureIsNotBlamedOnTheJump()
    {
        using var bastion = new TestSshHost("bastion");
        using var target = new TestSshHost("target", password: "other");

        var failure = Assert.Throws<SshSessionException>(() => Connect(target, Hop(bastion)));

        Assert.Equal(SshFailureKind.AuthenticationFailed, failure.Kind);
        Assert.Null(failure.JumpSessionId);
    }

    [Fact]
    public void UnreachableTargetBehindAJumpSaysTheJumpCouldNotReachIt()
    {
        using var bastion = new TestSshHost("bastion");
        var target = new Session { Name = "gone", Host = "127.0.0.1", Port = TestSshHost.FreePort(), Username = "test" };

        var failure = Assert.Throws<SshSessionException>(() => Connect(target, [Hop(bastion)]));

        Assert.Equal(SshFailureKind.HostUnreachable, failure.Kind);
        Assert.StartsWith($"The jump host could not reach 127.0.0.1:{target.Port}", failure.Message);
    }

    [Fact]
    public void UntrustedJumpHostKeyStopsBeforeTheTarget()
    {
        using var bastion = new TestSshHost("bastion");
        using var target = new TestSshHost("target");
        using var session = new SshTerminalSession(_knownHosts) { HostKeyDecision = _ => false };

        var failure = Assert.Throws<SshSessionException>(() => session.Connect(
            SessionFor(target), "test123", "xterm", 80, 24, openShell: false, jumps: [Hop(bastion)]));

        Assert.Equal(SshFailureKind.HostKeyRejected, failure.Kind);
        Assert.Equal(0, target.Connections);
    }

    [Fact]
    public void ClosingTheSessionClosesItsJumpConnections()
    {
        using var bastion = new TestSshHost("bastion");
        using var target = new TestSshHost("target");
        using var chain = SshJumpChain.Open([Hop(bastion)], "127.0.0.1", target.Port, _knownHosts, _ => true);
        var port = chain.Port;

        chain.Dispose();

        using var probe = new Socket(SocketType.Stream, ProtocolType.Tcp);
        Assert.ThrowsAny<SocketException>(() => probe.Connect(IPAddress.Loopback, port));
    }

    [Fact]
    public void ConnectsThroughASocks5Proxy()
    {
        using var target = new TestSshHost("target");
        using var proxy = new Socks5Stub();
        var session = SessionFor(target) with
        {
            Proxy = new ProxySettings { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = proxy.Port },
        };

        using var connected = Connect(session);

        Assert.Equal("target", connected.RunCommand("whoami")?.Output);
        Assert.Equal([target.Port], proxy.Destinations);
    }

    /// <summary>Pins what the docs promise: a SOCKS5 proxy is handed the host name, so a name
    /// only the proxy's network can resolve still works.</summary>
    [Fact]
    public void Socks5ProxyIsGivenTheHostNameToResolve()
    {
        using var target = new TestSshHost("target");
        using var proxy = new Socks5Stub();
        var session = SessionFor(target) with
        {
            Host = "localhost",
            Proxy = new ProxySettings { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = proxy.Port },
        };

        using var connected = Connect(session);

        Assert.Equal([3], proxy.AddressTypes.Distinct());
    }

    [Fact]
    public void UnreachableProxyFailsFastAndSaysSo()
    {
        using var target = new TestSshHost("target");
        var session = SessionFor(target) with
        {
            Proxy = new ProxySettings { Host = "127.0.0.1", Port = TestSshHost.FreePort() },
        };

        var failure = Assert.Throws<SshSessionException>(() => Connect(session));

        Assert.Equal(SshFailureKind.HostUnreachable, failure.Kind);
        Assert.StartsWith("Proxy:", failure.Message);
        Assert.Equal(0, target.Connections);
    }

    [Fact]
    public void AJumpHostReplacesTheSessionsOwnProxy()
    {
        using var bastion = new TestSshHost("bastion");
        using var target = new TestSshHost("target");
        var bastionSession = SessionFor(bastion);
        var session = SessionFor(target) with
        {
            JumpHostId = bastionSession.Id,
            Proxy = new ProxySettings { Host = "127.0.0.1", Port = TestSshHost.FreePort() },
        };

        using var connected = Connect(session, [new SshHop(bastionSession, "test123")]);

        Assert.Equal("target", connected.RunCommand("whoami")?.Output);
    }

    [Fact]
    public async Task LocalTunnelCarriesConnectionsToTheDestination()
    {
        using var host = new TestSshHost("host");
        using var echo = new EchoServer();
        using var session = Connect(host);
        var changes = new List<TunnelStatus>();
        session.TunnelChanged += changes.Add;
        var tunnel = new Tunnel
        {
            Kind = TunnelKind.Local,
            BindPort = TestSshHost.FreePort(),
            DestinationHost = "127.0.0.1",
            DestinationPort = echo.Port,
        };

        var status = session.StartTunnel(tunnel);

        Assert.Equal(TunnelState.Listening, status.State);
        Assert.Equal("hello", await RoundTripAsync(tunnel.BindPort, "hello"));
        Assert.Equal([status], session.Tunnels);

        session.StopTunnel(tunnel.Id);

        Assert.Equal(TunnelState.Stopped, Assert.Single(session.Tunnels).State);
        Assert.Equal([TunnelState.Listening, TunnelState.Stopped], changes.Select(change => change.State));
        using var probe = new Socket(SocketType.Stream, ProtocolType.Tcp);
        Assert.ThrowsAny<SocketException>(() => probe.Connect(IPAddress.Loopback, tunnel.BindPort));
    }

    [Fact]
    public void TunnelOnABusyPortFailsWithoutEndingTheSession()
    {
        using var host = new TestSshHost("host");
        using var busy = new EchoServer();
        using var session = Connect(host);

        var status = session.StartTunnel(new Tunnel
        {
            BindPort = busy.Port,
            DestinationHost = "127.0.0.1",
            DestinationPort = 80,
        });

        Assert.Equal(TunnelState.Failed, status.State);
        Assert.Equal($"Port {busy.Port} is already in use on this computer.", status.Error);
        Assert.Equal("host", session.RunCommand("whoami")?.Output);
    }

    [Fact]
    public void InvalidTunnelFailsWithItsValidationMessage()
    {
        using var host = new TestSshHost("host");
        using var session = Connect(host);

        var status = session.StartTunnel(new Tunnel { BindPort = TestSshHost.FreePort() });

        Assert.Equal(TunnelState.Failed, status.State);
        Assert.Equal("Enter the destination host.", status.Error);
    }

    [Fact]
    public void TunnelsEndWithTheSession()
    {
        using var host = new TestSshHost("host");
        using var echo = new EchoServer();
        var session = Connect(host);
        var tunnel = new Tunnel
        {
            BindPort = TestSshHost.FreePort(),
            DestinationHost = "127.0.0.1",
            DestinationPort = echo.Port,
        };
        Assert.Equal(TunnelState.Listening, session.StartTunnel(tunnel).State);

        session.Dispose();

        using var probe = new Socket(SocketType.Stream, ProtocolType.Tcp);
        Assert.ThrowsAny<SocketException>(() => probe.Connect(IPAddress.Loopback, tunnel.BindPort));
        Assert.Equal(TunnelState.Failed, session.StartTunnel(tunnel).State);
    }

    private static async Task<string> RoundTripAsync(int port, string text)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
        var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes);
        var buffer = new byte[bytes.Length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary>Accepts connections and sends back whatever arrives.</summary>
    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public EchoServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(async () =>
                        {
                            using (client)
                            {
                                try { await client.GetStream().CopyToAsync(client.GetStream()); }
                                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                            }
                        });
                    }
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>A SOCKS5 proxy that needs no login and connects to loopback destinations.</summary>
    private sealed class Socks5Stub : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public Socks5Stub()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(() => ServeAsync(client));
                    }
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>The destination port of every CONNECT request.</summary>
        public List<int> Destinations { get; } = [];

        /// <summary>The SOCKS address type of every CONNECT request: 1 IPv4, 3 name, 4 IPv6.</summary>
        public List<int> AddressTypes { get; } = [];

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var header = new byte[2];
                    // A preflight probe connects and leaves without speaking.
                    if (await stream.ReadAtLeastAsync(header, 2, throwOnEndOfStream: false) < 2)
                        return;
                    await stream.ReadExactlyAsync(new byte[header[1]]);
                    await stream.WriteAsync(new byte[] { 5, 0 });

                    var request = new byte[4];
                    await stream.ReadExactlyAsync(request);
                    var address = new byte[request[3] switch { 1 => 4, 4 => 16, _ => stream.ReadByte() }];
                    await stream.ReadExactlyAsync(address);
                    var portBytes = new byte[2];
                    await stream.ReadExactlyAsync(portBytes);
                    var port = (portBytes[0] << 8) | portBytes[1];
                    lock (Destinations)
                    {
                        Destinations.Add(port);
                        AddressTypes.Add(request[3]);
                    }

                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(IPAddress.Loopback, port);
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                    await Task.WhenAny(
                        stream.CopyToAsync(upstream.GetStream()),
                        upstream.GetStream().CopyToAsync(stream));
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException) { }
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
