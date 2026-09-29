using CommunityToolkit.Mvvm.Input;
using Resesh.Core.Input;

namespace Resesh.App.ViewModels;

/// <summary>A command as the palette lists it and a shortcut runs it. The parameter is
/// read at use time, so a descriptor can follow live state.</summary>
public sealed record CommandDescriptor(
    string Category,
    Func<string> Title,
    string Keywords,
    IRelayCommand Command,
    string? ShortcutId = null,
    Func<object?>? Parameter = null,
    bool KeepActionFocus = false,
    bool InPalette = true)
{
    public object? CurrentParameter => Parameter?.Invoke();

    public bool CanExecute() => Command.CanExecute(CurrentParameter);
}

/// <summary>The one table behind the command palette and window shortcuts. Availability
/// comes from each command's CanExecute, so the palette lists exactly what a shortcut
/// or menu would run.</summary>
public sealed class AppCommandCatalog
{
    private readonly MainViewModel _vm;
    private readonly IReadOnlyList<CommandDescriptor> _head;
    private readonly IReadOnlyList<CommandDescriptor> _tail;
    private readonly Dictionary<string, CommandDescriptor> _byShortcut;

    public AppCommandCatalog(MainViewModel vm)
    {
        _vm = vm;
        _head = BuildHead();
        _tail = BuildTail();

        // Only App- and Window-scope keys reach the window; terminal and tree keys are
        // handled where they are pressed, and their ids here only label palette entries.
        var windowScoped = KeyBindings.All
            .Where(binding => binding.Scope is ShortcutScope.App or ShortcutScope.Window)
            .Select(binding => binding.Id)
            .ToHashSet();
        _byShortcut = _head.Concat(_tail)
            .Where(descriptor => descriptor.ShortcutId is { } id && windowScoped.Contains(id))
            .ToDictionary(descriptor => descriptor.ShortcutId!);
    }

    /// <summary>The command a window shortcut runs, or null when the window handles the key itself.</summary>
    public CommandDescriptor? ForShortcut(string shortcutId) => _byShortcut.GetValueOrDefault(shortcutId);

    /// <summary>Available commands in palette order: application and view commands,
    /// settings, workspaces, then the active tab's commands.</summary>
    public IReadOnlyList<CommandDescriptor> PaletteCommands() =>
        _head.Concat(WorkspaceCommands()).Concat(_tail)
            .Where(descriptor => descriptor.InPalette && descriptor.CanExecute())
            .ToList();

    private static Func<string> Fixed(string title) => () => title;

