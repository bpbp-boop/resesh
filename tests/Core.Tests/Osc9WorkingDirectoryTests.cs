using Resesh.Core.Agents;
using Resesh.Core.Local;

namespace Resesh.Core.Tests;

public sealed class Osc9WorkingDirectoryTests
{
    [Theory]
    [InlineData("9;\"C:\\My Files\"", "C:\\My Files")]
    [InlineData("9;C:\\", "C:\\")]
    [InlineData("9;D:/work/日本語", "D:/work/日本語")]
    [InlineData("9;\\\\server\\share\\work", "\\\\server\\share\\work")]
    [InlineData("9;C:\\semi;colon", "C:\\semi;colon")]
    public void Accepts_absolute_windows_paths(string payload, string expected)
    {
        Assert.True(Osc9WorkingDirectory.TryParse(payload, out var directory));
        Assert.Equal(expected, directory);
        Assert.Null(AgentOsc.ParseNotify(payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9")]
    [InlineData("9;")]
    [InlineData("9;C:relative")]
    [InlineData("9;/home/user")]
    [InlineData("file:///C:/work")]
    [InlineData("9;\\rooted")]
    [InlineData("9;\"C:\\unclosed")]
    [InlineData("9;C:\\bad\u001bpath")]
    [InlineData("9;C:\\bad\u0085path")]
    [InlineData("9;C:\\file:stream")]
    [InlineData("9;\\\\server")]
    [InlineData("9;\\\\.\\pipe\\name")]
    [InlineData("9;\\\\?\\C:\\work")]
    public void Rejects_invalid_paths(string? payload) =>
        Assert.False(Osc9WorkingDirectory.TryParse(payload, out _));

    [Fact]
    public void Oversized_report_is_rejected_and_never_attention()
    {
        var payload = "9;C:\\" + new string('a', Osc9WorkingDirectory.MaxPayloadLength);
        Assert.False(Osc9WorkingDirectory.TryParse(payload, out _));
        Assert.Null(AgentOsc.ParseNotify(payload));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("9;")]
    [InlineData("9;invalid")]
    [InlineData("9;\u001bmalformed")]
    public void Malformed_directory_reports_are_never_notifications(string payload) =>
        Assert.Null(AgentOsc.ParseNotify(payload));
}
