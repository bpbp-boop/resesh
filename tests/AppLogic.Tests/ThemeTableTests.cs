using Resesh.App;
using Resesh.Core.Storage;
using Resesh.Terminal.Native;

namespace Resesh.AppLogic.Tests;

/// <summary>A theme is three tables: the catalog (id, name, light or dark), the terminal
/// palette and the app chrome. These keep them describing the same themes.</summary>
public sealed class ThemeTableTests
{
    private static IEnumerable<ThemeChoice> FixedThemes =>
        ThemeCatalog.All.Where(theme => theme.Id != ThemeIds.System);

    [Fact]
    public void EveryFixedThemeHasATerminalPaletteAndChrome()
    {
        foreach (var theme in FixedThemes)
        {
            Assert.True(TerminalThemes.Contains(theme.Id), $"{theme.Id} has no terminal palette");
            Assert.True(ThemeChromes.ById.ContainsKey(theme.Id), $"{theme.Id} has no chrome");
        }
    }

    [Fact]
    public void ThePaletteTablesHoldOnlyCatalogThemes()
    {
        var ids = FixedThemes.Select(theme => theme.Id).Order().ToList();
        Assert.Equal(ids, TerminalThemes.Ids.Order());
        Assert.Equal(ids, ThemeChromes.ById.Keys.Order());
    }

    [Fact]
    public void SystemHasNoColorsOfItsOwn()
    {
        // It is resolved to light or dark before anything is drawn.
        Assert.False(TerminalThemes.Contains(ThemeIds.System));
        Assert.False(ThemeChromes.ById.ContainsKey(ThemeIds.System));
    }

    [Fact]
    public void LightThemesHaveALightBackground()
    {
        foreach (var theme in FixedThemes)
        {
            var luminance = RelativeLuminance(TerminalThemes.Find(theme.Id).Background);
            Assert.True(theme.IsLight == luminance > 0.5,
                $"{theme.Id} is marked {(theme.IsLight ? "light" : "dark")} but its background luminance is {luminance:0.00}");
        }
    }

    [Fact]
    public void UnknownIdsFallBackToTheSameDefaultEverywhere()
    {
        Assert.Equal(ThemeIds.Default, TerminalThemes.DefaultId);
        Assert.Equal(ThemeIds.Default, ThemeCatalog.Find("no-such-theme").Id);
        Assert.Equal(ThemeIds.Default, new AppSettings().Theme);
        Assert.Same(TerminalThemes.Find(ThemeIds.Default), TerminalThemes.Find("no-such-theme"));
        Assert.Same(ThemeChromes.Find(ThemeIds.Default), ThemeChromes.Find("no-such-theme"));
    }

    [Fact]
    public void ThemeIdsAreInTheCatalog()
    {
        foreach (var id in new[] { ThemeIds.Dark, ThemeIds.Light, ThemeIds.System, ThemeIds.Default })
            Assert.Contains(ThemeCatalog.All, theme => theme.Id == id);
        Assert.True(ThemeCatalog.IsLight(ThemeIds.Light));
        Assert.False(ThemeCatalog.IsLight(ThemeIds.Dark));
    }

    private static double RelativeLuminance(uint rgb)
    {
        static double Linear(uint channel)
        {
            var value = (channel & 0xFF) / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Linear(rgb >> 16)) + (0.7152 * Linear(rgb >> 8)) + (0.0722 * Linear(rgb));
    }
}
