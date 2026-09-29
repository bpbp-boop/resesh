using CommunityToolkit.Mvvm.Input;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

// Window, view, tab, and workspace commands. Menus, the command palette, and keyboard
// shortcuts all invoke these, so each command's CanExecute is the one availability rule.
// Tab commands take an optional tab: null means the active tab; a terminal-forwarded
// shortcut passes the tab whose terminal had focus.
public sealed partial class MainViewModel
{
    // ---- Active tab presentation (the Session menu adapts to the active tab's kind) ----

    public string ActiveStartAgainVerb => ActiveTab?.Capabilities.StartAgainVerb ?? "Reconnect";

    public string ActiveStopVerb => ActiveTab?.Capabilities.StopVerb ?? "Disconnect";

    public bool ShowSendBreak => ActiveTab is { IsPlayback: false } tab && tab.Capabilities.SendBreak;

    public bool ShowManageRemoteSessions => ActiveTab is { IsPlayback: false, IsAppPage: false } tab
        && tab.Capabilities.RemoteSession && tab.Session.Persistent;

    public bool ShowEndRemoteSession => ActiveTab is not { } tab || tab.Capabilities.RemoteSession;

    public string PinTabTitle => ActiveTab?.IsPinned == true ? "Unpin Tab" : "Pin Tab";

    public string CloseTabTitle => ActiveTab is { IsAppPage: true } page ? $"Close {page.Header}" : "Close Tab";

    private IEnumerable<IRelayCommand> TabCommands =>
    [
        ReconnectCommand, DisconnectCommand, SendBreakCommand, ManageRemoteSessionsCommand,
        EndRemoteSessionCommand, CloneTabCommand, TogglePinCommand, EditSessionSettingsCommand,
        CloseTabCommand, SplitRightCommand, SplitDownCommand, MoveTabLeftCommand, MoveTabRightCommand,
        ToggleFilePaneCommand, ToggleCommandsPanelCommand, ToggleCompletionNotificationCommand,
        ShowSessionHistoryCommand,
    ];

    /// <summary>Re-evaluates everything that follows the active tab: menu text and visibility,
    /// and every tab command's availability.</summary>
    private void OnActiveTabStateChanged()
    {
        OnPropertyChanged(nameof(ActiveStartAgainVerb));
        OnPropertyChanged(nameof(ActiveStopVerb));
        OnPropertyChanged(nameof(ShowSendBreak));
        OnPropertyChanged(nameof(ShowManageRemoteSessions));
        OnPropertyChanged(nameof(ShowEndRemoteSession));
        OnPropertyChanged(nameof(PinTabTitle));
        OnPropertyChanged(nameof(CloseTabTitle));
        foreach (var command in TabCommands)
            command.NotifyCanExecuteChanged();
    }

    private TabViewModel? Target(TabViewModel? tab) => tab ?? ActiveTab;

    private bool HasTerminal(TabViewModel tab) => _services?.HasTerminal(tab) == true;

    private static bool IsStopped(TabViewModel tab) =>
        tab.State is TabConnectionState.Disconnected or TabConnectionState.Exited;

    // ---- Tab ----

    [RelayCommand(CanExecute = nameof(CanReconnect))]
    private void Reconnect(TabViewModel? tab) => Services.ReconnectTab(Target(tab)!);

    private bool CanReconnect(TabViewModel? tab) =>
        Target(tab) is { } target && IsStopped(target) && HasTerminal(target);

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect(TabViewModel? tab) => Services.DisconnectTab(Target(tab)!);

    private bool CanDisconnect(TabViewModel? tab) =>
        Target(tab) is { State: TabConnectionState.Connected } target && HasTerminal(target);

    [RelayCommand(CanExecute = nameof(CanSendBreak))]
    private void SendBreak(TabViewModel? tab) => Services.SendBreak(Target(tab)!);

    private bool CanSendBreak(TabViewModel? tab) =>
        Target(tab) is { State: TabConnectionState.Connected, IsLocked: false } target
        && target.Capabilities.SendBreak && HasTerminal(target);

