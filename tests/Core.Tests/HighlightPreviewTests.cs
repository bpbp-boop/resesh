using Resesh.Core.Models;

namespace Resesh.Core.Tests;

public sealed class HighlightPreviewTests
{
    private static HighlightRule Rule(string pattern, string? color, bool bold = false, bool enabled = true, bool matchCase = false) =>
        new() { Id = pattern, Pattern = pattern, Color = color!, Bold = bold, Enabled = enabled, MatchCase = matchCase };

    private static uint? ColorAt(IReadOnlyList<HighlightSpan> spans, int index) =>
        spans.Single(span => index >= span.Start && index < span.Start + span.Length).Color;

    [Fact]
    public void SpansCoverTheTextInOrder()
    {
        var spans = HighlightPreview.Paint("abc def ghi", [Rule("def", "#112233")]);

        Assert.Equal(0, spans[0].Start);
        for (var i = 1; i < spans.Count; i++)
            Assert.Equal(spans[i - 1].Start + spans[i - 1].Length, spans[i].Start);
        Assert.Equal(11, spans[^1].Start + spans[^1].Length);
        Assert.Equal([null, 0x112233u, null], spans.Select(span => span.Color));
    }

    [Fact]
    public void LaterRulePaintsOverEarlierOverlap()
    {
        var spans = HighlightPreview.Paint("10.0.0.1", [Rule(@"\d+", "#111111"), Rule(@"0\.0", "#222222")]);

        Assert.Equal(0x111111u, ColorAt(spans, 0));
        Assert.Equal(0x222222u, ColorAt(spans, 3));
    }

    [Fact]
    public void BoldRulesTint()
    {
        var spans = HighlightPreview.Paint("it failed", [Rule("failed", "#ff5555", bold: true)]);

        Assert.True(spans.Single(span => span.Color == 0xFF5555).Tint);
        Assert.False(spans.Single(span => span.Color is null).Tint);
    }

    [Fact]
    public void DisabledInvalidAndUnreadableRulesAreSkipped()
    {
        var spans = HighlightPreview.Paint("abc",
            [Rule("a", "#111111", enabled: false), Rule("(", "#222222"), Rule("b", "red"), Rule("c", "#12345")]);

        Assert.All(spans, span => Assert.Null(span.Color));
    }

    [Fact]
    public void MissingColorPaintsWhiteAsTheTerminalDoes()
    {
        var spans = HighlightPreview.Paint("x", [Rule("x", null)]);

        Assert.Equal(0xFFFFFFu, Assert.Single(spans).Color);
    }

    [Fact]
    public void MatchCaseIsHonored()
    {
        Assert.Null(Assert.Single(HighlightPreview.Paint("ERROR", [Rule("error", "#111111", matchCase: true)])).Color);
        Assert.Equal(0x111111u, Assert.Single(HighlightPreview.Paint("ERROR", [Rule("error", "#111111")])).Color);
    }

    [Fact]
    public void BuiltInRulesColorASyslogLineAsTheTerminalWould()
    {
        const string line = "Mar 23 20:11:53 web1 sshd[812]: Failed password";
        var spans = HighlightPreview.Paint(line, BuiltinHighlights.Rules);

        Assert.Equal(0xCE9178u, ColorAt(spans, line.IndexOf("20:11:53", StringComparison.Ordinal)));
        Assert.Equal(0x4EC9B0u, ColorAt(spans, line.IndexOf("sshd", StringComparison.Ordinal)));
        var failed = spans.Single(span => span.Start == line.IndexOf("Failed", StringComparison.Ordinal));
        Assert.Equal((0xFF5555u, true), (failed.Color!.Value, failed.Tint));
    }
}
