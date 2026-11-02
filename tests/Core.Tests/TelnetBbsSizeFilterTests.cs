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
    [InlineData("\u001b]title [6n 18t\u0007\u001b[6n")]
    [InlineData("\u001bPdata\u001b\\\u001b[6n")]
    [InlineData("\u001b]8;;http://x\u001b\\link\u001b]8;;\u001b\\")]
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

    [Theory]
    [InlineData("\u001b]title\u001b[18t", "\u001b]title")] // ESC that is not ST ends the string
    [InlineData("\u001bPdata\u0018\u001b[18t", "\u001bPdata\u0018")] // CAN aborts it
    [InlineData("\u001b_apc\u001a\u001b[18t", "\u001b_apc\u001a")] // and so does SUB
    public void AnAbortedString_DoesNotHideTheNextProbe(string text, string expected)
    {
        foreach (var chunk in new[] { 1, text.Length })
        {
            var filter = new TelnetBbsSizeFilter();
            var output = new List<byte>();
            var replies = new List<byte>();
            var bytes = Encoding.ASCII.GetBytes(text);
            for (var offset = 0; offset < bytes.Length; offset += chunk)
                output.AddRange(filter.Process(bytes.AsSpan(offset, Math.Min(chunk, bytes.Length - offset)), replies));
            Assert.Equal(expected, Encoding.ASCII.GetString([.. output]));
            Assert.Equal("\u001b[8;25;80t", Encoding.ASCII.GetString([.. replies]));
        }
    }

    [Theory]
    [InlineData("\u001b")]
    [InlineData("menu\u001b[999B")]
    [InlineData("\u001b[255;255H")]
    public void HeldOutput_IsReleasedByAFlush(string text)
    {
        var filter = new TelnetBbsSizeFilter();
        var output = new List<byte>(filter.Process(Encoding.ASCII.GetBytes(text), []));
        Assert.True(filter.HasHeldOutput);
        output.AddRange(filter.FlushHeld());
        Assert.False(filter.HasHeldOutput);
        Assert.Equal(text, Encoding.ASCII.GetString([.. output]));
    }

    [Fact]
    public void AFlushedEscape_CanStillOpenAString()
    {
        var filter = new TelnetBbsSizeFilter();
        var replies = new List<byte>();
        var output = new List<byte>(filter.Process("\u001b"u8, replies));
        output.AddRange(filter.FlushHeld());
        output.AddRange(filter.Process("]x\u001b[18t\u0007"u8, replies));
        // The ESC inside the string ends it, so the query is answered, not shown.
        Assert.Equal("\u001b]x\u0007", Encoding.ASCII.GetString([.. output]));
        Assert.Equal("\u001b[8;25;80t", Encoding.ASCII.GetString([.. replies]));
    }

    [Fact]
    public async Task Connection_ReleasesAHeldCursorMoveWhenTheServerGoesQuiet()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var session = new TelnetTerminalSession();
        var received = new List<byte>();
        var done = new TaskCompletionSource();
        session.OutputReceived += data =>
        {
            lock (received)
            {
                received.AddRange(data.ToArray());
                if (Encoding.ASCII.GetString([.. received]).EndsWith("\u001b[999B"))
                    done.TrySetResult();
            }
        };
        session.Connect("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "xterm", 80, 25, report80x25: true);
        using var peer = await listener.AcceptTcpClientAsync();
        await peer.GetStream().WriteAsync("Press a key\u001b[999B"u8.ToArray());
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
