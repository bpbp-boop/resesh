using Resesh.Core.Backend;

namespace Resesh.Core.Tests;

public sealed class CommandCompletionTrackerTests
{
    private sealed class Clock : TimeProvider
    {
        public long Timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
    }

    [Fact]
    public void ExplicitArm_ReportsDurationStatusAndProgramOnly_Once()
    {
        var clock = new Clock(); var tracker = new CommandCompletionTracker(clock);
        tracker.Start(1, "python3 secret-script.py --token private"); tracker.Arm();
        clock.Timestamp = 2500;
        Assert.Null(tracker.Complete(2, 0));
        var result = Assert.IsType<CommandCompletion>(tracker.Complete(1, 1));
        Assert.Equal("python3", result.ProgramName);
        Assert.Equal(TimeSpan.FromSeconds(2.5), result.Duration);
        Assert.Equal(1, result.ExitCode);
        Assert.False(tracker.IsArmed);
        Assert.Null(tracker.Complete(1, 0));
    }

    [Fact]
    public void NewExecutionAndResetCancelArmedRequest()
    {
        var tracker = new CommandCompletionTracker();
        tracker.Arm(); Assert.False(tracker.IsArmed);
        tracker.Start(1, "sleep 5"); tracker.Arm(); tracker.Start(1, "sleep 5");
        Assert.True(tracker.IsArmed);
        tracker.Start(2, "true"); Assert.False(tracker.IsArmed);
        Assert.Null(tracker.Complete(1, 0)); Assert.True(tracker.IsRunning);
        Assert.Null(tracker.Complete(2, 0));
        tracker.Start(3, "false"); tracker.Toggle(); tracker.Reset();
        Assert.Null(tracker.Complete(3, 1));
    }

    [Fact]
    public void ComplexCommandCanBeArmedWithoutExposingItsText()
    {
        var tracker = new CommandCompletionTracker();
        tracker.Start(1, "(sleep 5; echo private)");
        tracker.Arm();
        var result = Assert.IsType<CommandCompletion>(tracker.Complete(1, 0));
        Assert.Equal("Command", result.ProgramName);
        tracker.Start(2, "  ");
        tracker.Arm();
        Assert.False(tracker.IsRunning);
        Assert.False(tracker.IsArmed);
    }

    [Fact]
    public void DisarmAndUnknownStatusAreNotSuccess()
    {
        var tracker = new CommandCompletionTracker();
        tracker.Start(1, "true"); tracker.Arm(); tracker.Disarm();
        Assert.Null(tracker.Complete(1, 0));
        tracker.Start(2, "sleep 5"); tracker.Toggle();
        Assert.Null(Assert.IsType<CommandCompletion>(tracker.Complete(2, null)).ExitCode);
    }
}
