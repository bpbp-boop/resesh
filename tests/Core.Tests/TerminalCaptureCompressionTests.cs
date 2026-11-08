using System.Text;
using Resesh.Core.Recording;

namespace Resesh.Core.Tests;

public sealed class TerminalCaptureCompressionTests
{
    // Colored, repetitive output like a build log or `ls -l`: what rewind holds in practice.
    private static byte[] Line(int index) => Encoding.UTF8.GetBytes(
        $"\u001b[32m{index:D6}\u001b[0m -rw-r--r-- 1 user staff  {index * 37 % 100000,6} Nov  8 13:33 file-{index}.txt\r\n");

    [Fact]
    public void ReplayMatchesTheOutputAcrossCompressedBlocksAndKeyframes()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(80, 24, startedAt: started, maximumBytes: 256L << 20)
        {
            CompressInline = true,
        };
        var sent = new List<(double Time, string Data)>();
        var frames = new List<(double Time, byte[] State)>();
        for (var index = 0; index < 20_000; index++)
        {
            var time = started.AddMilliseconds(index * 2).ToUnixTimeMilliseconds();
            var line = Line(index);
            capture.CaptureOutput(line, time);
            sent.Add((capture.Snapshot().LatestTime, Encoding.UTF8.GetString(line)));
            if (index % 5000 == 4999)
            {
                var state = Encoding.UTF8.GetBytes($"screen at {index}" + new string('=', 4000));
                capture.CaptureKeyframe(state, 80, 24, time + 1);
                frames.Add((capture.Snapshot().LatestTime, state));
            }
        }
        capture.CaptureResize(100, 30, started.AddSeconds(60).ToUnixTimeMilliseconds());
        sent.Add((capture.Snapshot().LatestTime, "100x30"));

        foreach (var at in new[] { 1.0, 9.999, 10.5, 25.0, 39.0, 61.0 })
        {
            var snapshot = capture.Snapshot(at);
            var frame = frames.LastOrDefault(item => item.Time <= snapshot.LatestTime && item.Time <= at);
            var after = frame.State is null ? double.NegativeInfinity : frame.Time;
            Assert.Equal(frame.State, snapshot.Keyframe?.State.ToArray());
            var expected = sent.Where(item => item.Time > after && item.Time <= at).Select(item => item.Data);
            Assert.Equal(expected, snapshot.Events.Select(item => item.Data));
        }
        Assert.Equal("r", capture.Snapshot().Events[^1].Type);
    }

    [Fact]
    public void CompressedRewindKeepsMoreHistoryUnderTheSameCap()
    {
        var started = DateTimeOffset.UtcNow;
        const long cap = 1L << 20;
        using var capture = new TerminalCapture(80, 24, startedAt: started, maximumBytes: cap)
        {
            CompressInline = true,
        };
        long sent = 0;
        // Four times the cap in raw output; a keyframe now and then lets old output be trimmed.
        for (var index = 0; sent < 4 * cap; index++)
        {
            var line = Line(index);
            sent += line.Length;
            capture.CaptureOutput(line, started.AddMilliseconds(index).ToUnixTimeMilliseconds());
            if (index % 2000 == 1999)
                capture.CaptureKeyframe(new byte[] { 1 }, 80, 24, started.AddMilliseconds(index).ToUnixTimeMilliseconds());
        }

        Assert.True(capture.HasRewindData);
        Assert.InRange(capture.RetainedBytes, 1, cap);
        // Nothing had to be trimmed: the earliest output is still there.
        Assert.Equal(0, capture.Snapshot().EarliestTime, precision: 3);
    }

    [Fact]
    public void BackgroundCompressionShrinksWhatIsRetained()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(80, 24, startedAt: started);
        for (var index = 0; index < 5000; index++)
            capture.CaptureOutput(Line(index), started.AddMilliseconds(index).ToUnixTimeMilliseconds());
        var keyframe = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(index => $"row {index % 24}\r\n")));
        capture.CaptureKeyframe(keyframe, 80, 24, started.AddSeconds(10).ToUnixTimeMilliseconds());
        var raw = capture.RetainedBytes;

        // Generous: other tests can keep the thread pool busy for a while.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (capture.RetainedBytes > raw / 2 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        Assert.True(capture.RetainedBytes <= raw / 2, $"Still {capture.RetainedBytes} of {raw} bytes.");
        Assert.Equal(keyframe, capture.Snapshot().Keyframe?.State.ToArray());
        Assert.Equal(5000, capture.Snapshot(9).Events.Count);
    }

    [Fact]
    public void TrimmingDropsCompressedBlocksOlderThanTheAgeCap()
    {
        var started = DateTimeOffset.UtcNow;
        using var capture = new TerminalCapture(
            80, 24, startedAt: started, maximumAge: TimeSpan.FromSeconds(30))
        {
            CompressInline = true,
        };
        for (var second = 0; second < 120; second++)
        {
            for (var line = 0; line < 200; line++)
                capture.CaptureOutput(Line(second * 200 + line), started.AddSeconds(second).AddMilliseconds(line).ToUnixTimeMilliseconds());
            capture.CaptureKeyframe(Encoding.UTF8.GetBytes($"second {second}"), 80, 24,
                started.AddSeconds(second).AddMilliseconds(500).ToUnixTimeMilliseconds());
        }

        var snapshot = capture.Snapshot(0);
        Assert.InRange(snapshot.EarliestTime, 88, 91);
        Assert.NotNull(snapshot.Keyframe);
        Assert.Empty(snapshot.Events);
        var later = capture.Snapshot(100.2);
        Assert.Equal("second 99", Encoding.UTF8.GetString(later.Keyframe!.State.Span));
        Assert.Equal(Encoding.UTF8.GetString(Line(100 * 200)), later.Events[0].Data);
    }
}
