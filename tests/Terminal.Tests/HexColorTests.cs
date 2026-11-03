using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

/// <summary>The one colour reading the highlighter, the rule editor and session colour tags
/// share, so none accepts a value another rejects.</summary>
public class HexColorTests
{
    [Theory]
    [InlineData("#ff5555", 0xFF5555u)]
    [InlineData("#00Aaff", 0x00AAFFu)]
    [InlineData("#000000", 0u)]
    public void ReadsExactlyHashAndSixHexDigits(string text, uint expected) =>
        Assert.Equal(expected, HexColor.Parse(text));

    [Theory]
    [InlineData("# 12345")] // NumberStyles.HexNumber allowed leading whitespace
    [InlineData("#12345 ")]
    [InlineData("#12345\n")] // a regex ending in $ allowed a trailing line break
    [InlineData("#+12345")]
    [InlineData("#12345g")]
    [InlineData("ff5555")]
    [InlineData("#ff55")]
    [InlineData("#ff555555")]
    [InlineData("#ff5555\n")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsEverythingElse(string? text) =>
        Assert.Null(HexColor.Parse(text));

    [Fact]
    public void ARuleWithoutAColourIsPaintedInTheDefault() =>
        Assert.Equal(0xFFFFFFu, HexColor.ParseHighlight(null));

    [Fact]
    public void ARuleWithAnUnreadableColourIsSkipped()
    {
        var highlighter = new Highlighter();
        highlighter.SetRules([
            new TerminalHighlight("bad", "down", "# 12345"),
            new TerminalHighlight("none", "up", null),
        ]);
        Assert.Equal(["none"], highlighter.Rules.Select(rule => rule.Id));
    }
}
