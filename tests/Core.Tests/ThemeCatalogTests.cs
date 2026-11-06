using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class ThemeCatalogTests
{
    [Fact]
    public void EmbeddedCollectionLoadsWithBuiltInsWinningNameAndIdClashes()
    {
        using var stream = typeof(ThemeCatalog).Assembly.GetManifestResourceStream("ColorSchemes/color-schemes.tsv");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var upstream = new List<ThemeChoice>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#')
                upstream.Add(ThemeCatalog.ParseCollectionLine(line));

        Assert.Equal(890, upstream.Count);
        var builtIns = ThemeCatalog.All.Where(theme => theme.IsBuiltIn).ToList();
        var taken = builtIns.SelectMany(theme => new[] { theme.Id, theme.Name }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = upstream.Where(theme => !taken.Contains(theme.Id))
            .OrderBy(theme => theme.Name, StringComparer.OrdinalIgnoreCase).Select(theme => theme.Id).ToList();
        var collection = ThemeCatalog.All.Where(theme => !theme.IsBuiltIn).ToList();
        Assert.InRange(collection.Count, 875, 890);
        Assert.Equal(expected, collection.Select(theme => theme.Id));
        Assert.All(collection, theme => Assert.Equal(theme.Name, theme.Id));
        Assert.True(Assert.Single(ThemeCatalog.All,
            theme => string.Equals(theme.Name, "Dracula", StringComparison.OrdinalIgnoreCase)).IsBuiltIn);
        Assert.Equal("Catppuccin Latte", ThemeCatalog.Find("Catppuccin Latte").Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(21)]
    [InlineData(23)]
    public void ParseCollectionLine_RejectsWrongFieldCount(int count)
    {
        var line = string.Join('\t', Enumerable.Repeat("000000", count));

        Assert.Throws<InvalidDataException>(() => ThemeCatalog.ParseCollectionLine(line));
    }

    [Fact]
    public void ParseCollectionLine_PreservesSelectionForegroundAndAnsiOrder()
    {
        var colors = Enumerable.Range(1, 21).Select(color => color.ToString("X6"));

        var theme = ThemeCatalog.ParseCollectionLine("Test theme\t" + string.Join('\t', colors));

        Assert.Equal("Test theme", theme.Id);
        Assert.Equal(theme.Id, theme.Name);
        Assert.False(theme.IsBuiltIn);
        Assert.Equal(1u, theme.Palette.Background);
        Assert.Equal(2u, theme.Palette.Foreground);
        Assert.Equal(3u, theme.Palette.Cursor);
        Assert.Equal(4u, theme.Palette.Selection);
        Assert.Equal(5u, theme.Palette.SelectionForeground);
        Assert.Equal(Enumerable.Range(6, 16).Select(color => (uint)color), theme.Palette.Ansi);
    }

    [Fact]
    public void CollectionPalettesCarrySelectionForeground()
    {
        Assert.All(ThemeCatalog.All.Where(theme => !theme.IsBuiltIn), theme =>
        {
            Assert.NotNull(theme.Palette.SelectionForeground);
            Assert.Equal(16, theme.Palette.Ansi.Count);
        });
        var theme = ThemeCatalog.Find("0x96f");
        Assert.False(theme.IsBuiltIn);
        Assert.Equal(0xFCFCFAu, theme.Palette.Selection);
        Assert.Equal(0x262427u, theme.Palette.SelectionForeground);
    }

    [Theory]
    [InlineData(ThemeAppearances.Light, false, "solarized-light")]
    [InlineData(ThemeAppearances.Light, true, "solarized-light")]
    [InlineData(ThemeAppearances.Dark, false, "nord")]
    [InlineData(ThemeAppearances.Dark, true, "nord")]
    [InlineData(ThemeAppearances.System, false, "nord")]
    [InlineData(ThemeAppearances.System, true, "solarized-light")]
    [InlineData("unknown", false, "nord")]
    [InlineData("unknown", true, "nord")]
    [InlineData(null, true, "nord")]
    public void ThemeFor_AppearanceSelectsModeBeforeWindowsSetting(string? appearance, bool systemIsLight, string expected)
    {
        var settings = new AppSettings
        {
            Appearance = appearance!,
            LightTheme = "solarized-light",
            DarkTheme = "nord",
        };

        Assert.Equal(expected, ThemeAppearances.ThemeFor(appearance, settings.LightTheme, settings.DarkTheme, systemIsLight));
        Assert.Equal(expected, settings.ThemeFor(systemIsLight));
    }

    [Theory]
    [InlineData(ThemeAppearances.Light, false)]
    [InlineData(ThemeAppearances.Dark, true)]
    [InlineData(ThemeAppearances.System, false)]
    [InlineData(ThemeAppearances.System, true)]
    [InlineData("unknown", true)]
    public void ThemeFor_UnknownSelectedThemeFallsBackToDefault(string appearance, bool systemIsLight)
    {
        var settings = new AppSettings
        {
            Appearance = appearance,
            LightTheme = "no-such-light-theme",
            DarkTheme = "no-such-dark-theme",
        };

        Assert.Equal(ThemeIds.Default, settings.ThemeFor(systemIsLight));
        Assert.Equal(ThemeIds.Default,
            ThemeAppearances.ThemeFor(appearance, settings.LightTheme, settings.DarkTheme, systemIsLight));
    }
}
