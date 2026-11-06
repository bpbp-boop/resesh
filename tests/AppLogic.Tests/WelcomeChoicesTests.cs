using Resesh.App.ViewModels;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

public sealed class WelcomeChoicesTests
{
    private static readonly AppSettings Fresh = new() { OnboardingCompleted = false };

    [Fact]
    public void Finish_KeepsAThemeSavedInSettingsWhileWelcomeWasOpen()
    {
        var choices = new WelcomeChoices(Fresh);
        choices.Choose(choices.Shown with { CopyOnSelect = false });
        var saved = Fresh with
        {
            Appearance = ThemeAppearances.System,
            LightTheme = "solarized-light",
            DarkTheme = "nord",
            ShowStatusBar = false,
        };

        var finished = choices.Complete(saved);

        Assert.Equal(ThemeAppearances.System, finished.Appearance);
        Assert.Equal("solarized-light", finished.LightTheme);
        Assert.Equal("nord", finished.DarkTheme);
        Assert.False(finished.ShowStatusBar);
        Assert.False(finished.CopyOnSelect);
        Assert.True(finished.OnboardingCompleted);
    }

    [Fact]
    public void ASettingSavedElsewhere_ReplacesThePickMadeOverIt_AndKeepsTheOthers()
    {
        var choices = new WelcomeChoices(Fresh);
        choices.Choose(choices.Shown with { DarkTheme = "nord", RightClickPaste = false });
        Assert.True(choices.PreviewsTheme);

        choices.Refresh(Fresh with { DarkTheme = "dracula" });

        Assert.Equal("dracula", choices.Shown.DarkTheme);
        Assert.False(choices.PreviewsTheme);
        Assert.False(choices.Shown.RightClickPaste);
    }

    [Fact]
    public void Refresh_ShowsSettingsTheUserDidNotTouch()
    {
        var choices = new WelcomeChoices(Fresh);

        choices.Refresh(Fresh with { KeepCommandHistory = true, ConfirmCloseActiveSessions = false });

        Assert.True(choices.Shown.KeepCommandHistory);
        Assert.False(choices.Shown.ConfirmCloseActiveSessions);
    }

    [Fact]
    public void Finish_IsOffOnlyOnceWelcomeIsDoneAndNothingDiffers()
    {
        var done = Fresh with { OnboardingCompleted = true };
        var choices = new WelcomeChoices(done);
        Assert.True(choices.HasChanges(Fresh));
        Assert.False(choices.HasChanges(done));

        choices.Choose(WelcomeValues.Of(done with { LightTheme = "LIGHT", DarkTheme = "DARK" }));
        Assert.False(choices.HasChanges(done));
        choices.Choose(choices.Shown with { WriteCrashReports = false });
        Assert.True(choices.HasChanges(done));
    }

    [Theory]
    [InlineData(ThemeAppearances.Light, "nord", ThemeIds.Light, "nord")]
    [InlineData(ThemeAppearances.Dark, "solarized-light", "solarized-light", ThemeIds.Dark)]
    [InlineData(ThemeAppearances.System, "NORD", ThemeIds.Light, "nord")]
    public void WithModeTheme_ChangesOnlyTheThemesModeAndKeepsAppearance(
        string appearance, string theme, string lightTheme, string darkTheme)
    {
        var original = WelcomeValues.Of(Fresh with { Appearance = appearance });

        var changed = original.WithModeTheme(theme);

        Assert.Equal(appearance, changed.Appearance);
        Assert.Equal(lightTheme, changed.LightTheme);
        Assert.Equal(darkTheme, changed.DarkTheme);
        Assert.Equal(original with { LightTheme = lightTheme, DarkTheme = darkTheme }, changed);
    }

    [Theory]
    [InlineData("solarized-light", ThemeAppearances.Light, "solarized-light", "nord")]
    [InlineData("dracula", ThemeAppearances.Dark, ThemeIds.Light, "dracula")]
    public void WithFixedTheme_FixesAppearanceToTheThemesModeAndKeepsTheOtherSlot(
        string theme, string appearance, string lightTheme, string darkTheme)
    {
        var original = WelcomeValues.Of(Fresh with { Appearance = ThemeAppearances.System, DarkTheme = "nord" });

        var changed = original.WithFixedTheme(theme);

        Assert.Equal((appearance, lightTheme, darkTheme), (changed.Appearance, changed.LightTheme, changed.DarkTheme));
        Assert.Equal(theme, changed.ThemeFor(systemIsLight: !ThemeCatalog.IsLight(theme)));
    }

    [Theory]
    [InlineData("appearance")]
    [InlineData("light")]
    [InlineData("dark")]
    public void PreviewsTheme_TracksAppearanceAndBothModeThemes(string changed)
    {
        var choices = new WelcomeChoices(Fresh);
        var original = choices.Shown;
        Assert.False(choices.PreviewsTheme);

        choices.Choose(changed switch
        {
            "appearance" => original with { Appearance = ThemeAppearances.System },
            "light" => original with { LightTheme = "solarized-light" },
            _ => original with { DarkTheme = "nord" },
        });

        Assert.True(choices.PreviewsTheme);
        choices.Choose(original);
        Assert.False(choices.PreviewsTheme);
    }

    [Theory]
    [InlineData(false, "nord")]
    [InlineData(true, "solarized-light")]
    public void ThemeFor_UsesTheSameAppearanceRuleAsSavedSettings(bool systemIsLight, string expected)
    {
        var settings = Fresh with
        {
            Appearance = ThemeAppearances.System,
            LightTheme = "solarized-light",
            DarkTheme = "nord",
        };

        Assert.Equal(expected, WelcomeValues.Of(settings).ThemeFor(systemIsLight));
        Assert.Equal(settings.ThemeFor(systemIsLight), WelcomeValues.Of(settings).ThemeFor(systemIsLight));
    }
}
