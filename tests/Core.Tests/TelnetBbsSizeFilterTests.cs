using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Resesh.Core.Models;
using Resesh.Core.Telnet;

namespace Resesh.Core.Tests;

public sealed class TelnetBbsSizeFilterTests
{
    [Theory]
    [InlineData("\u001b[255B\u001b[255C")]
    [InlineData("\u001b[999B\u001b[999C")]
    [InlineData("\u001b[255;255H")]
    [InlineData("\u001b[999;999H")]
    public void SizeProbe_IsAnsweredOnceAtEveryReadBoundary(string move)
    {
        var bytes = Encoding.ASCII.GetBytes("hello\u001b[s" + move + "\u001b[6n\u001b[uworld");
        for (var chunk = 1; chunk <= bytes.Length; chunk++)
        {
            var filter = new TelnetBbsSizeFilter();
            var output = new List<byte>();
            var replies = new List<byte>();
            for (var offset = 0; offset < bytes.Length; offset += chunk)
                output.AddRange(filter.Process(bytes.AsSpan(offset, Math.Min(chunk, bytes.Length - offset)), replies));
            output.AddRange(filter.Process([], replies, flush: true));
            Assert.Equal("hello\u001b[s" + move + "\u001b[uworld", Encoding.ASCII.GetString([.. output]));
            Assert.Equal("\u001b[25;80R", Encoding.ASCII.GetString([.. replies]));
        }
    }

    [Theory]
    [InlineData("\u001b[6n")]
    [InlineData("\u001b[12;30H\u001b[6n")]
    [InlineData("\u001b[255Bhello\u001b[6n")]
    [InlineData("\u001b[999")]
    [InlineData("\u001b]title\u001b[255B\u001b[255C\u001b[6n\u0007")]
    [InlineData("\u001bP\u001b[18t\u001b\\")]
    public void OrdinaryOutputAndCursorQueries_AreUnchanged(string text)
    {
        var filter = new TelnetBbsSizeFilter();
        var output = new List<byte>();
        var replies = new List<byte>();
        foreach (var value in Encoding.ASCII.GetBytes(text))
            output.AddRange(filter.Process([value], replies));
        output.AddRange(filter.Process([], replies, flush: true));
        Assert.Equal(text, Encoding.ASCII.GetString([.. output]));
        Assert.Empty(replies);
    }

    [Fact]
    public void WindowSizeQuery_ReportsRowsThenColumns()
    {
        var replies = new List<byte>();
        Assert.Empty(new TelnetBbsSizeFilter().Process("\u001b[18t"u8, replies));
        Assert.Equal("\u001b[8;25;80t", Encoding.ASCII.GetString([.. replies]));
    }

    [Fact]
    public void Setting_DefaultsOffAndSurvivesSerializationAndCloning()
    {
        Assert.False(JsonSerializer.Deserialize<Session>("{}")!.TelnetReport80x25);
        var session = new Session { Kind = SessionKind.Telnet, TelnetReport80x25 = true };
        Assert.True(JsonSerializer.Deserialize<Session>(JsonSerializer.Serialize(session))!.TelnetReport80x25);
        Assert.True((session with { Name = "copy" }).TelnetReport80x25);
    }

    [Fact]
    public void FixedWidth_DefaultsOffAndSurvivesSerializationAndCloning()
    {
        Assert.False(JsonSerializer.Deserialize<Session>("{}")!.TelnetFixed80Columns);
        var session = new Session { Kind = SessionKind.Telnet, TelnetFixed80Columns = true };
        var restored = JsonSerializer.Deserialize<Session>(JsonSerializer.Serialize(session))!;
        Assert.True(restored.TelnetFixed80Columns);
        Assert.False(restored.TelnetReport80x25);
        Assert.True((session with { Name = "copy" }).TelnetFixed80Columns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connection_ReportsConfiguredSizeAfterResize(bool enabled)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var session = new TelnetTerminalSession();
        session.Connect("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "xterm", 160, 60, report80x25: enabled);
        using var peer = await listener.AcceptTcpClientAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = peer.GetStream();
        async Task<byte[]> Read(int length)
        {
            var result = new byte[length];
            await stream.ReadExactlyAsync(result, timeout.Token);
            return result;
        }
        await Read(15); // Initial Telnet negotiation.
        await stream.WriteAsync(new byte[] { 255, 253, 31 }, timeout.Token);
        byte[] Expected(int columns, int rows) => [255, 250, 31, 0, (byte)columns, 0, (byte)rows, 255, 240];
        Assert.Equal(Expected(enabled ? 80 : 160, enabled ? 25 : 60), await Read(9));
        session.Resize(200, 70);
        Assert.Equal(Expected(enabled ? 80 : 200, enabled ? 25 : 70), await Read(9));
        if (enabled)
        {
            await stream.WriteAsync("\u001b[s\u001b[255B\u001b[255C\u001b[6n\u001b[u"u8.ToArray(), timeout.Token);
            Assert.Equal("\u001b[25;80R"u8.ToArray(), await Read(8));
        }
    }
}
