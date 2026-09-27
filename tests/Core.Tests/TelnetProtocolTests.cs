using System.Net;
using System.Net.Sockets;
using System.Text;
using Resesh.Core.Models;
using Resesh.Core.Storage;
using Resesh.Core.Telnet;
using static Resesh.Core.Telnet.TelnetProtocol;

namespace Resesh.Core.Tests;

public sealed class TelnetProtocolTests
{
    private static (byte[] Data, byte[] Replies) Feed(TelnetProtocol protocol, params byte[] input)
    {
        var data = new List<byte>();
        var replies = new List<byte>();
        protocol.Receive(input, data, replies);
        return ([.. data], [.. replies]);
    }

    [Fact]
    public void PlainText_PassesThrough_AndDoubledIacIsOneByte()
    {
        var protocol = new TelnetProtocol("xterm", 80, 24);
        var (data, replies) = Feed(protocol, [.. "ok"u8, Iac, Iac, (byte)'!']);
        Assert.Equal([(byte)'o', (byte)'k', 255, (byte)'!'], data);
        Assert.Empty(replies);
    }

    [Fact]
    public void ServerWillEcho_IsAccepted_AndUnknownOptionsRefused()
    {
        var protocol = new TelnetProtocol("xterm", 80, 24);
        var (data, replies) = Feed(protocol, Iac, Will, OptEcho, Iac, Will, 94, Iac, Do, 36);
        Assert.Empty(data);
        Assert.Equal([Iac, Do, OptEcho, Iac, Dont, 94, Iac, Wont, 36], replies);
        Assert.True(protocol.RemoteEcho);
    }

    [Fact]
    public void RepeatedRequests_ForAnEnabledOption_GetNoReply()
    {
        var protocol = new TelnetProtocol("xterm", 80, 24);
        Feed(protocol, Iac, Will, OptSga);
        var (_, replies) = Feed(protocol, Iac, Will, OptSga);
        Assert.Empty(replies); // acknowledging again is how negotiation loops start
    }

    [Fact]
    public void AnswersToOurOwnOffers_AreNotReacknowledged()
    {
        var protocol = new TelnetProtocol("xterm", 80, 24);
        protocol.InitialNegotiation();
        var (_, replies) = Feed(protocol, Iac, Do, OptTerminalType, Iac, Will, OptEcho, Iac, Dont, OptSga);
        Assert.Empty(replies);
        Assert.True(protocol.IsLocalEnabled(OptTerminalType));
        Assert.False(protocol.IsLocalEnabled(OptSga));
    }

    [Fact]
    public void DoNaws_SendsTheCurrentSize_AndResizeReportsAgain()
    {
        var protocol = new TelnetProtocol("xterm", 132, 43);
        Assert.Empty(protocol.Resize(100, 30)); // not yet enabled: remembered, not sent

        var (_, replies) = Feed(protocol, Iac, Do, OptNaws);
        Assert.Equal([Iac, Will, OptNaws, Iac, Sb, OptNaws, 0, 100, 0, 30, Iac, Se], replies);

        Assert.Equal([Iac, Sb, OptNaws, 1, 0, 0, 50, Iac, Se], protocol.Resize(256, 50));
    }

    [Fact]
    public void NawsSizeBytesEqualToIac_AreDoubled()
    {
        var protocol = new TelnetProtocol("xterm", 255, 24);
        var (_, replies) = Feed(protocol, Iac, Do, OptNaws);
        Assert.Equal([Iac, Will, OptNaws, Iac, Sb, OptNaws, 0, Iac, Iac, 0, 24, Iac, Se], replies);
    }

    [Fact]
    public void TerminalTypeSend_IsAnsweredWithTheConfiguredType()
    {
        var protocol = new TelnetProtocol("xterm-256color", 80, 24);
        Feed(protocol, Iac, Do, OptTerminalType);
        var (_, replies) = Feed(protocol, Iac, Sb, OptTerminalType, 1, Iac, Se);
        Assert.Equal([Iac, Sb, OptTerminalType, 0, .. "xterm-256color"u8, Iac, Se], replies);
    }

    [Fact]
    public void CommandsSplitAcrossReads_DecodeTheSame()
    {
        var protocol = new TelnetProtocol("vt100", 80, 24);
        byte[] stream = [(byte)'a', Iac, Do, OptTerminalType, Iac, Sb, OptTerminalType, 1, Iac, Se, (byte)'b'];
        var data = new List<byte>();
        var replies = new List<byte>();
        foreach (var b in stream)
            protocol.Receive([b], data, replies);
        Assert.Equal("ab"u8.ToArray(), data);
        Assert.Equal([Iac, Will, OptTerminalType, Iac, Sb, OptTerminalType, 0, .. "vt100"u8, Iac, Se], replies);
    }

    [Fact]
    public void CrNul_FromTheServer_IsABareCr()
    {
        var protocol = new TelnetProtocol("xterm", 80, 24);
        var (data, _) = Feed(protocol, (byte)'x', (byte)'\r', 0, (byte)'\r', (byte)'\n');
        Assert.Equal("x\r\r\n"u8.ToArray(), data);
    }

