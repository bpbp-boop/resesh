using Resesh.App.ViewModels;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

/// <summary>Every window applies a saved settings change by what changed, not by who saved it.</summary>
public sealed class SettingsEffectsTests
{
    [Fact]
    public void EverySavedSettingSaysWhatItChanges()
    {
        var properties = typeof(AppSettings).GetProperties()
            .Where(property => property.CanWrite || property.SetMethod is not null)
            .Select(property => property.Name)
            .Order();

        Assert.Equal(properties, SettingsEffects.Described.Order());
    }

    [Fact]
    public void NothingChangedMeansNothingToDo()
    {
        var settings = new AppSettings();
        Assert.Equal(SettingsEffect.None, SettingsEffects.Between(settings, settings with { }));
    }

    [Fact]
    public void WindowStateChangesNeedNothingFromOtherWindows()
    {
        var before = new AppSettings();
        var after = before with
        {
            WindowPlacement = new WindowPlacement(1, 2, 800, 600),
            TreePaneWidth = 300,
            SessionsPaneOpen = false,
            SessionsRailTab = "recent",
        };

        // Not shown on the Settings page either, so a tree resize does not re-read it.
        Assert.Equal(SettingsEffect.None, SettingsEffects.Between(before, after));
    }

    [Theory]
    [InlineData(nameof(AppSettings.Appearance), SettingsEffect.Theme | SettingsEffect.SettingsPages)]
    [InlineData(nameof(AppSettings.LightTheme), SettingsEffect.Theme | SettingsEffect.SettingsPages)]
    [InlineData(nameof(AppSettings.DarkTheme), SettingsEffect.Theme | SettingsEffect.SettingsPages)]
    [InlineData(nameof(AppSettings.LegacyTheme), SettingsEffect.None)]
    [InlineData(nameof(AppSettings.ShowStatusBar), SettingsEffect.StatusBar | SettingsEffect.SettingsPages)]
    [InlineData(nameof(AppSettings.SessionsPaneLayout), SettingsEffect.PaneLayout | SettingsEffect.SettingsPages)]
    [InlineData(nameof(AppSettings.RecordingDirectory), SettingsEffect.Recordings | SettingsEffect.SettingsPages)]
    [InlineData(nameof(AppSettings.FontSize), SettingsEffect.Terminals | SettingsEffect.SettingsPages)]
    [InlineData(nameof(AppSettings.ShowAgentIcons), SettingsEffect.Terminals | SettingsEffect.SettingsPages)]
    [InlineData(nameof(AppSettings.ConfirmMultilinePaste), SettingsEffect.SettingsPages)]
    [InlineData(nameof(AppSettings.RecentSessionIds), SettingsEffect.RecentSessions)]
    [InlineData(nameof(AppSettings.PinnedSessionIds), SettingsEffect.None)]
    public void EachSettingChangesWhatShowsIt(string property, SettingsEffect expected)
    {
        var before = new AppSettings();
        var after = property switch
        {
            nameof(AppSettings.Appearance) => before with { Appearance = ThemeAppearances.Light },
            nameof(AppSettings.LightTheme) => before with { LightTheme = "solarized-light" },
            nameof(AppSettings.DarkTheme) => before with { DarkTheme = "nord" },
            nameof(AppSettings.LegacyTheme) => before with { LegacyTheme = "light" },
            nameof(AppSettings.ShowStatusBar) => before with { ShowStatusBar = false },
            nameof(AppSettings.SessionsPaneLayout) => before with { SessionsPaneLayout = SessionsPaneLayouts.Dock },
            nameof(AppSettings.RecordingDirectory) => before with { RecordingDirectory = @"D:\casts" },
            nameof(AppSettings.FontSize) => before with { FontSize = 18 },
            nameof(AppSettings.ShowAgentIcons) => before with { ShowAgentIcons = false },
            nameof(AppSettings.ConfirmMultilinePaste) => before with { ConfirmMultilinePaste = false },
            nameof(AppSettings.RecentSessionIds) => before with { RecentSessionIds = [Guid.NewGuid()] },
            nameof(AppSettings.PinnedSessionIds) => before with { PinnedSessionIds = [Guid.NewGuid()] },
            _ => throw new ArgumentOutOfRangeException(nameof(property)),
        };

        Assert.Equal(expected, SettingsEffects.Between(before, after));
    }

    [Fact]
    public void ListsCompareByContentNotInstance()
    {
        var id = Guid.NewGuid();
        var before = new AppSettings { RecentSessionIds = [id] };

        Assert.Equal(SettingsEffect.None, SettingsEffects.Between(before, before with { RecentSessionIds = [id] }));
    }

    [Fact]
    public void SeveralChangesCombine()
    {
        var before = new AppSettings();
        var after = before with { Appearance = ThemeAppearances.Light, FontSize = 18, ShowStatusBar = false };

        Assert.Equal(
            SettingsEffect.Theme | SettingsEffect.Terminals | SettingsEffect.StatusBar | SettingsEffect.SettingsPages,
            SettingsEffects.Between(before, after));
    }
}
