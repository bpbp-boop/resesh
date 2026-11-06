using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class ThemeAccentTests
{
    private static ThemePalette Palette(uint background, uint cursor, params uint[] ansi16) =>
        new(background, 0xCCCCCC, cursor, 0x444444, null, ansi16);

    private static readonly uint[] Greys = [.. Enumerable.Repeat(0x808080u, 16)];

    [Fact]
    public void ColorfulCursorIsTheAccent() =>
        Assert.Equal(0x19CDE6u, ThemeAccent.Pick(ThemeCatalog.Find("Synthwave").Palette));

    [Fact]
    public void GreyCursorFallsBackToTheMostColorfulReadableAnsiColor()
    {
        var ansi = (uint[])Greys.Clone();
        ansi[1] = 0xFF2020; // most colorful, readable on black
        ansi[4] = 0x5070A0;
        Assert.Equal(0xFF2020u, ThemeAccent.Pick(Palette(0x000000, 0xFFFFFF, ansi)));
    }

    [Fact]
    public void ColorsThatDoNotStandOutFromTheBackgroundAreSkipped()
    {
        var ansi = (uint[])Greys.Clone();
        ansi[1] = 0x400000; // colorful but too dark on black
        ansi[2] = 0x20C040;
        Assert.Equal(0x20C040u, ThemeAccent.Pick(Palette(0x000000, 0x300030, ansi)));
    }

    [Fact]
    public void GreyThemesHaveNoAccent() =>
        Assert.Null(ThemeAccent.Pick(Palette(0x101010, 0xEEEEEE, Greys)));

    [Fact]
    public void AnsiZeroAndSevenAreNeverPicked()
    {
        var ansi = (uint[])Greys.Clone();
        ansi[0] = 0xFF0000;
        ansi[7] = 0x00FF00;
        Assert.Null(ThemeAccent.Pick(Palette(0x101010, 0xEEEEEE, ansi)));
    }

    [Fact]
    public void MostCollectionThemesGetAnAccent()
    {
        var collection = ThemeCatalog.All.Where(theme => !theme.IsBuiltIn).ToList();
        Assert.InRange(collection.Count(theme => ThemeAccent.Pick(theme.Palette) is not null), collection.Count * 9 / 10, collection.Count);
    }
}
