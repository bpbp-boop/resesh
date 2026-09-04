using System.Text;
using Resesh.Core.Recording;

namespace Resesh.Core.Tests;

public sealed class TerminalRecordingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"resesh-recording-{Guid.NewGuid():N}");

    [Fact]
    public void AsciicastWriterUsesCurrentSizeAndSharedEventStream()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-10);
        using var capture = new TerminalCapture(80, 24, startedAt: started);
        capture.CaptureResize(132, 43, started.AddSeconds(1).ToUnixTimeMilliseconds());

        var path = capture.StartRecording(_directory, "router/lab", "xterm-256color");
        capture.CaptureOutput(Encoding.UTF8.GetBytes("hello \u001b[31mred\u001b[0m\r\n"), started.AddSeconds(2).ToUnixTimeMilliseconds());
        capture.CaptureResize(100, 30, started.AddSeconds(3).ToUnixTimeMilliseconds());
        capture.StopRecording();
        Assert.DoesNotContain("\"duration\"", File.ReadLines(path).First(), StringComparison.Ordinal);

        var recording = AsciicastReader.Read(path);
        Assert.Equal(2, recording.Events.Count);
        Assert.Equal((132, 43), (recording.Width, recording.Height));
        Assert.Equal("router/lab", recording.Title);
        Assert.Equal(new TerminalRecordingEvent(1, "o", "hello \u001b[31mred\u001b[0m\r\n"), recording.Events[0]);
        Assert.Equal(new TerminalRecordingEvent(2, "r", "100x30"), recording.Events[1]);
        Assert.EndsWith(".cast", path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.ChangeExtension(path, ".log")));
    }

    [Fact]
    public void RewindTrimKeepsAFullStateAnchor()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(
            80, 24, startedAt: started, maximumAge: TimeSpan.FromSeconds(5), maximumBytes: 1024 * 1024);
        capture.CaptureOutput(Encoding.UTF8.GetBytes("old"), started.AddSeconds(1).ToUnixTimeMilliseconds());
        capture.CaptureKeyframe(Encoding.UTF8.GetBytes("full-state"), 90, 25, started.AddSeconds(2).ToUnixTimeMilliseconds());
        capture.CaptureOutput(Encoding.UTF8.GetBytes("new"), started.AddSeconds(10).ToUnixTimeMilliseconds());

        var snapshot = capture.Snapshot();
        Assert.Equal(Encoding.UTF8.GetBytes("full-state"), snapshot.Keyframe?.State.ToArray());
        Assert.Equal(2, snapshot.EarliestTime);
        Assert.Single(snapshot.Events);
        Assert.Equal("new", snapshot.Events[0].Data);
    }

    [Fact]
    public void CompanionPlainTextLogStripsAnsiAndTimestampsContentLines()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(80, 24, startedAt: started);
        var castPath = capture.StartRecording(_directory, "plain");

        capture.CaptureOutput(Encoding.UTF8.GetBytes("before\u001b]0;secret"), started.AddMilliseconds(1).ToUnixTimeMilliseconds());
        capture.CaptureOutput(Encoding.UTF8.GetBytes(" title\aafter \u001b[3"), started.AddMilliseconds(2).ToUnixTimeMilliseconds());
        capture.CaptureOutput(Encoding.UTF8.GetBytes("1mred\u001b[0m\r\n\r\nnext\n"), started.AddMilliseconds(3).ToUnixTimeMilliseconds());
        capture.StopRecording();

        var logPath = Path.ChangeExtension(castPath, ".log");
        var lines = File.ReadAllLines(logPath);
        Assert.Equal(3, lines.Length);
        const string timestamp = @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}\] ";
        Assert.Matches(timestamp + "beforeafter red$", lines[0]);
        Assert.Empty(lines[1]);
        Assert.Matches(timestamp + "next$", lines[2]);
        Assert.True(File.Exists(castPath));
    }

    [Fact]
    public void PowerShellReadLineRedrawsProduceOneCommandLine()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(148, 80, startedAt: started);
        var castPath = capture.StartRecording(_directory, "powershell");
        var redraws =
            "\u001b[?25l\u001b[93me\u001b[?25h" +
            "\u001b[m\u001b[93m\bec" +
            "\u001b[m\u001b[?25l\u001b[93m\u001b[1;20Hech\u001b[?25h" +
            "\u001b[m\u001b[?25l\u001b[93m\u001b[1;20Hecho\u001b[?25h" +
            "\u001b[m\u001b[?25l\u001b[93m\u001b[1;20Hecho \u001b[?25h" +
            "\u001b[m\u001b[?25l\u001b[1;18H> \u001b[93mecho \u001b[36m\"hello everyone\"\u001b[?25h" +
            "\u001b[m\r\nhello everyone\r\nPS C:\\Users\\Boden> ";
        capture.CaptureOutput(
            Encoding.UTF8.GetBytes(redraws),
            started.AddSeconds(1).ToUnixTimeMilliseconds());
        capture.StopRecording();

        var lines = File.ReadAllLines(Path.ChangeExtension(castPath, ".log"));
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("> echo \"hello everyone\"", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("eecechecho", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("hello everyone", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("PS C:\\Users\\Boden>", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderRejectsUnorderedEvents()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "bad.cast");
        File.WriteAllText(path,
            "{\"version\":2,\"width\":80,\"height\":24}\n" +
            "[2,\"o\",\"later\"]\n" +
            "[1,\"o\",\"earlier\"]\n");

        var exception = Assert.Throws<InvalidDataException>(() => AsciicastReader.Read(path));
        Assert.Contains("ordered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisabledRewindRetainsNoEventsButStillRecordsToDisk()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(
            80, 24, startedAt: started, maximumBytes: 128, retainForRewind: false);
        var path = capture.StartRecording(_directory, "native");

        capture.CaptureOutput(
            Encoding.UTF8.GetBytes(new string('x', 4096)),
            started.AddSeconds(1).ToUnixTimeMilliseconds());
        capture.CaptureResize(100, 30, started.AddSeconds(2).ToUnixTimeMilliseconds());
        capture.StopRecording();

        Assert.Empty(capture.Snapshot().Events);
        var recording = AsciicastReader.Read(path);
        Assert.Equal(2, recording.Events.Count);
        Assert.Equal(4096, recording.Events[0].Data.Length);
        Assert.Equal("100x30", recording.Events[1].Data);
    }

    [Fact]
    public void RewindWithoutAKeyframeDisablesBeforeItExceedsTheByteLimit()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(
            80, 24, startedAt: started, maximumBytes: 128);

        capture.CaptureOutput(
            Encoding.UTF8.GetBytes(new string('x', 256)),
            started.AddSeconds(1).ToUnixTimeMilliseconds());
        capture.CaptureOutput(
            Encoding.UTF8.GetBytes("later"),
            started.AddSeconds(2).ToUnixTimeMilliseconds());

        var snapshot = capture.Snapshot();
        Assert.Null(snapshot.Keyframe);
        Assert.Empty(snapshot.Events);
    }

    [Fact]
    public void EqualTimestampsKeepEventsOnTheCorrectSideOfAKeyframe()
    {
        var started = DateTimeOffset.UtcNow;
        var time = started.AddSeconds(1).ToUnixTimeMilliseconds();
        using var capture = new TerminalCapture(80, 24, startedAt: started);

        capture.CaptureOutput(Encoding.UTF8.GetBytes("before"), time);
        capture.CaptureKeyframe(Encoding.UTF8.GetBytes("full-state"), 80, 24, time);
        capture.CaptureOutput(Encoding.UTF8.GetBytes("after"), time);
        capture.CaptureResize(100, 30, time);

        var snapshot = capture.Snapshot();
        Assert.Equal(Encoding.UTF8.GetBytes("full-state"), snapshot.Keyframe?.State.ToArray());
        Assert.Collection(
            snapshot.Events,
            item => Assert.Equal(new TerminalRecordingEvent(item.Time, "o", "after"), item),
            item => Assert.Equal(new TerminalRecordingEvent(item.Time, "r", "100x30"), item));
    }

    [Fact]
    public void OversizedKeyframeDisablesRewindWithoutOutputEvents()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(80, 24, startedAt: started, maximumBytes: 128);

        capture.CaptureKeyframe(new byte[256], 80, 24, started.ToUnixTimeMilliseconds());

        var snapshot = capture.Snapshot();
        Assert.Null(snapshot.Keyframe);
        Assert.Empty(snapshot.Events);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
