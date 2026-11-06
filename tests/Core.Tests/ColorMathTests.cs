using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

/// <summary>The one copy of the app's color arithmetic: High Contrast's highlight text, the
/// welcome page and the theme derivations all pick and check colors through it.</summary>
public sealed class ColorMathTests
{
    [Theory]
    [InlineData(0xFFFFFFu, 0x000000u, 21.0)]
    [InlineData(0x000000u, 0x000000u, 1.0)]
    [InlineData(0x777777u, 0xFFFFFFu, 4.48)]
    public void ContrastIsWcag(uint first, uint second, double expected) =>
        Assert.Equal(expected, ColorMath.Contrast(first, second), 2);

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
        Assert.Equal(black, ColorMath.PrefersBlackText(background));
        Assert.Equal(black, ColorMath.Contrast(0x000000, background) >= ColorMath.Contrast(0xFFFFFF, background));
    }

    [Fact]
    public void ReadableKeepsAColorThatAlreadyStandsOut() =>
        Assert.Equal(0xEEEEEEu, ColorMath.Readable(0xEEEEEE, 0x101010, 4.5));

    [Theory]
    [InlineData(0x303030u, 0x202020u, 4.5)]
    [InlineData(0x005FB8u, 0x1E1E1Eu, 3.0)]
    [InlineData(0xD0D0D0u, 0xF3F3F3u, 4.5)]
    public void ReadableMovesTowardBlackOrWhiteUntilTheMinimumHolds(uint color, uint surface, double minimum)
    {
        var readable = ColorMath.Readable(color, surface, minimum);

        Assert.True(ColorMath.Contrast(readable, surface) >= minimum, $"{readable:X6} on {surface:X6}");
        var target = ColorMath.PrefersBlackText(surface) ? 0x000000u : 0xFFFFFFu;
        Assert.True(Math.Abs(ColorMath.RelativeLuminance(readable) - ColorMath.RelativeLuminance(target))
            < Math.Abs(ColorMath.RelativeLuminance(color) - ColorMath.RelativeLuminance(target)));
    }

    [Fact]
    public void ReadableEndsAtTheTargetWhenNoStepIsEnough() =>
        Assert.Equal(0x000000u, ColorMath.Readable(0x808080, 0x707070, 21, toward: 0x000000));

    [Theory]
    [InlineData(0xFF0000u)]
    [InlineData(0x4CC2FFu)]
    [InlineData(0x0078D4u)]
    [InlineData(0x777777u)]
    public void HslRoundTrips(uint rgb)
    {
        var hsl = ColorMath.ToHsl(rgb);
        Assert.Equal(rgb, ColorMath.FromHsl(hsl.Hue, hsl.Saturation, hsl.Lightness));
    }
}