    [Fact]
    public void GoAheadAndNop_AreSwallowed()
    {
        var protocol = new TelnetProtocol("xterm", 80, 24);
        var (data, replies) = Feed(protocol, (byte)'>', Iac, 249, Iac, Nop);
        Assert.Equal(">"u8.ToArray(), data);
        Assert.Empty(replies);
    }

    [Fact]
    public void Input_EscapesIac_AndSendsEnterAsCrNul()
    {
        var protocol = new TelnetProtocol("xterm", 80, 24);
        Assert.Equal([(byte)'l', (byte)'s', (byte)'\r', 0], protocol.EncodeInput("ls\r"u8));
        Assert.Equal("a\r\nb"u8.ToArray(), protocol.EncodeInput("a\r\nb"u8));
        Assert.Equal([Iac, Iac], protocol.EncodeInput([Iac]));
    }

    [Fact]
    public void Input_InBinaryMode_SendsBareCr()
    {
        var protocol = new TelnetProtocol("xterm", 80, 24);
        Feed(protocol, Iac, Do, OptBinary);
        Assert.Equal("ls\r"u8.ToArray(), protocol.EncodeInput("ls\r"u8));
    }

    [Fact]
    public void Session_OverLoopback_NegotiatesAndExchangesData()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var received = new StringBuilder();
        var gotOutput = new ManualResetEventSlim();
        var closed = new ManualResetEventSlim();
        using var session = new TelnetTerminalSession();
        session.OutputReceived += data =>
        {
            lock (received) received.Append(Encoding.ASCII.GetString(data));
            gotOutput.Set();
        };
        session.Closed += _ => closed.Set();

        var accept = listener.AcceptSocketAsync();
        session.Connect("127.0.0.1", port, "xterm-256color", 120, 40);
        using var server = accept.GetAwaiter().GetResult();
        server.ReceiveTimeout = 5000;

        // The client's unprompted offers arrive first.
        var offers = ReadExactly(server, 15);
        Assert.Equal([Iac, Will, OptNaws, Iac, Will, OptTerminalType, Iac, Will, OptSga, Iac, Do, OptSga, Iac, Do, OptEcho], offers);

        server.Send([Iac, Do, OptNaws, Iac, Will, OptEcho, .. "login: "u8]);
        Assert.Equal([Iac, Sb, OptNaws, 0, 120, 0, 40, Iac, Se], ReadExactly(server, 9));
        Assert.True(gotOutput.Wait(5000));
        lock (received) Assert.Equal("login: ", received.ToString());

        session.Write("root\r"u8.ToArray());
        Assert.Equal("root\r\0"u8.ToArray(), ReadExactly(server, 6));

        session.Resize(80, 25);
        Assert.Equal([Iac, Sb, OptNaws, 0, 80, 0, 25, Iac, Se], ReadExactly(server, 9));

        server.Shutdown(SocketShutdown.Both);
        Assert.True(closed.Wait(5000));
        Assert.False(session.IsConnected);
    }

    [Fact]
    public void Connect_ToAClosedPort_ExplainsTheRefusal()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var session = new TelnetTerminalSession();
        var ex = Assert.Throws<TelnetSessionException>(() => session.Connect("127.0.0.1", port, "xterm", 80, 24));
        Assert.Contains("refused", ex.Message);
    }

    [Fact]
    public void TelnetSessions_ShareTheSshFolderNamespace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"resesh-telnet-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SessionStore(path);
            store.Add(new Session { Kind = SessionKind.Telnet, Name = "console", Host = "ts1", Port = 2003, FolderPath = "Lab" });
            store.Add(new Session { Kind = SessionKind.Ssh, Name = "web", Host = "web", FolderPath = "Lab" });
            store.RenameFolder("Lab", "Bench");
            Assert.All(store.Sessions, s => Assert.Equal("Bench", s.FolderPath));

            var reloaded = new SessionStore(path);
            reloaded.Load();
            var telnet = reloaded.Sessions.Single(s => s.IsTelnet);
            Assert.Equal(2003, telnet.Port);
            Assert.Equal(SessionKind.Ssh, telnet.FolderScope);
            Assert.False(SessionCapabilities.For(telnet).FilePane);
            Assert.False(SessionCapabilities.For(telnet).HostKeys);
            Assert.True(SessionCapabilities.For(telnet).SendBreak);
            Assert.False(SessionCapabilities.For(new Session { Kind = SessionKind.Ssh }).SendBreak);
            Assert.False(SessionCapabilities.For(new Session { Kind = SessionKind.Local }).SendBreak);

            Assert.Equal(2, reloaded.DeleteFolder("Bench").Count);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    private static byte[] ReadExactly(Socket socket, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = socket.Receive(buffer, read, count - read, SocketFlags.None);
            if (n == 0) break;
            read += n;
        }
        return buffer[..read];
    }
}
