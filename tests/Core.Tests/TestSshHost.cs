using System.Net;
using System.Net.Sockets;
using System.Text;
using FxSsh;
using FxSsh.Services;

namespace Resesh.Core.Tests;

/// <summary>
/// A loopback SSH server for connection tests. It accepts one username and password,
/// answers every exec request with its own name, and carries direct-tcpip channels the
/// way a jump host does.
/// </summary>
internal sealed class TestSshHost : IDisposable
{
    private readonly SshServer _server;

    public TestSshHost(string name, string username = "test", string password = "test123", bool allowForwarding = true)
    {
        Name = name;
        Port = FreePort();
        _server = new SshServer(new StartingInfo(IPAddress.Loopback, Port, "SSH-2.0-" + name));
        _server.AddHostKey("rsa-sha2-256", new FxSsh.Algorithms.RsaKey(256, null).ExportKey());
        _server.ConnectionAccepted += (_, session) =>
        {
            Interlocked.Increment(ref _connections);
            session.ServiceRegistered += (_, service) =>
            {
                switch (service)
                {
                    case UserAuthService auth:
                        auth.UserAuth += (_, e) =>
                            e.Result = e.AuthMethod == "password" && e.Username == username && e.Password == password;
                        break;
                    case ConnectionService connection:
                        connection.CommandOpened += (_, e) =>
                        {
                            e.Agreed = true;
                            if (e.ShellType != "exec")
                                return;
                            e.Channel.SendData(Encoding.UTF8.GetBytes(name));
                            e.Channel.SendClose(0);
                        };
                        if (allowForwarding)
                            connection.TcpForwardRequest += (_, e) => Forward(e);
                        break;
                }
            };
        };
        _server.Start();
    }

    public string Name { get; }

    public int Port { get; }

    private int _connections;

    /// <summary>TCP connections accepted so far, including preflight probes.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Destinations asked for through direct-tcpip, as "host:port".</summary>
    public List<string> Forwarded { get; } = [];

    private void Forward(TcpRequestArgs request)
    {
        lock (Forwarded) Forwarded.Add($"{request.Host}:{request.Port}");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var channel = request.Channel;
        try
        {
            socket.Connect(request.Host, request.Port);
        }
        catch (SocketException)
        {
            // FxSsh always confirms the channel, so an unreachable destination is a
            // channel that closes at once rather than one that never opens.
            socket.Dispose();
            _ = Task.Run(() => channel.SendClose());
            return;
        }
        channel.DataReceived += (_, data) =>
        {
            try { socket.Send(data.Span); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        };
        channel.CloseReceived += (_, _) => socket.Dispose();
        _ = Task.Run(() =>
        {
            var buffer = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = socket.Receive(buffer)) > 0)
                    channel.SendData(buffer.AsSpan(0, read).ToArray());
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
            try { channel.SendClose(); } catch (Exception) { }
        });
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try { _server.Stop(); } catch (Exception) { }
        _server.Dispose();
    }
}
