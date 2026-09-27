using Resesh.Core.History;

namespace Resesh.Core.Tests;

public sealed class OutputDiffTests
{
    private static string Lines(params string[] lines) => string.Join('\n', lines);

    private static string Render(OutputComparison comparison) =>
        string.Join('\n', comparison.Lines.Select(line => line.Kind switch
        {
            DiffKind.Added => "+" + line.Text,
            DiffKind.Removed => "-" + line.Text,
            _ => " " + line.Text,
        }));

    [Fact]
    public void IdenticalOutputsHaveNoChanges()
    {
        var comparison = OutputDiff.Compare(Lines("a", "b"), Lines("a", "b"));

        Assert.True(comparison.Identical);
        Assert.True(comparison.Aligned);
        Assert.Equal(" a\n b", Render(comparison));
    }

    [Fact]
    public void ChangesAreAlignedAroundUnchangedLines()
    {
        var comparison = OutputDiff.Compare(
            Lines("C 10.0.0.0/24 Gi0/1", "O 10.1.0.0/16 via 10.0.0.2", "S 0.0.0.0/0 via 10.0.0.1"),
            Lines("C 10.0.0.0/24 Gi0/1", "O 10.2.0.0/16 via 10.0.0.2", "S 0.0.0.0/0 via 10.0.0.1", "B 172.16.0.0/12 via 10.0.0.9"));

        Assert.Equal(
            " C 10.0.0.0/24 Gi0/1\n-O 10.1.0.0/16 via 10.0.0.2\n+O 10.2.0.0/16 via 10.0.0.2\n S 0.0.0.0/0 via 10.0.0.1\n+B 172.16.0.0/12 via 10.0.0.9",
            Render(comparison));
        Assert.Equal(2, comparison.Added);
        Assert.Equal(1, comparison.Removed);
        var removed = comparison.Lines[1];
        var added = comparison.Lines[2];
        Assert.Equal((2, (int?)null), (removed.OldNumber!.Value, removed.NewNumber));
        Assert.Equal("10.1.0.0/16", removed.Text.Substring(removed.Changed[0].Start, removed.Changed[0].Length));
        Assert.Equal("10.2.0.0/16", added.Text.Substring(added.Changed[0].Start, added.Changed[0].Length));
    }

    [Fact]
    public void ALineChangedBeyondRecognitionGetsNoWordMarks()
    {
        var comparison = OutputDiff.Compare("alpha beta gamma", "one two three");

        Assert.All(comparison.Lines, line => Assert.Empty(line.Changed));
    }

    [Fact]
    public void TimestampsAreIgnoredByDefaultButKeptForDisplay()
    {
        var older = Lines("Last check: 2026-09-22 14:05:11", "Status: up", "uptime 3w4d", "Sep 22 14:05:11 host sshd[1]: ok");
        var newer = Lines("Last check: 2026-09-27 09:41:02", "Status: up", "uptime 4w1d", "Sep 27 09:41:02 host sshd[1]: ok");

        var ignoring = OutputDiff.Compare(older, newer);
        var strict = OutputDiff.Compare(older, newer, new OutputDiffOptions { IgnoreTimestamps = false });

        Assert.True(ignoring.Identical);
        Assert.True(ignoring.Lines[0].DiffersOnlyInIgnoredValues);
        Assert.Equal("Last check: 2026-09-22 14:05:11", ignoring.Lines[0].OldText);
        Assert.Equal("Last check: 2026-09-27 09:41:02", ignoring.Lines[0].Text);
        Assert.False(ignoring.Lines[1].DiffersOnlyInIgnoredValues);
        Assert.Equal(3, strict.Added);
    }

    [Fact]
    public void NumbersAreComparedUnlessIgnored()
    {
        var older = Lines("Gi0/1 is up, 1204 input errors", "Gi0/2 is up, 0 input errors");
        var newer = Lines("Gi0/1 is up, 1311 input errors", "Gi0/2 is down, 0 input errors");

        var ignoring = OutputDiff.Compare(older, newer, new OutputDiffOptions { IgnoreNumbers = true });
        var strict = OutputDiff.Compare(older, newer);

        Assert.Equal(1, ignoring.Added);
        Assert.Equal("Gi0/2 is down, 0 input errors", ignoring.Lines.Single(line => line.Kind == DiffKind.Added).Text);
        Assert.Equal(2, strict.Added);
    }

    [Fact]
    public void TrailingSpacesAndCarriageReturnsDoNotCountAsChanges()
    {
        Assert.True(OutputDiff.Compare("a   \r\nb", "a\nb").Identical);
    }

    [Fact]
    public void EmptyOutputsCompare()
    {
        Assert.True(OutputDiff.Compare("", "").Identical);
        var added = OutputDiff.Compare("", Lines("x", "y"));
        Assert.Equal(2, added.Added);
        Assert.Equal(0, added.Removed);
    }

