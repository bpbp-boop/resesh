using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class ThemeCategoriesTests
{
    [Theory]
    [InlineData("resesh Dark", ThemeCategory.Traditional)]
    [InlineData("Builtin Dark", ThemeCategory.Traditional)]
    [InlineData("Green Phosphor CRT", ThemeCategory.Retro)]
    [InlineData("C64", ThemeCategory.Retro)]
    [InlineData("IBM 5153 CGA", ThemeCategory.Retro)]
    [InlineData("Catppuccin Mocha", ThemeCategory.Cool)]
    public void CatalogThemes_BelongToRecognizableCategories(string name, ThemeCategory category)
    {
        var theme = Assert.Single(ThemeCatalog.All, theme => theme.Name == name);

        Assert.True(theme.Categories.HasFlag(category), $"{name}: {theme.Categories}");
    }

    [Fact]
    public void SolarizedLight_IsNotRetro()
    {
        var theme = Assert.Single(ThemeCatalog.All, theme => theme.Name == "Solarized Light");

        Assert.False(theme.Categories.HasFlag(ThemeCategory.Retro));
    }

    [Theory]
    [InlineData(ThemeCategory.Traditional)]
    [InlineData(ThemeCategory.Cool)]
    [InlineData(ThemeCategory.Warm)]
    [InlineData(ThemeCategory.Pastel)]
    [InlineData(ThemeCategory.Vivid)]
    [InlineData(ThemeCategory.Retro)]
    [InlineData(ThemeCategory.HighContrast)]
    public void EveryCategory_HasUsefulButSelectiveCatalogCoverage(ThemeCategory category)
    {
        var count = ThemeCatalog.All.Count(theme => theme.Categories.HasFlag(category));

        Assert.InRange(count, 20, (int)(ThemeCatalog.All.Count * 0.4));
    }
}
