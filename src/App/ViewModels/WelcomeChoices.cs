using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>The settings Welcome offers, as one value. Theme ids and the appearance are kept
/// in their canonical form, so two values that draw the same compare equal.</summary>
public sealed record WelcomeValues(
    string Appearance,
    string LightTheme,
    string DarkTheme,
    bool ConfirmCloseActiveSessions,
    bool CopyOnSelect,
    bool RightClickPaste,
    bool WriteCrashReports,
    bool KeepCommandHistory)
{
    public static WelcomeValues Of(AppSettings settings) => new(
        ThemeAppearances.Normalize(settings.Appearance),
        ThemeCatalog.Find(settings.LightTheme).Id,
        ThemeCatalog.Find(settings.DarkTheme).Id,
        settings.ConfirmCloseActiveSessions,
        settings.CopyOnSelect,
        settings.RightClickPaste,
        settings.WriteCrashReports,
        settings.KeepCommandHistory);

    /// <summary>These values with <paramref name="themeId"/> as the theme for its mode (light
    /// or dark, as its background); the appearance stays.</summary>
    public WelcomeValues WithModeTheme(string themeId)
    {
        var theme = ThemeCatalog.Find(themeId);
        return theme.IsLight ? this with { LightTheme = theme.Id } : this with { DarkTheme = theme.Id };
    }

    /// <summary>These values drawing with <paramref name="themeId"/> whatever mode Windows is
    /// in, as <see cref="AppSettings.WithFixedTheme"/> saves it.</summary>
    public WelcomeValues WithFixedTheme(string themeId)
    {
        var theme = ThemeCatalog.Find(themeId);
        return theme.IsLight
            ? this with { Appearance = ThemeAppearances.Light, LightTheme = theme.Id }
            : this with { Appearance = ThemeAppearances.Dark, DarkTheme = theme.Id };
    }

    /// <summary>The theme these values draw with when Windows is in light mode or not.</summary>
    public string ThemeFor(bool systemIsLight) =>
        ThemeAppearances.ThemeFor(Appearance, LightTheme, DarkTheme, systemIsLight);

    internal (string, string, string) Theme => (Appearance, LightTheme, DarkTheme);
}

/// <summary>
/// What Welcome shows and saves. Welcome lists the saved settings with the user's choices on
/// the page laid over them, and Settings (in any window) can change the same settings while
/// it is open. Each choice is kept only while the setting it was made over is unchanged: a
/// later save elsewhere replaces it. Finishing writes the choices onto the settings saved at
/// that moment, so nothing saved meanwhile is reverted.
/// </summary>
public sealed class WelcomeChoices
{
    private WelcomeValues _saved;

    public WelcomeChoices(AppSettings saved)
    {
        _saved = WelcomeValues.Of(saved);
        Shown = _saved;
    }

    /// <summary>The values the page shows.</summary>
    public WelcomeValues Shown { get; private set; }

    /// <summary>True when the page shows a theme choice other than the saved one: it is being previewed.</summary>
    public bool PreviewsTheme => Shown.Theme != _saved.Theme;

    /// <summary>Records what the user picked on the page.</summary>
    public void Choose(WelcomeValues shown) => Shown = shown;

    /// <summary>Takes settings saved elsewhere: a setting that changed replaces the choice made
    /// over it; the user's other choices stay.</summary>
    public void Refresh(AppSettings saved)
    {
        var now = WelcomeValues.Of(saved);
        T Keep<T>(Func<WelcomeValues, T> field) =>
            EqualityComparer<T>.Default.Equals(field(now), field(_saved)) ? field(Shown) : field(now);
        Shown = new WelcomeValues(
            Keep(values => values.Appearance),
            Keep(values => values.LightTheme),
            Keep(values => values.DarkTheme),
            Keep(values => values.ConfirmCloseActiveSessions),
            Keep(values => values.CopyOnSelect),
            Keep(values => values.RightClickPaste),
            Keep(values => values.WriteCrashReports),
            Keep(values => values.KeepCommandHistory));
        _saved = now;
    }

    /// <summary>Whether Finish has anything to save.</summary>
    public bool HasChanges(AppSettings saved) =>
        saved.OnboardingCompleted != true || Shown != WelcomeValues.Of(saved);

    /// <summary><paramref name="saved"/> with the choices made on the page, and Welcome done.
    /// Only what the user changed here is written.</summary>
    public AppSettings Complete(AppSettings saved)
    {
        Refresh(saved);
        return saved with
        {
            Appearance = Shown.Appearance,
            LightTheme = Shown.LightTheme,
            DarkTheme = Shown.DarkTheme,
            ConfirmCloseActiveSessions = Shown.ConfirmCloseActiveSessions,
            CopyOnSelect = Shown.CopyOnSelect,
            RightClickPaste = Shown.RightClickPaste,
            WriteCrashReports = Shown.WriteCrashReports,
            KeepCommandHistory = Shown.KeepCommandHistory,
            OnboardingCompleted = true,
        };
    }
}
