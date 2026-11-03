using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>The settings Welcome offers, as one value.</summary>
public sealed record WelcomeValues(
    string Theme,
    bool ConfirmCloseActiveSessions,
    bool CopyOnSelect,
    bool RightClickPaste,
    bool WriteCrashReports,
    bool KeepCommandHistory)
{
    public static WelcomeValues Of(AppSettings settings) => new(
        settings.Theme,
        settings.ConfirmCloseActiveSessions,
        settings.CopyOnSelect,
        settings.RightClickPaste,
        settings.WriteCrashReports,
        settings.KeepCommandHistory);
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

    /// <summary>True when the page shows a theme other than the saved one: it is being previewed.</summary>
    public bool PreviewsTheme => !SameTheme(Shown.Theme, _saved.Theme);

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
            SameTheme(now.Theme, _saved.Theme) ? Shown.Theme : now.Theme,
            Keep(values => values.ConfirmCloseActiveSessions),
            Keep(values => values.CopyOnSelect),
            Keep(values => values.RightClickPaste),
            Keep(values => values.WriteCrashReports),
            Keep(values => values.KeepCommandHistory));
        _saved = now;
    }

    /// <summary>Whether Finish has anything to save.</summary>
    public bool HasChanges(AppSettings saved) =>
        saved.OnboardingCompleted != true || !Same(Shown, WelcomeValues.Of(saved));

    /// <summary><paramref name="saved"/> with the choices made on the page, and Welcome done.
    /// Only what the user changed here is written.</summary>
    public AppSettings Complete(AppSettings saved)
    {
        Refresh(saved);
        return saved with
        {
            Theme = Shown.Theme,
            ConfirmCloseActiveSessions = Shown.ConfirmCloseActiveSessions,
            CopyOnSelect = Shown.CopyOnSelect,
            RightClickPaste = Shown.RightClickPaste,
            WriteCrashReports = Shown.WriteCrashReports,
            KeepCommandHistory = Shown.KeepCommandHistory,
            OnboardingCompleted = true,
        };
    }

    private static bool Same(WelcomeValues first, WelcomeValues second) =>
        first with { Theme = second.Theme } == second && SameTheme(first.Theme, second.Theme);

    private static bool SameTheme(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
