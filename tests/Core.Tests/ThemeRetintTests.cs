using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class ThemeRetintTests
{
    // Amber-on-brown dark theme, magenta accent.
    private static readonly RetintTarget Dark = new(0x281C00, 0xFFB000, 0xE020C0);
    private static readonly RetintTarget Light = new(0xEFF1F5, 0x4C4F69, 0x1E66F5);

    [Fact]
    public void FluentTextBecomesTheThemeText()
    {
        Assert.Equal(0xFFFFB000u, ThemeRetint.Map(0xFFFFFFFF, fluentDark: true, Dark));
        Assert.Equal(0xE44C4F69u, ThemeRetint.Map(0xE4000000, fluentDark: false, Light));
    }

    [Fact]
    public void EitherFluentSetGivesTheSameThemeText()
    {
        // The app reads only the set for the mode it started in, then draws themes of both modes.
        Assert.Equal(0xFF4C4F69u, ThemeRetint.Map(0xFFFFFFFF, fluentDark: true, Light));
        Assert.Equal(0xFFFFB000u, ThemeRetint.Map(0xFF000000, fluentDark: false, Dark));
    }

    [Fact]
    public void AccentFillsFromTheOtherModeKeepWhiteTextReadable()
    {
        // Dark mode's light accent fill (under black text) on a light theme, where its text is white.
        var fill = ThemeRetint.Map(0xFF4CC2FF, fluentDark: true, Light) & 0xFFFFFF;
        Assert.True(ColorMath.Contrast(fill, 0xFFFFFF) >= 4.5, $"{fill:X6}");
        Assert.Equal(0xFFFFFFFFu, ThemeRetint.Map(0xFF000000, fluentDark: true, Light));
    }

    [Fact]
    public void TranslucentFillsKeepTheirAlphaOverTheThemeText() =>
        Assert.Equal(0x0FFFB000u, ThemeRetint.Map(0x0FFFFFFF, fluentDark: true, Dark));

    [Fact]
    public void FluentSurfaceBecomesTheThemeSurface()
    {
        Assert.Equal(0xFF281C00u, ThemeRetint.Map(0xFF202020, fluentDark: true, Dark));
        Assert.Equal(0xFFEFF1F5u, ThemeRetint.Map(0xFFF3F3F3, fluentDark: false, Light));
    }

    [Fact]
    public void GreysBeyondTheSurfaceGoFurtherFromTheText()
    {
        // Fluent's deeper dark surface (#0A0A0A) is darker than the theme's; its white cards brighter.
        var deeper = ThemeRetint.Map(0xFF0A0A0A, fluentDark: true, Dark) & 0xFFFFFF;
        Assert.True(ColorMath.RelativeLuminance(deeper) < ColorMath.RelativeLuminance(Dark.Background));
        var card = ThemeRetint.Map(0xFFFFFFFF, fluentDark: false, Light) & 0xFFFFFF;
        Assert.True(ColorMath.RelativeLuminance(card) > ColorMath.RelativeLuminance(Light.Background));
    }

    [Fact]
    public void AccentBluesTakeTheThemeAccentHue()
    {
        var mapped = ThemeRetint.Map(0xFF4CC2FF, fluentDark: true, Dark) & 0xFFFFFF;
        int r = (int)(mapped >> 16) & 0xFF, g = (int)(mapped >> 8) & 0xFF, b = (int)mapped & 0xFF;
        Assert.True(r > g && b > g, $"{mapped:X6} is not magenta");
    }

    [Fact]
    public void StatusColorsKeepTheirMeaning() =>
        Assert.Equal(0xFF6CCB5Fu, ThemeRetint.Map(0xFF6CCB5F, fluentDark: true, Dark));

    [Fact]
    public void AccentTextStandsOutFromTheSurface()
    {
        // A dark accent on a dark surface: as text it is lifted to 3:1.
        var target = Dark with { Accent = 0x300060 };
        var text = ThemeRetint.Map(0xFF0078D4, fluentDark: true, target, isText: true) & 0xFFFFFF;
        Assert.True(ColorMath.Contrast(text, target.Background) >= 3);
    }
}
