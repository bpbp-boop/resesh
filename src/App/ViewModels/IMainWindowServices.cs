using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>What a window does for its view model's commands: dialogs, pickers, views, and
/// window chrome. Commands decide whether and what to do; these carry it out.</summary>
public interface IMainWindowServices
{
    // ---- Prompts ----

    Task<bool> ConfirmAsync(string title, string message, string primaryText = "Delete", bool acceptY = false);

    /// <summary>Returns the entered text, or null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string placeholder, string initial);

    // ---- Sessions ----

    void ConnectSession(Session session);
    void ConnectInNewWindow(IReadOnlyList<Session> sessions);
    void OpenDefaultLocalProfile();

    /// <summary>Opens the session editor and saves the result.</summary>
    Task EditSessionAsync(Session? existing, string defaultFolder, SessionKind newKind = SessionKind.Ssh);

    /// <summary>Opens the local profile editor and saves the result.</summary>
    Task EditLocalProfileAsync(Session? existing, string defaultFolder);

    Task EditSessionSettingsAsync(TabViewModel tab, SessionSettingsTarget target);
    Task ImportSessionsAsync();
    Task ExportBackupAsync();
    Task ImportBackupAsync();

    // ---- Tabs ----

    /// <summary>True when the tab hosts a live terminal (not playback or Welcome).</summary>
    bool HasTerminal(TabViewModel tab);

    Task RequestCloseTabAsync(TabViewModel tab);
    void ReconnectTab(TabViewModel tab);
    void DisconnectTab(TabViewModel tab);
    void SendBreak(TabViewModel tab);
    Task ManageRemoteSessionsAsync(TabViewModel tab);
    Task EndRemoteSessionAsync(TabViewModel tab);
    void CloneSession(TabViewModel tab);
    void TogglePin(TabViewModel tab);
    void SplitRight(TabViewModel tab);
    void SplitDown(TabViewModel tab);
    void MoveTab(TabViewModel tab, int delta);
    void ToggleFilePane(TabViewModel tab);
    bool IsFilePaneOpen(TabViewModel tab);
    void ToggleCommandsPanel(TabViewModel tab);
    bool IsCommandsPanelOpen(TabViewModel tab);

    // ---- Recordings ----

    /// <summary>Returns the chosen recording's path, or null when cancelled.</summary>
    Task<string?> PickRecordingFileAsync();

    Task OpenRecordingPathAsync(string path);
    Task OpenRecordingsLocationAsync();

    // ---- Workspaces ----

    IReadOnlyList<Workspace> Workspaces { get; }
    Task SaveCurrentWorkspaceAsAsync();
    Task OpenWorkspaceAsync(Workspace workspace, bool additive);
    void OpenWorkspaceInNewWindow(Workspace workspace);
    Task UpdateWorkspaceAsync(Workspace workspace);
    Task RenameWorkspaceAsync(Workspace workspace);
    Task DeleteWorkspaceAsync(Workspace workspace);

    // ---- Window ----

    void OpenNewWindow();
    void CloseWindow();
    Task ShowSettingsAsync(GlobalSettingsTarget target);
    Task ShowSshKeysAsync();
    Task ShowKeyboardShortcutsAsync();
    void ShowCommandPalette();
    void ShowHistory(Guid? sessionId);
    void OpenWelcome();
    void FocusQuickConnect();
    void FocusSessionFilter();

    bool IsFullScreen { get; }
    void ToggleFullScreen();
    bool IsSessionsPaneOpen { get; }
    void SetSessionsPaneOpen(bool open);
    void ShowRailTab(string tab);
    bool IsStatusBarVisible { get; }
    void SetStatusBarVisible(bool visible);

    /// <summary>Pushes view-model folder expansion onto realized tree containers.</summary>
    void SyncTreeExpansion();
}