    private IReadOnlyList<CommandDescriptor> BuildHead()
    {
        var vm = _vm;
        var commands = new List<CommandDescriptor>
        {
            new("Application", Fixed("New Window"), "open separate window",
                vm.NewWindowCommand, ShortcutIds.NewWindow),
            new("Application", Fixed("Open Default Local Terminal"), "new session shell tab",
                vm.OpenDefaultLocalProfileCommand, ShortcutIds.NewLocalTab),
            new("Application", Fixed("Quick Connect"), "ssh telnet search sessions connect",
                vm.FocusQuickConnectCommand, ShortcutIds.QuickConnect, KeepActionFocus: true),
            new("Application", Fixed("Keyboard Shortcuts"), "keys hotkeys keybindings accelerators help reference",
                vm.ShowKeyboardShortcutsCommand, ShortcutIds.KeyboardShortcuts),
            new("Application", Fixed("Search Command History"), "history commands output past previous find grep ran",
                vm.ShowCommandHistoryCommand, ShortcutIds.CommandHistory, KeepActionFocus: true),
            new("Application", Fixed("Command Palette"), "commands actions",
                vm.ShowCommandPaletteCommand, ShortcutIds.CommandPalette, InPalette: false),

            new("View", Fixed("Filter Sessions"), "search tree",
                vm.FocusSessionFilterCommand, ShortcutIds.FilterSessions, KeepActionFocus: true),
            new("View", () => vm.WindowServices.IsFullScreen ? "Exit Full Screen" : "Full Screen",
                "fullscreen maximize window", vm.ToggleFullScreenCommand, ShortcutIds.FullScreen),
            new("View", Fixed("Expand All Session Folders"), "tree folders", vm.ExpandAllCommand),
            new("View", Fixed("Collapse All Session Folders"), "tree folders", vm.CollapseAllCommand),
            new("View", () => vm.WindowServices.IsSessionsPaneOpen ? "Hide Sessions Pane" : "Show Sessions Pane",
                "sidebar rail sessions recent recordings", vm.ToggleSessionsPaneCommand, ShortcutIds.ToggleSessionsPane),
            new("View", () => vm.WindowServices.IsStatusBarVisible ? "Hide Status Bar" : "Show Status Bar",
                "bottom bar interface chrome", vm.ToggleStatusBarCommand),
            new("View", Fixed("Show Workspaces"), "sidebar rail layouts",
                vm.ShowRailTabCommand, Parameter: () => "workspaces"),
            new("View", Fixed("Show Recent Sessions"), "sidebar rail history",
                vm.ShowRailTabCommand, Parameter: () => "recent"),
            new("View", Fixed("Show Recordings"), "sidebar rail playback asciicast",
                vm.ShowRailTabCommand, Parameter: () => "recordings"),
            new("View", Fixed("Open Welcome"), "onboarding setup getting started import theme",
                vm.OpenWelcomeCommand, KeepActionFocus: true),
        };

        void Setting(string title, string keywords, GlobalSettingsTarget target, string? shortcut = null) =>
            commands.Add(new("Global Settings", Fixed(title), keywords, vm.OpenSettingsCommand, shortcut,
                Parameter: () => target));
        Setting("Open Settings", "preferences options", GlobalSettingsTarget.General, ShortcutIds.Settings);
        Setting("Theme", "appearance color scheme", GlobalSettingsTarget.Theme);
        Setting("Terminal Font Family", "appearance typeface", GlobalSettingsTarget.FontFamily);
        Setting("Status Bar", "appearance interface bottom chrome", GlobalSettingsTarget.ShowStatusBar);
        Setting("Sessions Pane Layout", "sidebar float overlay flyout dock narrow window", GlobalSettingsTarget.SessionsPaneLayout);
        Setting("Font Size", "appearance terminal text", GlobalSettingsTarget.FontSize);
        Setting("Scrollback Lines", "terminal history buffer", GlobalSettingsTarget.Scrollback);
        Setting("Copy Selected Text", "clipboard copy on select", GlobalSettingsTarget.CopyOnSelect);
        Setting("Paste With Right-Click", "clipboard mouse", GlobalSettingsTarget.RightClickPaste);
        Setting("Reopen Last Layout at Startup", "workspace launch restore groups", GlobalSettingsTarget.ReopenLastLayout);
        Setting("Command History", "keep save commands output search retention", GlobalSettingsTarget.CommandHistory);
        Setting("Automatic Recording", "record sessions disk", GlobalSettingsTarget.AlwaysRecord);
        Setting("Recording Directory", "record sessions path folder", GlobalSettingsTarget.RecordingDirectory);
        Setting("Rewind History", "minutes terminal capture", GlobalSettingsTarget.RewindMinutes);
        Setting("Rewind Memory Limit", "megabytes terminal capture", GlobalSettingsTarget.RewindMegabytes);
        Setting("Highlighting", "rules regex colors", GlobalSettingsTarget.Highlighting);
        Setting("Agent Display", "icons tab coding agents", GlobalSettingsTarget.ShowAgentIcons);
        Setting("Agent Taskbar Alerts", "flash notification", GlobalSettingsTarget.AgentAlertFlash);
        Setting("Agent Notification Sound", "alert audio", GlobalSettingsTarget.AgentAlertSound);

        commands.Add(new("Workspaces", Fixed("Save Current Layout as Workspace"), "tabs groups layout save as",
            vm.SaveWorkspaceAsCommand));
        return commands;
    }

    private IEnumerable<CommandDescriptor> WorkspaceCommands()
    {
        foreach (var workspace in _vm.WindowServices.Workspaces)
        {
            yield return new("Workspaces", Fixed($"Open {workspace.Name}"), "tabs groups layout replace",
                _vm.OpenWorkspaceCommand, Parameter: () => workspace);
            yield return new("Workspaces", Fixed($"Open {workspace.Name} in New Window"), "tabs groups layout separate",
                _vm.OpenWorkspaceInNewWindowCommand, Parameter: () => workspace);
            yield return new("Workspaces", Fixed($"Open {workspace.Name} Additively"), "tabs groups layout add",
                _vm.OpenWorkspaceAdditivelyCommand, Parameter: () => workspace);
        }
    }

