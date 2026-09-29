using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Resesh.App.Terminal;
using Resesh.App.ViewModels;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App;

// The window's side of the view model's commands. Each member forwards to the window's
// own dialog, view, or chrome code; the commands decide when these run.
public sealed partial class MainWindow
{
    // ---- Prompts ----

    Task<bool> IMainWindowServices.ConfirmAsync(string title, string message, string primaryText, bool acceptY) =>
        ConfirmAsync(title, message, primaryText, acceptY);

    Task<string?> IMainWindowServices.PromptAsync(string title, string placeholder, string initial) =>
        PromptAsync(title, placeholder, initial);

    // ---- Sessions ----

    void IMainWindowServices.ConnectSession(Session session) => ConnectSession(session);

    void IMainWindowServices.ConnectInNewWindow(IReadOnlyList<Session> sessions) => ConnectInNewWindow(sessions);

    void IMainWindowServices.OpenDefaultLocalProfile() => OpenDefaultLocalProfile();

    Task IMainWindowServices.EditSessionAsync(Session? existing, string defaultFolder, SessionKind newKind) =>
        OpenSessionEditorAsync(existing, defaultFolder, newKind);

    Task IMainWindowServices.EditLocalProfileAsync(Session? existing, string defaultFolder) =>
        OpenLocalProfileEditorAsync(existing, defaultFolder);

    Task IMainWindowServices.EditSessionSettingsAsync(TabViewModel tab, SessionSettingsTarget target) =>
        OpenSessionSettingsAsync(tab, target);

    Task IMainWindowServices.ImportSessionsAsync() => ImportSessionsAsync();

    Task IMainWindowServices.ExportBackupAsync() => ExportBackupAsync();

    Task IMainWindowServices.ImportBackupAsync() => ImportBackupAsync();

    // ---- Tabs ----

    bool IMainWindowServices.HasTerminal(TabViewModel tab) => tab.View is TerminalTabView;

    void IMainWindowServices.MoveTab(TabViewModel tab, int delta) => MoveTab(tab, delta);

    bool IMainWindowServices.IsFilePaneOpen(TabViewModel tab) => tab.View is TerminalTabView { IsFilePaneOpen: true };

    void IMainWindowServices.ToggleCommandsPanel(TabViewModel tab) => (tab.View as TerminalTabView)?.ToggleCommandsPanel();

    bool IMainWindowServices.IsCommandsPanelOpen(TabViewModel tab) =>
        tab.View is TerminalTabView { IsCommandsPanelOpen: true };

    // ---- Recordings ----

    Task<string?> IMainWindowServices.PickRecordingFileAsync() => PickRecordingFileAsync();

    Task IMainWindowServices.OpenRecordingPathAsync(string path) => OpenRecordingPathAsync(path);

    // ---- Workspaces ----

    IReadOnlyList<Workspace> IMainWindowServices.Workspaces => App.Workspaces.Workspaces;

    Task IMainWindowServices.SaveCurrentWorkspaceAsAsync() => SaveCurrentWorkspaceAsAsync();

    Task IMainWindowServices.OpenWorkspaceAsync(Workspace workspace, bool additive) =>
        OpenWorkspaceAsync(workspace, additive);

    void IMainWindowServices.OpenWorkspaceInNewWindow(Workspace workspace) => OpenWorkspaceInNewWindow(workspace);

    Task IMainWindowServices.UpdateWorkspaceAsync(Workspace workspace) => UpdateWorkspaceAsync(workspace);

    Task IMainWindowServices.RenameWorkspaceAsync(Workspace workspace) => RenameWorkspaceAsync(workspace);

    Task IMainWindowServices.DeleteWorkspaceAsync(Workspace workspace) => DeleteWorkspaceAsync(workspace);

    // ---- Window ----

    void IMainWindowServices.OpenNewWindow() => App.OpenNewWindow();

    void IMainWindowServices.CloseWindow() => Close();

    // Settings is a tab, so it keeps focus. Dialogs opened by a terminal-forwarded key hand
    // focus back to that terminal.

    Task IMainWindowServices.ShowSettingsAsync(GlobalSettingsTarget target) => ShowSettingsAsync(target);

    Task IMainWindowServices.ShowSshKeysAsync() =>
        Dialogs.SshKeyManagerDialog.ManageAsync(Root.XamlRoot, App.SshKeys, App.Store, App.Credentials);

    Task IMainWindowServices.ShowKeyboardShortcutsAsync() =>
        ShowThenRefocusAsync(ShowKeyboardShortcutsAsync, _invokedFromTerminal);

    void IMainWindowServices.ShowCommandPalette() => ShowCommandPalette(openedFromTerminal: _invokedFromTerminal);

    void IMainWindowServices.ShowHistory(Guid? sessionId) =>
        ShowHistory(sessionId, openedFromTerminal: _invokedFromTerminal);

    void IMainWindowServices.OpenWelcome() => OpenWelcome();

    void IMainWindowServices.FocusQuickConnect() => FocusQuickConnectBox();

    void IMainWindowServices.FocusSessionFilter() => FocusSessionFilter();

    bool IMainWindowServices.IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    void IMainWindowServices.ToggleFullScreen() => ToggleFullScreen();

    bool IMainWindowServices.IsSessionsPaneOpen => SessionsPaneShown;

    void IMainWindowServices.SetSessionsPaneOpen(bool open) => SetSessionsPaneOpen(open);

    void IMainWindowServices.ShowRailTab(string tab) => SelectSessionsRailTab(tab);

    bool IMainWindowServices.IsStatusBarVisible => App.Settings.Current.ShowStatusBar;

    void IMainWindowServices.SetStatusBarVisible(bool visible) => SetStatusBarVisible(visible);

    void IMainWindowServices.SyncTreeExpansion() => ScheduleExpansionSync();
}
