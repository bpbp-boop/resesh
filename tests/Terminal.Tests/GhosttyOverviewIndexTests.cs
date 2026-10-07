using Resesh.Terminal.Ghostty;

namespace Resesh.Terminal.Tests;

/// <summary>The ruler's highlight lane index (terminal.html's Phase 9.3).</summary>
public class GhosttyOverviewIndexTests
{
    private static (FakeBuffer Buffer, GhosttyOverviewIndex Index) Setup(int lines, Func<int, string> text, int rows = 10)
    {
        var buffer = new FakeBuffer { Rows = rows };
        for (var i = 0; i < lines; i++)
            buffer.Add(text(i));
        var highlighter = new GhosttyHighlighter();
        highlighter.SetRules([
            new { id = "err", pattern = "error", color = "#ff5555", bold = false, underline = false, matchCase = false, showInOverview = true },
            new { id = "warn", pattern = "warn", color = "#f1fa8c", bold = false, underline = false, matchCase = false, showInOverview = false },
        ]);
        return (buffer, new GhosttyOverviewIndex(buffer, highlighter));
    }

    private static int[] Indexed(GhosttyOverviewIndex index) => [.. index.Lines().Select(l => l.Line).OrderBy(l => l)];

    [Fact]
    public void IndexesOnlyOverviewRules()
    {
        var (_, index) = Setup(30, i => i switch { 3 => "error here", 5 => "warn here", 25 => "ERROR live", _ => "ok" });
        while (index.Advance()) { }
        Assert.Equal([3, 25], Indexed(index));
    }

    [Fact]
    public void ScansLargeScrollbackInSlices()
    {
        var (_, index) = Setup(5000, i => i % 1000 == 7 ? "error" : "fine");
        Assert.True(index.Advance());        // first 2000 lines only
        Assert.Equal([7, 1007], Indexed(index));
        var calls = 2;                       // the one above, and the last that returns false
        while (index.Advance())
            calls++;
        Assert.Equal(3, calls);              // 4990 scrollback lines: 2000 + 2000 + 990
        Assert.Equal([7, 1007, 2007, 3007, 4007], Indexed(index));
    }

    [Fact]
    public void SurvivesTrimmingFromTheTop()
    {
        var (buffer, index) = Setup(100, i => i == 50 ? "error" : "fine");
        index.Advance();
        buffer.Trimmed = 20;                 // the top 20 lines left the scrollback
        Assert.Empty(Indexed(index));        // the anchor is gone: the index answers nothing
        index.Advance();                     // ... and rebuilds against the new top
        Assert.Equal([30], Indexed(index));
    }

    [Fact]
    public void FollowsLinesWhileTheAnchorLives()
    {
        var (buffer, index) = Setup(6000, i => i == 5500 ? "error" : "fine");
        while (index.Advance()) { }
        for (var i = 0; i < 200; i++)
            buffer.Add("fine");
        index.Advance();                     // moves the anchor near the new output
        buffer.Trimmed = 1000;
        index.Advance();
        Assert.Equal([4500], Indexed(index));
    }

    [Fact]
    public void JoinsSoftWrappedLines()
    {
        var (buffer, index) = Setup(0, _ => "");
        buffer.Add("connection err");
        buffer.Add("or at port 22", wrapped: true);
        for (var i = 0; i < 20; i++)
            buffer.Add("fine");
        index.Advance();
        Assert.Equal([0], Indexed(index));
    }

    [Fact]
    public void AlternateScreenIsNotIndexed()
    {
        var (buffer, index) = Setup(30, i => i == 3 ? "error" : "fine");
        buffer.IsAlternate = true;
        Assert.False(index.Advance());
        Assert.Empty(Indexed(index));
    }
}
