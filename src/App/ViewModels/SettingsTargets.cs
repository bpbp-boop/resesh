using Resesh.Core.Input;
using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>The Settings field to focus when Settings opens from a command. A target named
/// after a <see cref="SettingsSection"/> opens that section; every other target is a field,
/// and <see cref="SettingsCatalog.Targets"/> says where it is and how the palette lists it.</summary>
public enum GlobalSettingsTarget
{
    General,
    Theme,
    Appearance,
    LightTheme,
    DarkTheme,
    ThemeColors,
    FontFamily,
    FallbackFonts,
    FontSize,
    Scrollback,
    CopyOnSelect,
    RightClickPaste,
    MouseCapture,
    ConfirmMultilinePaste,
    ShowStatusBar,
    SessionsPaneLayout,
    ReopenLastLayout,
    LaunchAtSignIn,
    ConfirmCloseActiveSessions,
    WriteCrashReports,
    Recording,
    RecordingDirectory,
    AlwaysRecord,
    RewindMinutes,
    RewindMegabytes,
    CommandHistory,
    CommandHistoryDays,
    Highlighting,
    Agents,
    ShowAgentIcons,
    AgentAlertFlash,
    AgentAlertSound,
    Identities,
    SshAlgorithms,
    Sessions,
    About,
}

/// <summary>The Settings page's sections, in the order its navigation lists them.</summary>
public enum SettingsSection
{
    General,
    Theme,
    Sessions,
    Identities,
    Recording,
    Highlighting,
    Agents,
    Shortcuts,
    About,
}

/// <summary>A place Settings can open at: its section, the saved setting it edits (null for a
/// whole section, or one kept outside settings.json), and its command palette entry.</summary>
public sealed record GlobalSettingsEntry(
    GlobalSettingsTarget Target,
    SettingsSection Section,
    string? Setting,
    string PaletteTitle,
    string Keywords,
    string? ShortcutId = null);

/// <summary>
/// The one table of where things are in Settings. The page opens sections and fields from it,
/// the command palette lists it, and text that sends people to Settings names sections through
/// <see cref="PathTo"/>. Adding a setting means a row here and a field on the page;
/// SettingsCatalogTests fails until both exist, or until the setting is listed in
/// <see cref="NotInSettings"/> with the reason it has no field.
/// </summary>
public static class SettingsCatalog
{
    public const string Separator = " › ";

    public static string SectionTitle(SettingsSection section) => section switch
    {
        SettingsSection.Theme => "Theme",
        SettingsSection.Sessions => "Sessions",
        SettingsSection.Identities => "Identities & Keys",
        SettingsSection.Recording => "Recording",
        SettingsSection.Highlighting => "Highlighting",
        SettingsSection.Agents => "Agents",
        SettingsSection.Shortcuts => "Keyboard Shortcuts",
        SettingsSection.About => "About",
        _ => "General",
    };

    /// <summary>How text points at a place in Settings: "Settings › Identities &amp; Keys", with
    /// <paramref name="group"/> after it when given.</summary>
    public static string PathTo(SettingsSection section, string? group = null) =>
        "Settings" + Separator + SectionTitle(section) + (group is null ? "" : Separator + group);

    /// <summary>The section holding <paramref name="target"/>.</summary>
    public static string PathTo(GlobalSettingsTarget target) => PathTo(Of(target).Section);

