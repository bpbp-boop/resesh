using Resesh.Terminal.Ghostty;

namespace Resesh.Terminal.Tests;

/// <summary>Line timestamps (terminal.html's Phase 9.5).</summary>
public class GhosttyLineTimesTests
{
    private static (FakeBuffer Buffer, GhosttyLineTimes Times) Setup(int lines)
    {
        var buffer = new FakeBuffer();
        for (var i = 0; i < lines; i++)
            buffer.Add("line " + i);
        return (buffer, new GhosttyLineTimes(buffer));
    }

    [Fact]
    public void StampsLinesReachedSinceTheLastNote()
    {
        var (buffer, times) = Setup(3);
        times.Note(10_000);                    // first note: only the cursor line is known
        Assert.Null(times.TimeOf(0));
        Assert.Equal(10_000, times.TimeOf(2));
        buffer.Add("a");
        buffer.Add("b");
        times.Note(13_000);
        Assert.Equal(13_000, times.TimeOf(3));
        Assert.Equal(13_000, times.TimeOf(4));
        Assert.Equal(10_000, times.TimeOf(2)); // a line keeps its first time
        Assert.Null(times.TimeOf(5));          // not written yet
    }

    [Fact]
    public void SameSecondExtendsTheRun()
    {
        var (buffer, times) = Setup(1);
        times.Note(20_100);
        buffer.Add("x");
        times.Note(20_900);
        Assert.Equal(20_100, times.TimeOf(1));
    }

    [Fact]
    public void SurvivesTrimmingFromTheTop()
    {
        var (buffer, times) = Setup(1);
        times.Note(1_000);
        for (var i = 0; i < 5; i++)
            buffer.Add("y");
        times.Note(5_000);                     // lines 1..5
        buffer.Trimmed = 3;                    // the run for 5_000 began at old line 1
        Assert.Equal(5_000, times.TimeOf(0));  // old line 3
        Assert.Equal(5_000, times.TimeOf(2));
    }

    [Fact]
    public void AlternateScreenIsNotStamped()
    {
        var (buffer, times) = Setup(2);
        buffer.IsAlternate = true;
        times.Note(1_000);
        Assert.Null(times.TimeOf(1));
    }

    [Fact]
    public void DropsTheOldestRunsBeyondTheCap()
    {
        var (buffer, times) = Setup(1);
        for (var i = 0; i < GhosttyLineTimes.MaxRuns + 5; i++)
        {
            buffer.Add("z");
            times.Note((i + 1) * 1000L);
        }
        Assert.Null(times.TimeOf(1));          // its run was dropped
        Assert.Equal((GhosttyLineTimes.MaxRuns + 5) * 1000L, times.TimeOf(buffer.Length - 1));
    }
}
