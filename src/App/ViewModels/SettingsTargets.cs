namespace Resesh.App.ViewModels;

/// <summary>The Settings field to focus when Settings opens from a command.</summary>
public enum GlobalSettingsTarget
{
    General,
    Theme,
    FontFamily,
    FontSize,
    Scrollback,
    CopyOnSelect,
    RightClickPaste,
    ShowStatusBar,
    SessionsPaneLayout,
    ReopenLastLayout,
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
}

/// <summary>The session editor field to focus when a session's settings open from a command.</summary>
public enum SessionSettingsTarget
{
    General,
    Theme,
    FontFamily,
    FontSize,
    Scrollback,
    AlwaysRecord,
    CommandHistory,
}
