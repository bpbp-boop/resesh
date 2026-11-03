using Resesh.App;

namespace Resesh.AppLogic.Tests;

/// <summary>High Contrast's highlight text and the welcome page pick black or white text the
/// same WCAG way.</summary>
public sealed class ColorContrastTests
{
    [Theory]
    [InlineData(0xFFFFFFu, 0x000000u, 21.0)]
    [InlineData(0x000000u, 0x000000u, 1.0)]
    [InlineData(0x777777u, 0xFFFFFFu, 4.48)]
    public void RatioIsWcag(uint first, uint second, double expected) =>
        Assert.Equal(expected, ColorContrast.Ratio(first, second), 2);

    [Theory]
    [InlineData(0xFFFFFFu, true)]
    [InlineData(0xFFFF00u, true)]
    [InlineData(0x000000u, false)]
    [InlineData(0x000080u, false)]
    // Gamma-encoded Rec.709 under 128 chose white for these; black has the higher ratio.
    [InlineData(0x777777u, true)]
    [InlineData(0x00A000u, true)]
    public void TextIsWhicheverOfBlackOrWhiteContrastsMore(uint background, bool black)
    {
        Assert.Equal(black, ColorContrast.PrefersBlackText(background));
        var blackRatio = ColorContrast.Ratio(0x000000, background);
        var whiteRatio = ColorContrast.Ratio(0xFFFFFF, background);
        Assert.Equal(black, blackRatio >= whiteRatio);
    }
}
