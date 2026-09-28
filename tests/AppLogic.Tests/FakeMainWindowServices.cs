using Resesh.App.ViewModels;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

/// <summary>Records what commands ask the window to do and answers prompts from a script.</summary>
internal sealed class FakeMainWindowServices : IMainWindowServices
{
    public List<string> Calls { get; } = [];
    public HashSet<TabViewModel> Terminals { get; } = [];
    public List<(string Title, string Message)> Confirmations { get; } = [];
    public bool ConfirmAnswer { get; set; } = true;
    public Queue<string?> PromptAnswers { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string primaryText = "Delete", bool acceptY = false)
    {
        Confirmations.Add((title, message));
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> PromptAsync(string title, string placeholder, string initial) =>
        Task.FromResult(PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : null);

    private void Record(string call) => Calls.Add(call);

    private Task RecordAsync(string call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }

    public void ConnectSession(Session session) => Record($"Connect {session.Name}");
    public void ConnectInNewWindow(IReadOnlyList<Session> sessions) =>
        Record($"ConnectInNewWindow {string.Join(",", sessions.Select(s => s.Name))}");
    public void OpenDefaultLocalProfile() => Record("OpenDefaultLocalProfile");
    public Task EditSessionAsync(Session? existing, string defaultFolder, SessionKind newKind = SessionKind.Ssh) =>
        RecordAsync($"EditSession {existing?.Name ?? "(new " + newKind + ")"} in '{defaultFolder}'");
    public Task EditLocalProfileAsync(Session? existing, string defaultFolder) =>
        RecordAsync($"EditLocalProfile {existing?.Name ?? "(new)"} in '{defaultFolder}'");
    public Task EditSessionSettingsAsync(TabViewModel tab, SessionSettingsTarget target) =>
        RecordAsync($"EditSessionSettings {tab.Session.Name} {target}");
    public Task ImportSessionsAsync() => RecordAsync("ImportSessions");
    public Task ExportBackupAsync() => RecordAsync("ExportBackup");
    public Task ImportBackupAsync() => RecordAsync("ImportBackup");

    public bool HasTerminal(TabViewModel tab) => Terminals.Contains(tab);
    public Task RequestCloseTabAsync(TabViewModel tab) => RecordAsync($"Close {tab.Header}");
    public void ReconnectTab(TabViewModel tab) => Record($"Reconnect {tab.Header}");
    public void DisconnectTab(TabViewModel tab) => Record($"Disconnect {tab.Header}");
    public void SendBreak(TabViewModel tab) => Record($"SendBreak {tab.Header}");
    public Task ManageRemoteSessionsAsync(TabViewModel tab) => RecordAsync($"ManageRemote {tab.Header}");
    public Task EndRemoteSessionAsync(TabViewModel tab) => RecordAsync($"EndRemote {tab.Header}");
    public void CloneSession(TabViewModel tab) => Record($"Clone {tab.Header}");
    public void TogglePin(TabViewModel tab) => Record($"TogglePin {tab.Header}");
    public void SplitRight(TabViewModel tab) => Record($"SplitRight {tab.Header}");
    public void SplitDown(TabViewModel tab) => Record($"SplitDown {tab.Header}");
    public void MoveTab(TabViewModel tab, int delta) => Record($"MoveTab {tab.Header} {delta}");
    public void ToggleFilePane(TabViewModel tab) => Record($"ToggleFilePane {tab.Header}");
    public bool IsFilePaneOpen(TabViewModel tab) => false;
    public void ToggleCommandsPanel(TabViewModel tab) => Record($"ToggleCommandsPanel {tab.Header}");
    public bool IsCommandsPanelOpen(TabViewModel tab) => false;

    public string? PickedRecording { get; set; }
    public Task<string?> PickRecordingFileAsync() => Task.FromResult(PickedRecording);
    public Task OpenRecordingPathAsync(string path) => RecordAsync($"OpenRecording {path}");
    public Task OpenRecordingsLocationAsync() => RecordAsync("OpenRecordingsLocation");

    public List<Workspace> WorkspaceList { get; } = [];
    public IReadOnlyList<Workspace> Workspaces => WorkspaceList;
    public Task SaveCurrentWorkspaceAsAsync() => RecordAsync("SaveWorkspaceAs");
    public Task OpenWorkspaceAsync(Workspace workspace, bool additive) =>
        RecordAsync($"OpenWorkspace {workspace.Name} additive={additive}");
    public void OpenWorkspaceInNewWindow(Workspace workspace) => Record($"OpenWorkspaceInNewWindow {workspace.Name}");
    public Task UpdateWorkspaceAsync(Workspace workspace) => RecordAsync($"UpdateWorkspace {workspace.Name}");
    public Task RenameWorkspaceAsync(Workspace workspace) => RecordAsync($"RenameWorkspace {workspace.Name}");
    public Task DeleteWorkspaceAsync(Workspace workspace) => RecordAsync($"DeleteWorkspace {workspace.Name}");

    public void OpenNewWindow() => Record("OpenNewWindow");
    public void CloseWindow() => Record("CloseWindow");
    public Task ShowSettingsAsync(GlobalSettingsTarget target) => RecordAsync($"ShowSettings {target}");
    public Task ShowSshKeysAsync() => RecordAsync("ShowSshKeys");
    public Task ShowKeyboardShortcutsAsync() => RecordAsync("ShowKeyboardShortcuts");
    public void ShowCommandPalette() => Record("ShowCommandPalette");
    public void ShowHistory(Guid? sessionId) => Record(sessionId is null ? "ShowHistory" : "ShowSessionHistory");
    public void OpenWelcome() => Record("OpenWelcome");
    public void FocusQuickConnect() => Record("FocusQuickConnect");
    public void FocusSessionFilter() => Record("FocusSessionFilter");

    public bool IsFullScreen { get; set; }
    public void ToggleFullScreen() => IsFullScreen = !IsFullScreen;
    public bool IsSessionsPaneOpen { get; set; } = true;
    public void SetSessionsPaneOpen(bool open) => IsSessionsPaneOpen = open;
    public void ShowRailTab(string tab) => Record($"ShowRailTab {tab}");
    public bool IsStatusBarVisible { get; set; } = true;
    public void SetStatusBarVisible(bool visible) => IsStatusBarVisible = visible;
    public void SyncTreeExpansion() => Record("SyncTreeExpansion");
}