    /// <summary>Every target, in command palette order.</summary>
    public static IReadOnlyList<GlobalSettingsEntry> Targets { get; } =
    [
        new(GlobalSettingsTarget.General, SettingsSection.General, null, "Open Settings", "preferences options", ShortcutIds.Settings),
        new(GlobalSettingsTarget.Sessions, SettingsSection.Sessions, null, "Manage Sessions", "bulk edit move duplicate delete identity sort filter"),
        new(GlobalSettingsTarget.Theme, SettingsSection.Theme, null, "Theme Settings", "appearance color scheme palette colors"),
        new(GlobalSettingsTarget.Appearance, SettingsSection.Theme, nameof(AppSettings.Appearance), "Appearance (Light, Dark or Windows)", "theme mode light dark system windows color"),
        new(GlobalSettingsTarget.LightTheme, SettingsSection.Theme, nameof(AppSettings.LightTheme), "Light Theme", "color scheme palette light mode appearance"),
        new(GlobalSettingsTarget.DarkTheme, SettingsSection.Theme, nameof(AppSettings.DarkTheme), "Dark Theme", "color scheme palette dark mode appearance"),
        new(GlobalSettingsTarget.ThemeColors, SettingsSection.Theme, nameof(AppSettings.ThemeColors), "Color the App With the Theme", "accent color menus text controls icons windows blue everything"),
        new(GlobalSettingsTarget.FontFamily, SettingsSection.General, nameof(AppSettings.FontFamily), "Terminal Font Family", "appearance typeface"),
        new(GlobalSettingsTarget.FallbackFonts, SettingsSection.General, nameof(AppSettings.FontFamily), "Fallback Fonts", "appearance typeface nerd font icons glyphs missing characters"),
        new(GlobalSettingsTarget.ShowStatusBar, SettingsSection.General, nameof(AppSettings.ShowStatusBar), "Status Bar", "appearance interface bottom chrome"),
        new(GlobalSettingsTarget.SessionsPaneLayout, SettingsSection.General, nameof(AppSettings.SessionsPaneLayout), "Sessions Pane Layout", "sidebar float overlay flyout dock narrow window"),
        new(GlobalSettingsTarget.FontSize, SettingsSection.General, nameof(AppSettings.FontSize), "Font Size", "appearance terminal text"),
        new(GlobalSettingsTarget.Scrollback, SettingsSection.General, nameof(AppSettings.Scrollback), "Scrollback Lines", "terminal history buffer"),
        new(GlobalSettingsTarget.CopyOnSelect, SettingsSection.General, nameof(AppSettings.CopyOnSelect), "Copy Selected Text", "clipboard copy on select"),
        new(GlobalSettingsTarget.RightClickPaste, SettingsSection.General, nameof(AppSettings.RightClickPaste), "Paste With Right-Click", "clipboard mouse"),
        new(GlobalSettingsTarget.MouseCapture, SettingsSection.General, nameof(AppSettings.MouseCapture), "Mouse in Programs", "mouse tracking reporting capture select copy shift claude codex tui vim"),
        new(GlobalSettingsTarget.Identities, SettingsSection.Identities, null, "Identities & Keys", "ssh keys login username password identity default folder"),
        new(GlobalSettingsTarget.SshAlgorithms, SettingsSection.Identities, nameof(AppSettings.SshAlgorithms), "SSH Algorithms", "kex key exchange cipher mac host key legacy security harden"),
        new(GlobalSettingsTarget.ConfirmMultilinePaste, SettingsSection.General, nameof(AppSettings.ConfirmMultilinePaste), "Confirm Multi-Line Pastes", "clipboard paste warning safe lines enter"),
        new(GlobalSettingsTarget.ReopenLastLayout, SettingsSection.General, nameof(AppSettings.ReopenLastLayoutAtStartup), "Reopen Last Layout at Startup", "workspace launch restore groups"),
        new(GlobalSettingsTarget.LaunchAtSignIn, SettingsSection.General, null, "Start resesh at Sign-In", "startup login boot windows launch automatically"),
        new(GlobalSettingsTarget.ConfirmCloseActiveSessions, SettingsSection.General, nameof(AppSettings.ConfirmCloseActiveSessions), "Confirm Before Closing Active Sessions", "close exit quit warning prompt running connected"),
        new(GlobalSettingsTarget.WriteCrashReports, SettingsSection.General, nameof(AppSettings.WriteCrashReports), "Crash Reports", "errors log diagnostics privacy"),
        new(GlobalSettingsTarget.Recording, SettingsSection.Recording, null, "Recording Settings", "record rewind history disk capture"),
        new(GlobalSettingsTarget.CommandHistory, SettingsSection.Recording, nameof(AppSettings.KeepCommandHistory), "Command History", "keep save commands output search retention"),
        new(GlobalSettingsTarget.CommandHistoryDays, SettingsSection.Recording, nameof(AppSettings.CommandHistoryDays), "Command History Retention", "days keep delete prune age commands"),
        new(GlobalSettingsTarget.AlwaysRecord, SettingsSection.Recording, nameof(AppSettings.AlwaysRecord), "Automatic Recording", "record sessions disk"),
        new(GlobalSettingsTarget.RecordingDirectory, SettingsSection.Recording, nameof(AppSettings.RecordingDirectory), "Recording Directory", "record sessions path folder"),
        new(GlobalSettingsTarget.RewindMinutes, SettingsSection.Recording, nameof(AppSettings.RewindMinutes), "Rewind History", "minutes terminal capture"),
        new(GlobalSettingsTarget.RewindMegabytes, SettingsSection.Recording, nameof(AppSettings.RewindMegabytes), "Rewind Memory Limit", "megabytes terminal capture"),
        new(GlobalSettingsTarget.Highlighting, SettingsSection.Highlighting, null, "Highlighting", "rules regex colors"),
        new(GlobalSettingsTarget.Agents, SettingsSection.Agents, null, "Agent Settings", "coding agents adapters claude codex status"),
        new(GlobalSettingsTarget.ShowAgentIcons, SettingsSection.Agents, nameof(AppSettings.ShowAgentIcons), "Agent Display", "icons tab coding agents"),
        new(GlobalSettingsTarget.AgentAlertFlash, SettingsSection.Agents, nameof(AppSettings.AgentAlertFlash), "Agent Taskbar Alerts", "flash notification"),
        new(GlobalSettingsTarget.AgentAlertSound, SettingsSection.Agents, nameof(AppSettings.AgentAlertSound), "Agent Notification Sound", "alert audio"),
        new(GlobalSettingsTarget.About, SettingsSection.About, null, "About resesh", "version build license credits help report bug"),
    ];

