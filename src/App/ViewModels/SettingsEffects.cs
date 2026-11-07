using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>What a window redoes after the saved settings change.</summary>
[Flags]
public enum SettingsEffect
{
    None = 0,

    /// <summary>The app theme: chrome, tabs and terminals recolor.</summary>
    Theme = 1 << 0,

    /// <summary>The status bar shows or hides.</summary>
    StatusBar = 1 << 1,

    /// <summary>The sessions pane re-decides whether it floats or docks.</summary>
    PaneLayout = 1 << 2,

    /// <summary>A Recordings list on screen reads the folder again.</summary>
    Recordings = 1 << 3,

    /// <summary>Open terminals and players re-read their options, and tabs their agent visuals.</summary>
    Terminals = 1 << 4,

    /// <summary>The Recent list re-reads the saved order.</summary>
    RecentSessions = 1 << 5,

    /// <summary>Settings and Welcome pages re-read the values they show.</summary>
    SettingsPages = 1 << 6,
}

/// <summary>
/// Which settings change what a window shows. Every window follows
/// <see cref="SettingsStore.Changed"/> and applies <see cref="Between"/>, so a setting saved
/// anywhere (Settings in another window, a paste prompt, Welcome, a backup restore) reaches
/// every window without the writer knowing who reads it. A new setting needs one row here;
/// <c>SettingsEffectsTests</c> fails until it has one.
/// </summary>
public static class SettingsEffects
{
    private sealed record Row(string Name, Func<AppSettings, AppSettings, bool> Differs, SettingsEffect Effect);

    private static Row Value<T>(string name, Func<AppSettings, T> read, SettingsEffect effect) =>
        new(name, (before, after) => !EqualityComparer<T>.Default.Equals(read(before), read(after)), effect);

    private static Row List(string name, Func<AppSettings, IReadOnlyList<Guid>> read, SettingsEffect effect) =>
        new(name, (before, after) => !read(before).SequenceEqual(read(after)), effect);