    private IReadOnlyList<CommandDescriptor> BuildTail()
    {
        var vm = _vm;
        var commands = new List<CommandDescriptor>();

        void SessionSetting(string title, string keywords, SessionSettingsTarget target) =>
            commands.Add(new("Session Settings", Fixed(title), keywords, vm.EditSessionSettingsCommand,
                Parameter: () => target));
        SessionSetting("Open Session Options", "current active tab profile", SessionSettingsTarget.General);
        SessionSetting("Theme Override", "current active tab appearance inherit", SessionSettingsTarget.Theme);
        SessionSetting("Terminal Font Family Override", "current active tab appearance inherit", SessionSettingsTarget.FontFamily);
        SessionSetting("Font Size Override", "current active tab appearance inherit", SessionSettingsTarget.FontSize);
        SessionSetting("Scrollback Lines Override", "current active tab history inherit", SessionSettingsTarget.Scrollback);
        SessionSetting("Automatic Recording Override", "current active tab inherit", SessionSettingsTarget.AlwaysRecord);
        SessionSetting("Command History Override", "current active tab inherit keep", SessionSettingsTarget.CommandHistory);

        commands.AddRange(
        [
            new("Tab", () => $"{vm.ActiveStartAgainVerb} Tab", "current active session",
                vm.ReconnectCommand, ShortcutIds.ReconnectTab),
            new("Tab", () => $"{vm.ActiveStopVerb} Tab", "current active session", vm.DisconnectCommand),
            new("Tab", Fixed("Send Break"), "telnet console serial break boot interrupt rommon password recovery",
                vm.SendBreakCommand, ShortcutIds.SendBreak),
            new("Tab", Fixed("Manage Remote Sessions"), "tmux persistent shells resume end close command running age",
                vm.ManageRemoteSessionsCommand),
            new("Tab", Fixed("Clone Tab"), "duplicate copy current active session next adjacent",
                vm.CloneTabCommand, ShortcutIds.CloneTab),
            new("Tab", () => vm.PinTabTitle, "current active keep", vm.TogglePinCommand),
            new("Tab", Fixed("Split Right"), "current active move group", vm.SplitRightCommand, ShortcutIds.SplitRight),
            new("Tab", Fixed("Split Down"), "current active move group", vm.SplitDownCommand, ShortcutIds.SplitDown),
            new("Tab", Fixed("Move Tab Left"), "reorder", vm.MoveTabLeftCommand, ShortcutIds.MoveTabLeft, InPalette: false),
            new("Tab", Fixed("Move Tab Right"), "reorder", vm.MoveTabRightCommand, ShortcutIds.MoveTabRight, InPalette: false),
            new("Tab", Fixed("Search This Session's History"), "command history output past previous current active",
                vm.ShowSessionHistoryCommand, KeepActionFocus: true),
            new("Tab", () => vm.ActiveTab?.IsCompletionNotificationArmed == true
                    ? "Cancel Completion Notification" : "Notify When Command Finishes",
                "current command completion alert bell", vm.ToggleCompletionNotificationCommand),
            new("Tab", () => vm.ActiveTab is { } panelTab && vm.WindowServices.IsCommandsPanelOpen(panelTab) ? "Hide Commands Panel" : "Show Commands Panel",
                "current active terminal history", vm.ToggleCommandsPanelCommand, ShortcutIds.CommandsPanel),
            new("Tab", () => vm.ActiveTab is { } paneTab && vm.WindowServices.IsFilePaneOpen(paneTab) ? "Hide File Pane" : "Show File Pane",
                "current active files browser", vm.ToggleFilePaneCommand, ShortcutIds.FilePane),
            new("Tab", () => vm.CloseTabTitle, "current active session", vm.CloseTabCommand, ShortcutIds.CloseTab),
        ]);
        return commands;
    }
}
