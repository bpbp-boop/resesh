using Resesh.Core.Recording;

namespace Resesh.Core.Tests;

public sealed class TerminalCaptureAvailabilityTests
{
    [Fact]
    public void AvailabilityTracksOutputKeyframesAndRetentionFailure()
    {
        using var capture = new TerminalCapture(80, 24, maximumBytes: 1024);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.False(capture.HasRewindData);
        capture.CaptureOutput("output"u8, now);
        Assert.True(capture.HasRewindData);
        capture.CaptureKeyframe(new byte[2048], 80, 24, now + 1);
        Assert.False(capture.HasRewindData);
        capture.CaptureOutput("more"u8, now + 2);
        Assert.False(capture.HasRewindData);
    }

    [Fact]
    public void AvailabilityChecksDoNotAllocateReplayArrays()
    {
        using var capture = new TerminalCapture(80, 24);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var index = 0; index < 10000; index++)
            capture.CaptureOutput("line\r\n"u8, now + index);
        _ = capture.HasRewindData;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            if (!capture.HasRewindData)
                throw new InvalidOperationException("Rewind data was lost.");
        }
        Assert.Equal(allocated, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void KeyframeAloneIsAvailableButDisabledCaptureIsNot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var capture = new TerminalCapture(80, 24);
        capture.CaptureKeyframe(new byte[] { 1 }, 80, 24, now);
        Assert.True(capture.HasRewindData);
        using var disabled = new TerminalCapture(80, 24, retainForRewind: false);
        disabled.CaptureOutput("line"u8, now);
        disabled.CaptureKeyframe(new byte[] { 1 }, 80, 24, now);
        Assert.False(disabled.HasRewindData);
    }
}