    [Fact]
    public void UnrelatedLongOutputsAreReplacedRatherThanAligned()
    {
        var older = string.Join('\n', Enumerable.Range(0, OutputDiff.MaxEdits).Select(i => "old " + i));
        var newer = string.Join('\n', Enumerable.Range(0, OutputDiff.MaxEdits).Select(i => "new " + i));

        var comparison = OutputDiff.Compare(older, newer);

        Assert.False(comparison.Aligned);
        Assert.Equal(OutputDiff.MaxEdits, comparison.Removed);
        Assert.Equal(OutputDiff.MaxEdits, comparison.Added);
        Assert.Equal(DiffKind.Removed, comparison.Lines[0].Kind);
        Assert.Equal(DiffKind.Added, comparison.Lines[^1].Kind);
    }

    [Fact]
    public void MyersFindsAShortestEditScript()
    {
        // Classic example from Myers' paper: ABCABBA -> CBABAC takes 5 edits.
        int[] a = [.. "ABCABBA".Select(c => (int)c)];
        int[] b = [.. "CBABAC".Select(c => (int)c)];

        var ops = OutputDiff.Myers(a, 0, a.Length, b, 0, b.Length, 100)!;

        Assert.Equal(5, ops.Count(op => op.Kind != DiffKind.Same));
        var rebuilt = ops.Where(op => op.Kind != DiffKind.Removed)
            .Select(op => op.Kind == DiffKind.Same ? (char)a[op.A] : (char)b[op.B]);
        Assert.Equal("CBABAC", string.Concat(rebuilt));
    }

    [Fact]
    public void UnifiedRowsFoldLongUnchangedStretches()
    {
        var older = string.Join('\n', Enumerable.Range(1, 30).Select(i => "line " + i));
        var newer = older.Replace("line 15", "line fifteen");

        var rows = OutputDiff.Compare(older, newer).Unified(context: 3);

        Assert.Equal(11, rows[0].Skipped);             // lines 1-11
        Assert.Equal("line 12", rows[1].Line!.Text);
        Assert.Equal(DiffKind.Removed, rows[4].Line!.Kind);
        Assert.Equal(DiffKind.Added, rows[5].Line!.Kind);
        Assert.Equal("line 18", rows[8].Line!.Text);
        Assert.Equal(12, rows[9].Skipped);             // lines 19-30
        Assert.Equal(10, rows.Count);
    }

    [Fact]
    public void ShortUnchangedStretchesAreShownRatherThanFolded()
    {
        var rows = OutputDiff.Compare(Lines("a", "b", "c", "d"), Lines("x", "b", "c", "y")).Unified(context: 0);

        Assert.DoesNotContain(rows, row => row.IsGap);
    }

    [Fact]
    public void NegativeContextShowsEveryLine()
    {
        var older = string.Join('\n', Enumerable.Range(1, 30).Select(i => "line " + i));

        var rows = OutputDiff.Compare(older, older).Unified(context: -1);

        Assert.Equal(30, rows.Count);
    }

    [Fact]
    public void SideBySidePairsRemovedWithAddedLines()
    {
        var comparison = OutputDiff.Compare(Lines("keep", "old 1", "old 2", "tail"), Lines("keep", "new 1", "tail", "extra"));

        var rows = comparison.SideBySide(context: -1);

        Assert.Equal(["keep", "old 1", "old 2", "tail", null], rows.Select(row => row.Left?.Text));
        Assert.Equal(["keep", "new 1", null, "tail", "extra"], rows.Select(row => row.Right?.Text));
        Assert.Equal(1, rows[0].Left!.OldNumber);
        Assert.Equal(1, rows[0].Right!.NewNumber);
    }

    [Fact]
    public void SideBySideShowsEachSidesOwnTextForIgnoredDifferences()
    {
        var rows = OutputDiff.Compare("checked 10:00:01", "checked 11:30:45").SideBySide(context: -1);

        Assert.Equal("checked 10:00:01", rows[0].Left!.Text);
        Assert.Equal("checked 11:30:45", rows[0].Right!.Text);
    }

    [Theory]
    [InlineData("  show   ip route  ", "show ip route")]
    [InlineData("ls\t-la", "ls -la")]
    [InlineData("", "")]
    public void CommandsAreMatchedWithWhitespaceCollapsed(string command, string key)
    {
        Assert.Equal(key, CommandHistoryEntry.NormalizeCommand(command));
    }

    [Theory]
    [InlineData("line\n --More-- ", true)]
    [InlineData("line\n---(more 43%)---", true)]
    [InlineData("<--- More --->", true)]
    [InlineData("Press any key to continue", true)]
    [InlineData("more rows follow", false)]
    [InlineData("no pager here", false)]
    public void PagedOutputIsRecognized(string output, bool paged)
    {
        Assert.Equal(paged, new CommandHistoryEntry { Command = "x", Output = output }.LooksPaged);
    }
}