    public static GlobalSettingsEntry Of(GlobalSettingsTarget target) => Targets.First(entry => entry.Target == target);

    /// <summary>Saved settings with no field in Settings, and why.</summary>
    public static IReadOnlyDictionary<string, string> NotInSettings { get; } = new Dictionary<string, string>
    {
        [nameof(AppSettings.WindowPlacement)] = "Window state, saved as the window moves and resizes.",
        [nameof(AppSettings.TreePaneWidth)] = "Layout, saved as the sessions pane is resized.",
        [nameof(AppSettings.FilePaneWidth)] = "Layout, saved as a file pane is resized.",
        [nameof(AppSettings.PinnedSessionIds)] = "Pinned from the tab strip.",
        [nameof(AppSettings.RecentSessionIds)] = "Kept as sessions are opened.",
        [nameof(AppSettings.SessionsPaneOpen)] = "Window state, from the sessions rail.",
        [nameof(AppSettings.SessionsRailTab)] = "Window state, from the sessions rail.",
        [nameof(AppSettings.OnboardingCompleted)] = "Set by finishing Welcome.",
        [nameof(AppSettings.DefaultLocalProfileId)] = "Chosen in the local profile editor (Make default).",
        [nameof(AppSettings.LegacyTheme)] = "Read from older settings files and moved into Appearance, LightTheme and DarkTheme.",
    };
}

/// <summary>The session editor field to focus when a session's settings open from a command.</summary>
public enum SessionSettingsTarget
{
    General,
    Theme,
    FontFamily,
    FontSize,
    Scrollback,
    MouseCapture,
    AlwaysRecord,
    CommandHistory,
}
