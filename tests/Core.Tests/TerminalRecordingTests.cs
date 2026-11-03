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

        var recordingStart = DateTimeOffset.UtcNow;
        var path = capture.StartRecording(_directory, "router/lab", "xterm-256color");
        capture.CaptureOutput(Encoding.UTF8.GetBytes("hello \u001b[31mred\u001b[0m\r\n"), recordingStart.AddSeconds(2).ToUnixTimeMilliseconds());
        capture.CaptureResize(100, 30, recordingStart.AddSeconds(3).ToUnixTimeMilliseconds());
        capture.StopRecording();
        Assert.DoesNotContain("\"duration\"", File.ReadLines(path).First(), StringComparison.Ordinal);

        var recording = AsciicastReader.Read(path);
        Assert.Equal(2, recording.Events.Count);
        Assert.Equal((132, 43), (recording.Width, recording.Height));
        Assert.Equal("router/lab", recording.Title);
        Assert.Equal("o", recording.Events[0].Type);
        Assert.Equal("hello \u001b[31mred\u001b[0m\r\n", recording.Events[0].Data);
        Assert.InRange(recording.Events[0].Time, 0, 2);
        Assert.Equal("r", recording.Events[1].Type);
        Assert.Equal("100x30", recording.Events[1].Data);
        Assert.Equal(1, recording.Events[1].Time - recording.Events[0].Time, precision: 6);
        Assert.EndsWith(".cast", path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.ChangeExtension(path, ".log")));
    }

    [Fact]
    public void CastLinesMatchTheSerializerAndReachDiskWhileRecording()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(80, 24, startedAt: started);
        var path = capture.StartRecording(_directory, "flush");
        const string text = "tab\t quote\" slash\\ \u001b[1m é 😀 <&>\r\n";
        capture.CaptureOutput(Encoding.UTF8.GetBytes(text), started.AddSeconds(1).ToUnixTimeMilliseconds());

        // The tail is flushed by a timer within 250 ms, while the recording is still open.
        string[] lines = [];
        for (var attempt = 0; attempt < 100 && lines.Length < 2; attempt++)
        {
            Thread.Sleep(20);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            lines = new StreamReader(stream).ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        }
        Assert.Equal(2, lines.Length);
        var options = new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var time = System.Text.Json.JsonDocument.Parse(lines[1]).RootElement[0].GetDouble();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(new object[] { time, "o", text }, options), lines[1]);
        capture.StopRecording();
    }

    [Fact]
    public void RecordingFolderExpandsEnvironmentVariablesForWritersAndReaders()
    {
        const string variable = "RESESH_RECORDING_TEST_ROOT";
        Environment.SetEnvironmentVariable(variable, _directory);
        try
        {
            var setting = $"%{variable}%" + Path.DirectorySeparatorChar + "casts";
            var expected = Path.Combine(_directory, "casts");
            Assert.Equal(expected, TerminalRecordingFiles.ResolveDirectory(setting));

            using var capture = new TerminalCapture(80, 24);
            var path = capture.StartRecording(setting, "lab");
            capture.StopRecording();
            Assert.Equal(expected, Path.GetDirectoryName(path));
            Assert.False(Directory.Exists(Path.GetFullPath(setting))); // no literal "%VAR%" folder
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
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
    public void KeyframeIsCopiedBecauseTheCallersBufferIsFreedAfterwards()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(80, 24, startedAt: started);
        var buffer = Encoding.UTF8.GetBytes("full-state");

        capture.CaptureKeyframe(buffer, 80, 24, started.AddSeconds(1).ToUnixTimeMilliseconds());
        Array.Fill(buffer, (byte)'x'); // the terminal surface hands over native memory it then frees

        Assert.Equal(Encoding.UTF8.GetBytes("full-state"), capture.Snapshot().Keyframe?.State.ToArray());
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

    /// <summary>A file that accepts <paramref name="capacity"/> bytes, then fails like a full disk.</summary>
    private sealed class FullDiskStream(int capacity) : MemoryStream
    {
        public bool Full { get; set; }
        public bool FlushFails { get; set; }

        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Full || Length + count > capacity)
                throw new IOException("There is not enough space on the disk.");
            base.Write(buffer, offset, count);
        }

        public override void Flush()
        {
            if (Full || FlushFails)
                throw new IOException("There is not enough space on the disk.");
        }
    }

    [Fact]
    public void FullDiskStopsTheRecordingButNotTheTerminal()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(80, 24, startedAt: started);
        capture.OpenRecordingFile = _ => new FullDiskStream(4096);
        var changes = new List<bool>();
        var failures = new List<(string Path, string Message)>();
        capture.RecordingChanged += (recording, _) => changes.Add(recording);
        capture.RecordingFailed += (path, message) => failures.Add((path, message));
        var path = capture.StartRecording(_directory, "lab");

        var chunk = Encoding.UTF8.GetBytes(new string('x', 1000) + "\r\n");
        for (var index = 0; index < 10; index++)
            capture.CaptureOutput(chunk, started.AddMilliseconds(index).ToUnixTimeMilliseconds());

        Assert.False(capture.IsRecording);
        Assert.Null(capture.RecordingPath);
        Assert.Contains("not enough space", capture.RecordingError);
        var failure = Assert.Single(failures);
        Assert.Equal(path, failure.Path);
        Assert.Contains("not enough space", failure.Message);
        Assert.Equal([true, false], changes);
        // Rewind kept every chunk, including those after the failure.
        Assert.Equal(10, capture.Snapshot().Events.Count);
        Assert.Null(capture.StopRecording());
    }

    [Fact]
    public void FailedBackgroundFlushStopsTheRecording()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(80, 24, startedAt: started);
        var streams = new List<FullDiskStream>();
        capture.OpenRecordingFile = _ =>
        {
            var stream = new FullDiskStream(1 << 20);
            streams.Add(stream);
            return stream;
        };
        using var failed = new ManualResetEventSlim();
        capture.RecordingFailed += (_, _) => failed.Set();
        capture.StartRecording(_directory, "lab");

        // Writes still land but flushing fails. Output then stops, so only the flush timer is
        // left to notice (or the write itself, if this machine took over 250 ms to get here).
        foreach (var stream in streams)
            stream.FlushFails = true;
        capture.CaptureOutput("hello\r\n"u8, started.ToUnixTimeMilliseconds());

        Assert.True(failed.Wait(TimeSpan.FromSeconds(5)), "The failed flush was never reported.");
        Assert.False(capture.IsRecording);
        Assert.NotNull(capture.RecordingError);
        capture.CaptureOutput("more\r\n"u8, started.AddSeconds(1).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void NewRecordingClearsTheLastFailure()
    {
        using var capture = new TerminalCapture(80, 24);
        capture.OpenRecordingFile = _ => new FullDiskStream(512);
        capture.StartRecording(_directory, "lab");
        capture.CaptureOutput(Encoding.UTF8.GetBytes(new string('x', 2000)), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.NotNull(capture.RecordingError);

        capture.OpenRecordingFile = null;
        capture.StartRecording(_directory, "lab");
        Assert.Null(capture.RecordingError);
        Assert.True(capture.IsRecording);
        capture.StopRecording();
    }

    [Fact]
    public void DisposingAfterTheDiskFillsDoesNotThrow()
    {
        var capture = new TerminalCapture(80, 24);
        var streams = new List<FullDiskStream>();
        capture.OpenRecordingFile = _ =>
        {
            var stream = new FullDiskStream(1 << 20);
            streams.Add(stream);
            return stream;
        };
        capture.StartRecording(_directory, "lab");
        capture.CaptureOutput("pending\r\n"u8, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var stream in streams)
            stream.Full = true;

        capture.Dispose();
    }

    [Fact]
    public void PlainTextLogKeepsRowsAndLinesBounded()
    {
        var renderer = new AnsiPlainTextRenderer();
        var start = DateTimeOffset.UtcNow;
        var hostile = new StringBuilder();
        // Text on 5000 different rows, none ever ended by a newline...
        for (var row = 1; row <= 5000; row++)
            hostile.Append(System.Globalization.CultureInfo.InvariantCulture, $"\u001b[{row};100Hx");
        // ...and a line padded far past any real screen.
        hostile.Append("\u001b[1;1H").Append('y', 100_000);

        var written = renderer.Feed(hostile.ToString(), start);
        var flushed = renderer.Flush();

        // The excess rows are written out early instead of held, and nothing is lost but the
        // columns past the cap.
        Assert.NotEmpty(written);
        Assert.InRange(flushed.Count, 1, 1024);
        Assert.All(written.Concat(flushed), line => Assert.True(line.Text.Length <= 8192));
    }

    [Fact]
    public void PlainTextLogSurvivesEndlessLineFeedsAndTabs()
    {
        var renderer = new AnsiPlainTextRenderer();
        var start = DateTimeOffset.UtcNow;
        renderer.Feed(new string('\n', 70_000), start);
        var lines = renderer.Feed("\u001b[1;65535H\t\t\tend\n", start);

        Assert.Equal("", Assert.Single(lines).Text);
        Assert.Empty(renderer.Flush());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
