using Resesh.App;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

/// <summary>The catalog owns palettes; only the built-ins need hand-tuned app chrome.</summary>
public sealed class ThemeTableTests
{
    [Fact]
    public void EveryBuiltInThemeHasHandTunedChromeAndOnlyBuiltInsDo()
    {
        var builtIns = ThemeCatalog.All.Where(theme => theme.IsBuiltIn).ToList();
        Assert.Equal(builtIns.Select(theme => theme.Id).Order(), ThemeChromes.ById.Keys.Order());
        foreach (var theme in builtIns)
            Assert.Same(ThemeChromes.ById[theme.Id], ThemeChromes.Find(theme.Id));
    }

    [Fact]
    public void CollectionThemesDeriveChromeFromTheirPalette()
    {
        var collection = ThemeCatalog.All.Where(theme => !theme.IsBuiltIn).ToList();
        Assert.NotEmpty(collection);
        foreach (var theme in collection)
        {
            Assert.False(ThemeChromes.ById.ContainsKey(theme.Id));
            Assert.Equal(ThemeChromes.Derive(theme.Palette), ThemeChromes.Find(theme.Id));
        }
    }

    [Fact]
    public void VaporwaveEmphasizesFocusWhileDefaultThemesKeepAOnePixelAccent()
    {
        Assert.Equal(3, ThemeChromes.Find("vaporwave").AccentBarThickness);
        Assert.Equal(1, ThemeChromes.Find(ThemeIds.Dark).AccentBarThickness);
        Assert.Equal(1, ThemeChromes.Find(ThemeIds.Light).AccentBarThickness);
    }

    [Fact]
    public void SystemHasNoColorsOfItsOwn()
    {
        Assert.False(ThemeCatalog.Contains(ThemeIds.System));
        Assert.False(ThemeChromes.ById.ContainsKey(ThemeIds.System));
    }

    [Fact]
    public void LightThemesHaveALightBackground()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var luminance = RelativeLuminance(theme.Palette.Background);
            Assert.True(theme.IsLight == luminance > 0.5,
                $"{theme.Id} is marked {(theme.IsLight ? "light" : "dark")} but its background luminance is {luminance:0.00}");
            Assert.Equal(theme.IsLight, ThemeCatalog.IsLight(theme.Id));
        }
    }

    [Fact]
    public void UnknownIdsFallBackToTheSameDefaultEverywhere()
    {
        Assert.Equal(ThemeIds.Default, ThemeCatalog.Find("no-such-theme").Id);
        Assert.Equal(ThemeIds.Default, new AppSettings().ThemeFor(systemIsLight: false));
        Assert.Same(ThemeChromes.Find(ThemeIds.Default), ThemeChromes.Find("no-such-theme"));
    }

    [Fact]
    public void BuiltInsComeFirstAndIdsAreUniqueIgnoringCase()
    {
        Assert.Equal(14, ThemeCatalog.All.TakeWhile(theme => theme.IsBuiltIn).Count());
        Assert.All(ThemeCatalog.All.Skip(14), theme => Assert.False(theme.IsBuiltIn));
        Assert.Equal(ThemeCatalog.All.Count,
            ThemeCatalog.All.Select(theme => theme.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var id in new[] { ThemeIds.Dark, ThemeIds.Light, ThemeIds.Default })
            Assert.Contains(ThemeCatalog.All, theme => theme.Id == id);
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
