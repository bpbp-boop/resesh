using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

/// <summary>The ghostty surface's port of addon-highlight.js.</summary>
public class HighlighterTests
{
    private const uint Fg = 0xCCCCCC, Bg = 0x0C0C0C;

    /// <summary>A row as the shim flattens it: CJK and other wide characters take two cells.</summary>
    private static TerminalCell[] Row(string text, int width = 40)
    {
        var cells = new List<TerminalCell>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            var cp = (uint)char.ConvertToUtf32(element, 0);
            var wide = cp is >= 0x1100 and (<= 0x115F or (>= 0x2E80 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3) or (>= 0xFF00 and <= 0xFF60));
            cells.Add(new TerminalCell { Codepoint = cp == ' ' ? ' ' : cp, Foreground = Fg, Background = Bg, Flags = TerminalCell.DefaultBackground, Wide = wide ? TerminalCell.WideChar : TerminalCell.Narrow, GraphemeLength = 1 });
            if (wide)
                cells.Add(new TerminalCell { Foreground = Fg, Background = Bg, Flags = TerminalCell.DefaultBackground, Wide = TerminalCell.SpacerTail });
        }
        while (cells.Count < width)
            cells.Add(new TerminalCell { Foreground = Fg, Background = Bg, Flags = TerminalCell.DefaultBackground });
        return [.. cells];
    }

    private static Highlighter With(params object[] rules)
    {
        var h = new Highlighter();
        h.SetRules(rules);
        return h;
    }

    private static object Rule(string pattern, string color = "#ff5555", bool bold = false, bool underline = false,
        bool matchCase = false, bool showInOverview = false) =>
        new { id = pattern, pattern, color, bold, underline, matchCase, showInOverview };

    [Fact]
    public void RecolorsOnlyMatchedCells()
    {
        var row = Row("disk error on sda");
        With(Rule("error")).ApplyRow(row, null);
        for (var x = 0; x < row.Length; x++)
            Assert.Equal(x is >= 5 and < 10 ? 0xFF5555u : Fg, row[x].Foreground);
    }

    [Fact]
    public void IgnoresCaseUnlessMatchCase()
    {
        var row = Row("ERROR Error error");
        With(Rule("error", matchCase: true)).ApplyRow(row, null);
        Assert.Equal(Fg, row[0].Foreground);
        Assert.Equal(0xFF5555u, row[12].Foreground);

        row = Row("ERROR");
        With(Rule("error")).ApplyRow(row, null);
        Assert.Equal(0xFF5555u, row[0].Foreground);
    }

    [Fact]
    public void WideCharactersMapToTheirCells()
    {
        var row = Row("日本 fail");
        With(Rule("fail")).ApplyRow(row, null);
        // 日 and 本 take columns 0-3, the space column 4, "fail" columns 5-8.
        Assert.Equal(Fg, row[4].Foreground);
        Assert.All(row[5..9], c => Assert.Equal(0xFF5555u, c.Foreground));
        Assert.Equal(Fg, row[9].Foreground);

        row = Row("日本");
        With(Rule("本")).ApplyRow(row, null);
        Assert.Equal(0xFF5555u, row[2].Foreground);
        Assert.Equal(0xFF5555u, row[3].Foreground); // the wide character's second cell
    }

    [Fact]
    public void BoldTintsTheBackgroundAndUnderlineMarksCells()
    {
        var row = Row("warn");
        With(Rule("warn", color: "#ffffff", bold: true, underline: true)).ApplyRow(row, null);
        Assert.Equal(Highlighter.Blend(Bg, 0xFFFFFF, 0.22), row[0].Background);
        Assert.Equal(0, row[0].Flags & TerminalCell.DefaultBackground);
        Assert.NotEqual(0, row[0].Flags & TerminalCell.Underline);
        Assert.Equal(TerminalCell.DefaultBackground, row[4].Flags);
    }

    [Fact]
    public void LaterRulesPaintOverEarlierOnes()
    {
        var row = Row("connection refused");
        With(Rule("connection refused", "#111111"), Rule("refused", "#222222")).ApplyRow(row, null);
        Assert.Equal(0x111111u, row[0].Foreground);
        Assert.Equal(0x222222u, row[11].Foreground);
    }

    [Fact]
    public void CapsMatchesPerRow()
    {
        var row = Row(new string('x', 60), width: 60);
        With(Rule("x")).ApplyRow(row, null);
        Assert.Equal(40, row.Count(c => c.Foreground == 0xFF5555u));
    }

    [Fact]
    public void SkipsInvalidPatternsAndKeepsLookarounds()
    {
        var h = With(Rule("("), Rule(@"(?<=port )\d+"));
        Assert.Single(h.Rules);
        var row = Row("port 22 open");
        h.ApplyRow(row, null);
        Assert.Equal(Fg, row[0].Foreground);
        Assert.Equal(0xFF5555u, row[5].Foreground);
        Assert.Equal(0xFF5555u, row[6].Foreground);
    }

    [Fact]
    public void GraphemeClustersComeFromTheLookup()
    {
        var row = Row("cafe ok");
        row[3] = row[3] with { GraphemeLength = 2 }; // "é" as e + combining acute
        With(Rule("café")).ApplyRow(row, x => x == 3 ? "é" : null);
        Assert.Equal(Fg, row[0].Foreground); // "café" in NFC is not "café"

        With(Rule("café")).ApplyRow(row, x => x == 3 ? "é" : null);
        Assert.All(row[0..4], c => Assert.Equal(0xFF5555u, c.Foreground));
    }

    [Fact]
    public void OverviewMaskCoversOnlyOverviewRules()
    {
        var h = With(Rule("error", showInOverview: true), Rule("warn"), Rule("fail", showInOverview: true));
        Assert.True(h.HasOverviewRules);
        Assert.Equal(0b101u, h.OverviewMask("error: build fail, warn"));
        Assert.Equal(0u, h.OverviewMask("warn only"));
    }
}