    private static readonly Row[] Rows =
    [
        // Window state: each window keeps its own, saved as it changes; others need nothing.
        Value(nameof(AppSettings.WindowPlacement), s => s.WindowPlacement, SettingsEffect.None),
        Value(nameof(AppSettings.TreePaneWidth), s => s.TreePaneWidth, SettingsEffect.None),
        Value(nameof(AppSettings.FilePaneWidth), s => s.FilePaneWidth, SettingsEffect.None),
        Value(nameof(AppSettings.SessionsPaneOpen), s => s.SessionsPaneOpen, SettingsEffect.None),
        Value(nameof(AppSettings.SessionsRailTab), s => s.SessionsRailTab, SettingsEffect.None),
        // Read when the app starts or a tab opens, or by the jump list (which follows the store itself).
        List(nameof(AppSettings.PinnedSessionIds), s => s.PinnedSessionIds, SettingsEffect.None),
        Value(nameof(AppSettings.OnboardingCompleted), s => s.OnboardingCompleted, SettingsEffect.None),
        Value(nameof(AppSettings.DefaultLocalProfileId), s => s.DefaultLocalProfileId, SettingsEffect.None),
        List(nameof(AppSettings.RecentSessionIds), s => s.RecentSessionIds, SettingsEffect.RecentSessions),

        Value(nameof(AppSettings.Appearance), s => s.Appearance, SettingsEffect.Theme),
        Value(nameof(AppSettings.LightTheme), s => s.LightTheme, SettingsEffect.Theme),
        Value(nameof(AppSettings.DarkTheme), s => s.DarkTheme, SettingsEffect.Theme),
        Value(nameof(AppSettings.ThemeColors), s => s.ThemeColors, SettingsEffect.Theme),
        Value(nameof(AppSettings.ShowStatusBar), s => s.ShowStatusBar, SettingsEffect.StatusBar),
        Value(nameof(AppSettings.SessionsPaneLayout), s => s.SessionsPaneLayout, SettingsEffect.PaneLayout),
        Value(nameof(AppSettings.RecordingDirectory), s => s.RecordingDirectory, SettingsEffect.Recordings),

        // Options a running terminal takes at once (TerminalTabView.ApplyNonThemeSettings).
        Value(nameof(AppSettings.FontFamily), s => s.FontFamily, SettingsEffect.Terminals),
        Value(nameof(AppSettings.FontSize), s => s.FontSize, SettingsEffect.Terminals),
        Value(nameof(AppSettings.Scrollback), s => s.Scrollback, SettingsEffect.Terminals),
        Value(nameof(AppSettings.CopyOnSelect), s => s.CopyOnSelect, SettingsEffect.Terminals),
        Value(nameof(AppSettings.RightClickPaste), s => s.RightClickPaste, SettingsEffect.Terminals),
        Value(nameof(AppSettings.MouseCapture), s => s.MouseCapture, SettingsEffect.Terminals),
        Value(nameof(AppSettings.KeepCommandHistory), s => s.KeepCommandHistory, SettingsEffect.Terminals),
        Value(nameof(AppSettings.RewindMinutes), s => s.RewindMinutes, SettingsEffect.Terminals),
        Value(nameof(AppSettings.RewindMegabytes), s => s.RewindMegabytes, SettingsEffect.Terminals),
        // The tab's agent icon and badge are drawn only while this is on.
        Value(nameof(AppSettings.ShowAgentIcons), s => s.ShowAgentIcons, SettingsEffect.Terminals),

        // Read where they are used: the next close, paste, connect, recording, crash or alert.
        Value(nameof(AppSettings.ReopenLastLayoutAtStartup), s => s.ReopenLastLayoutAtStartup, SettingsEffect.None),
        Value(nameof(AppSettings.ConfirmCloseActiveSessions), s => s.ConfirmCloseActiveSessions, SettingsEffect.None),
        Value(nameof(AppSettings.WriteCrashReports), s => s.WriteCrashReports, SettingsEffect.None),
        Value(nameof(AppSettings.ConfirmMultilinePaste), s => s.ConfirmMultilinePaste, SettingsEffect.None),
        Value(nameof(AppSettings.SshAlgorithms), s => s.SshAlgorithms, SettingsEffect.None),
        Value(nameof(AppSettings.AlwaysRecord), s => s.AlwaysRecord, SettingsEffect.None),
        Value(nameof(AppSettings.CommandHistoryDays), s => s.CommandHistoryDays, SettingsEffect.None),
        Value(nameof(AppSettings.AgentAlertFlash), s => s.AgentAlertFlash, SettingsEffect.None),
        Value(nameof(AppSettings.AgentAlertSound), s => s.AgentAlertSound, SettingsEffect.None),
    ];

    /// <summary>The settings this table describes, for the test that keeps it complete.</summary>
    public static IEnumerable<string> Described => Rows.Select(row => row.Name);

    /// <summary>What a window redoes when the saved settings go from
    /// <paramref name="before"/> to <paramref name="after"/>. A change to anything the
    /// Settings page shows also refreshes Settings pages, whoever saved it.</summary>
    public static SettingsEffect Between(AppSettings before, AppSettings after)
    {
        var effect = SettingsEffect.None;
        foreach (var row in Rows)
        {
            if (!row.Differs(before, after))
                continue;
            effect |= row.Effect;
            if (!SettingsCatalog.NotInSettings.ContainsKey(row.Name))
                effect |= SettingsEffect.SettingsPages;
        }
        return effect;
    }

    /// <summary>Everything, for a window that has to apply the settings from scratch.</summary>
    public const SettingsEffect All = SettingsEffect.Theme | SettingsEffect.StatusBar | SettingsEffect.PaneLayout
        | SettingsEffect.Recordings | SettingsEffect.Terminals | SettingsEffect.RecentSessions
        | SettingsEffect.SettingsPages;
}