    [RelayCommand(CanExecute = nameof(CanManageRemoteSessions),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task ManageRemoteSessionsAsync(TabViewModel? tab) => Services.ManageRemoteSessionsAsync(Target(tab)!);

    private bool CanManageRemoteSessions(TabViewModel? tab) =>
        Target(tab) is { IsLocked: false, IsPlayback: false, IsAppPage: false } target
        && target.Capabilities.RemoteSession && target.Session.Persistent
        && target.State != TabConnectionState.Connecting && HasTerminal(target);

    [RelayCommand(CanExecute = nameof(CanEndRemoteSession),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task EndRemoteSessionAsync(TabViewModel? tab) => Services.EndRemoteSessionAsync(Target(tab)!);

    private bool CanEndRemoteSession(TabViewModel? tab) =>
        Target(tab) is { State: TabConnectionState.Connected } target
        && target.Capabilities.RemoteSession && HasTerminal(target);

    [RelayCommand(CanExecute = nameof(IsSessionTab))]
    private void CloneTab(TabViewModel? tab) => Services.CloneSession(Target(tab)!);

    [RelayCommand(CanExecute = nameof(IsSessionTab))]
    private void TogglePin(TabViewModel? tab) => Services.TogglePin(Target(tab)!);

    /// <summary>A tab backed by a session: not a recording playback and not Welcome.</summary>
    private bool IsSessionTab(TabViewModel? tab) => Target(tab) is { IsPlayback: false, IsAppPage: false };

    /// <summary>Opens the active tab's saved session in its editor at the given field.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSessionSettings),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task EditSessionSettingsAsync(SessionSettingsTarget? target) =>
        Services.EditSessionSettingsAsync(ActiveTab!, target ?? SessionSettingsTarget.General);

    private bool CanEditSessionSettings(SessionSettingsTarget? target) =>
        IsSessionTab(ActiveTab) && _store.Find(ActiveTab!.Session.Id) is not null;

    [RelayCommand(CanExecute = nameof(HasTab),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task CloseTabAsync(TabViewModel? tab) => Services.RequestCloseTabAsync(Target(tab)!);

    private bool HasTab(TabViewModel? tab) => Target(tab) is not null;

    [RelayCommand(CanExecute = nameof(CanSplit))]
    private void SplitRight(TabViewModel? tab) => Services.SplitRight(Target(tab)!);

    [RelayCommand(CanExecute = nameof(CanSplit))]
    private void SplitDown(TabViewModel? tab) => Services.SplitDown(Target(tab)!);

    /// <summary>A split moves the tab into a new group, so its own group must keep one.</summary>
    private bool CanSplit(TabViewModel? tab) =>
        Target(tab) is { } target && Groups.FirstOrDefault(g => g.Tabs.Contains(target))?.Tabs.Count > 1;

    [RelayCommand(CanExecute = nameof(HasTab))]
    private void MoveTabLeft(TabViewModel? tab) => Services.MoveTab(Target(tab)!, -1);

    [RelayCommand(CanExecute = nameof(HasTab))]
    private void MoveTabRight(TabViewModel? tab) => Services.MoveTab(Target(tab)!, 1);

    [RelayCommand(CanExecute = nameof(CanToggleFilePane))]
    private void ToggleFilePane(TabViewModel? tab) => Services.ToggleFilePane(Target(tab)!);

    private bool CanToggleFilePane(TabViewModel? tab) =>
        IsUnlockedTerminal(tab) && Target(tab)!.Capabilities.FilePane;

    [RelayCommand(CanExecute = nameof(IsUnlockedTerminal))]
    private void ToggleCommandsPanel(TabViewModel? tab) => Services.ToggleCommandsPanel(Target(tab)!);

    private bool IsUnlockedTerminal(TabViewModel? tab) =>
        Target(tab) is { IsLocked: false } target && HasTerminal(target);

    [RelayCommand(CanExecute = nameof(CanToggleCompletionNotification))]
    private void ToggleCompletionNotification(TabViewModel? tab) =>
        Target(tab)!.ToggleCompletionNotificationCommand.Execute(null);

    private bool CanToggleCompletionNotification(TabViewModel? tab) =>
        IsUnlockedTerminal(tab) && Target(tab)!.CanNotifyCommandCompletion;

    [RelayCommand(CanExecute = nameof(CanShowSessionHistory))]
    private void ShowSessionHistory(TabViewModel? tab) => Services.ShowHistory(Target(tab)!.Session.Id);

    private bool CanShowSessionHistory(TabViewModel? tab) =>
        Target(tab) is { IsPlayback: false } target && HasTerminal(target);

    // ---- Application ----

    [RelayCommand]
    private void NewWindow() => Services.OpenNewWindow();

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task NewSshSessionAsync() => Services.EditSessionAsync(existing: null, defaultFolder: "");

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task NewTelnetSessionAsync() =>
        Services.EditSessionAsync(existing: null, defaultFolder: "", SessionKind.Telnet);

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task NewLocalProfileAsync() => Services.EditLocalProfileAsync(existing: null, defaultFolder: "");

    [RelayCommand]
    private void OpenDefaultLocalProfile() => Services.OpenDefaultLocalProfile();

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private async Task NewFolderAsync()
    {
        var name = await Services.PromptAsync("New Folder", "Folder name", "");
        if (!string.IsNullOrWhiteSpace(name))
            CreateFolder(name);
    }

    /// <summary>Plays a recording from the rail, or asks for one when none is given.</summary>
    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private async Task OpenRecordingAsync(RecordingItemViewModel? recording)
    {
        var path = recording?.FilePath ?? await Services.PickRecordingFileAsync();
        if (path is not null)
            await Services.OpenRecordingPathAsync(path);
    }

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task OpenRecordingsFolderAsync() => Services.OpenRecordingsLocationAsync();

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task ImportSessionsAsync() => Services.ImportSessionsAsync();

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task ExportBackupAsync() => Services.ExportBackupAsync();

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task ImportBackupAsync() => Services.ImportBackupAsync();

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task ShowSshKeysAsync() => Services.ShowSshKeysAsync();

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task OpenSettingsAsync(GlobalSettingsTarget? target) =>
        Services.ShowSettingsAsync(target ?? GlobalSettingsTarget.General);

    [RelayCommand]
    private void Exit() => Services.CloseWindow();

    // ---- View ----

    [RelayCommand]
    private void ToggleSessionsPane() => Services.SetSessionsPaneOpen(!Services.IsSessionsPaneOpen);

    [RelayCommand]
    private void ToggleStatusBar() => Services.SetStatusBarVisible(!Services.IsStatusBarVisible);

    [RelayCommand]
    private void ToggleFullScreen() => Services.ToggleFullScreen();

    [RelayCommand]
    private void ShowRailTab(string? tab) => Services.ShowRailTab(tab ?? "sessions");

    [RelayCommand]
    private void ShowCommandPalette() => Services.ShowCommandPalette();

    [RelayCommand]
    private void ShowCommandHistory() => Services.ShowHistory(sessionId: null);

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task ShowKeyboardShortcutsAsync() => Services.ShowKeyboardShortcutsAsync();

    [RelayCommand]
    private void OpenWelcome() => Services.OpenWelcome();

    [RelayCommand]
    private void FocusQuickConnect() => Services.FocusQuickConnect();

    [RelayCommand]
    private void FocusSessionFilter() => Services.FocusSessionFilter();

    [RelayCommand]
    private void ExpandAll()
    {
        SetExpansionAll(true);
        Services.SyncTreeExpansion();
    }

    [RelayCommand]
    private void CollapseAll()
    {
        SetExpansionAll(false);
        Services.SyncTreeExpansion();
    }

    // ---- Workspaces ----

    [RelayCommand(AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task SaveWorkspaceAsAsync() => Services.SaveCurrentWorkspaceAsAsync();

    [RelayCommand(CanExecute = nameof(IsWorkspace),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task OpenWorkspaceAsync(Workspace? workspace) => Services.OpenWorkspaceAsync(workspace!, additive: false);

    [RelayCommand(CanExecute = nameof(IsWorkspace),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task OpenWorkspaceAdditivelyAsync(Workspace? workspace) =>
        Services.OpenWorkspaceAsync(workspace!, additive: true);

    [RelayCommand(CanExecute = nameof(IsWorkspace))]
    private void OpenWorkspaceInNewWindow(Workspace? workspace) => Services.OpenWorkspaceInNewWindow(workspace!);

    [RelayCommand(CanExecute = nameof(IsWorkspace),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task UpdateWorkspaceAsync(Workspace? workspace) => Services.UpdateWorkspaceAsync(workspace!);

    [RelayCommand(CanExecute = nameof(IsWorkspace),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task RenameWorkspaceAsync(Workspace? workspace) => Services.RenameWorkspaceAsync(workspace!);

    [RelayCommand(CanExecute = nameof(IsWorkspace),
        AllowConcurrentExecutions = true, FlowExceptionsToTaskScheduler = true)]
    private Task DeleteWorkspaceAsync(Workspace? workspace) => Services.DeleteWorkspaceAsync(workspace!);

    private static bool IsWorkspace(Workspace? workspace) => workspace is not null;
}
