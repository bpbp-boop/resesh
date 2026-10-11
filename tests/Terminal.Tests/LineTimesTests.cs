using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

/// <summary>Line timestamps (terminal.html's Phase 9.5).</summary>
public class LineTimesTests
{
    private static (FakeBuffer Buffer, LineTimes Times) Setup(int lines)
    {
        var buffer = new FakeBuffer();
        for (var i = 0; i < lines; i++)
            buffer.Add("line " + i);
        return (buffer, new LineTimes(buffer));
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
        Assert.Equal(2, buffer.LiveMarkerCount); // the carried run and surviving frontier
        Assert.All(buffer.Markers.Where(marker => marker.Origin < buffer.Trimmed),
            marker => Assert.True(marker.Disposed));
        times.Reset();
        Assert.Equal(0, buffer.LiveMarkerCount);
        Assert.All(buffer.Markers, marker => Assert.Equal(1, marker.ReleaseCount));
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
        for (var i = 0; i < LineTimes.MaxRuns + 5; i++)
        {
            buffer.Add("z");
            times.Note((i + 1) * 1000L);
        }
        Assert.Null(times.TimeOf(1));          // its run was dropped
        Assert.Equal((LineTimes.MaxRuns + 5) * 1000L, times.TimeOf(buffer.Length - 1));
        Assert.Equal(LineTimes.MaxRuns + 1, buffer.LiveMarkerCount); // capped runs plus frontier
        times.Reset();
        Assert.Equal(0, buffer.LiveMarkerCount);
        Assert.All(buffer.Markers, marker => Assert.Equal(1, marker.ReleaseCount));
    }

    [Fact]
    public void ReadingAfterAllStampedLinesAreTrimmedRetiresRunsAndFrontier()
    {
        var (buffer, times) = Setup(1);
        times.Note(1_000);
        Assert.Equal(2, buffer.LiveMarkerCount);
        buffer.Trimmed = 1;

        Assert.Null(times.TimeOf(0));

        Assert.Equal(0, buffer.LiveMarkerCount);
        times.Reset();
        Assert.All(buffer.Markers, marker => Assert.Equal(1, marker.ReleaseCount));
    }

    [Fact]
    public void NoteReplacesTrimmedRunsAndFrontierWithoutKeepingOldResources()
    {
        var (buffer, times) = Setup(1);
        times.Note(1_000);
        var retired = buffer.Markers.ToArray();
        buffer.Trimmed = 1;
        buffer.Add("fresh");

        times.Note(2_000);

        Assert.All(retired, marker => Assert.True(marker.Disposed));
        Assert.Equal(2, buffer.LiveMarkerCount);
        Assert.Equal(2_000, times.TimeOf(0));
        times.Reset();
        Assert.Equal(0, buffer.LiveMarkerCount);
        Assert.All(buffer.Markers, marker => Assert.Equal(1, marker.ReleaseCount));
    }

    [Fact]
    public void ExtendingTheSameRunKeepsOnlyOneFrontier()
    {
        var (buffer, times) = Setup(1);
        times.Note(1_100);
        for (var i = 0; i < 20; i++)
        {
            buffer.Add("more");
            times.Note(1_200 + i);
            Assert.Equal(2, buffer.LiveMarkerCount);
        }
        Assert.Equal(1_100, times.TimeOf(buffer.CursorLine));
        times.Reset();
        Assert.Equal(0, buffer.LiveMarkerCount);
        Assert.All(buffer.Markers, marker => Assert.Equal(1, marker.ReleaseCount));
    }
}
