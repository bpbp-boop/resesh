using Resesh.App.ViewModels;
using Resesh.Core.Credentials;
using Resesh.Core.Input;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

public sealed class MainViewModelCommandTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("resesh-command-tests-").FullName;
    private readonly FakeMainWindowServices _window = new();
    private readonly SessionStore _store;
    private IReadOnlyList<Guid> _recentIds = [];
    private string _recordingDirectory = "";

    public MainViewModelCommandTests() =>
        _store = new SessionStore(Path.Combine(_directory, "sessions.json"));

    public void Dispose() => Directory.Delete(_directory, true);

    private ViewModelEnvironment Environment() => new()
    {
        CurrentTheme = () => "dark",
        ResolveTheme = theme => theme,
        ShowAgentIcons = () => true,
        IsSessionVisible = _ => true,
        ApplySessionSettings = _ => { },
        ReportError = exception => throw exception,
        RecentSessionIds = () => _recentIds,
        SaveRecentSessionIds = ids => _recentIds = ids,
        RecordingDirectory = () => _recordingDirectory,
    };

    private MainViewModel Window() => new(_store, new Credentials(), Environment(), _window);

    private TabViewModel Open(MainViewModel window, Session session, TabConnectionState state, bool terminal = true)
    {
        var tab = window.Connect(session);
        tab.State = state;
        if (terminal)
            _window.Terminals.Add(tab);
        return tab;
    }

    private static Session Ssh(string name = "router", string folder = "") =>
        new() { Id = Guid.NewGuid(), Name = name, Host = name + ".example", FolderPath = folder };

    // ---- Tab commands ----

    [Fact]
    public void ConnectedTabOffersStopButNotReconnect()
    {
        var window = Window();
        Open(window, Ssh(), TabConnectionState.Connected);

        Assert.True(window.DisconnectCommand.CanExecute(null));
        Assert.False(window.ReconnectCommand.CanExecute(null));
        Assert.True(window.EndRemoteSessionCommand.CanExecute(null));
        Assert.True(window.CloneTabCommand.CanExecute(null));
        Assert.False(window.SendBreakCommand.CanExecute(null)); // SSH has no break
    }

    [Theory]
    [InlineData(TabConnectionState.Disconnected)]
    [InlineData(TabConnectionState.Exited)]
    public void StoppedTabOffersReconnectOnly(TabConnectionState state)
    {
        var window = Window();
        Open(window, Ssh(), state);

        Assert.True(window.ReconnectCommand.CanExecute(null));
        Assert.False(window.DisconnectCommand.CanExecute(null));
        Assert.False(window.EndRemoteSessionCommand.CanExecute(null));
    }

    [Fact]
    public void PlaybackTabCanOnlyClose()
    {
        var window = Window();
        var tab = Open(window, Ssh(), TabConnectionState.Playback, terminal: false);
        tab.PlaybackPath = "demo.cast";

        Assert.False(window.CloneTabCommand.CanExecute(null));
        Assert.False(window.TogglePinCommand.CanExecute(null));
        Assert.False(window.ReconnectCommand.CanExecute(null));
        Assert.False(window.EditSessionSettingsCommand.CanExecute(null));
        Assert.True(window.CloseTabCommand.CanExecute(null));
    }

    [Fact]
    public void WelcomeTabClosesAsWelcomeAndHasNoSessionActions()
    {
        var window = Window();
        var welcome = TabViewModel.CreateOnboarding(Environment());
        window.AttachTab(welcome, window.FocusedGroup, 0);

        Assert.Equal("Close Welcome", window.CloseTabTitle);
        Assert.False(window.CloneTabCommand.CanExecute(null));
        Assert.False(window.EditSessionSettingsCommand.CanExecute(null));
        Assert.True(window.CloseTabCommand.CanExecute(null));
    }

    [Fact]
    public void SendBreakNeedsAnUnlockedConnectedTelnetTab()
    {
        var window = Window();
        var tab = Open(window, Ssh() with { Kind = SessionKind.Telnet }, TabConnectionState.Connected);
        Assert.True(window.SendBreakCommand.CanExecute(null));

        tab.Lock("secret");
        Assert.False(window.SendBreakCommand.CanExecute(null));
    }

    [Fact]
    public void SplitNeedsASecondTabInTheGroup()
    {
        var window = Window();
        Open(window, Ssh("a"), TabConnectionState.Connected);
        Assert.False(window.SplitRightCommand.CanExecute(null));

        Open(window, Ssh("b"), TabConnectionState.Connected);
        Assert.True(window.SplitRightCommand.CanExecute(null));
        window.SplitRightCommand.Execute(null);
        Assert.Equal(["SplitRight b"], _window.Calls);
    }

    [Fact]
    public void ActiveTabStateChangeReevaluatesTabCommands()
    {
        var window = Window();
        var tab = Open(window, Ssh(), TabConnectionState.Connected);
        var raised = 0;
        window.ReconnectCommand.CanExecuteChanged += (_, _) => raised++;
        var verbs = new List<string?>();
        window.PropertyChanged += (_, e) => verbs.Add(e.PropertyName);

        tab.State = TabConnectionState.Disconnected;

        Assert.True(raised > 0);
        Assert.True(window.ReconnectCommand.CanExecute(null));
        Assert.Contains(nameof(MainViewModel.ActiveStartAgainVerb), verbs);
    }

    [Fact]
    public void LocalTabUsesStopAndRestartVerbs()
    {
        var window = Window();
        Open(window, Ssh() with { Kind = SessionKind.Local }, TabConnectionState.Connected);

        Assert.Equal("Restart", window.ActiveStartAgainVerb);
        Assert.Equal("Stop", window.ActiveStopVerb);
        Assert.False(window.ShowEndRemoteSession);
    }

    [Fact]
    public void ExplicitTabParameterOverridesTheActiveTab()
    {
        var window = Window();
        var background = Open(window, Ssh("background"), TabConnectionState.Disconnected);
        Open(window, Ssh("active"), TabConnectionState.Connected);

        Assert.False(window.ReconnectCommand.CanExecute(null));
        Assert.True(window.ReconnectCommand.CanExecute(background));
        window.ReconnectCommand.Execute(background);
        Assert.Equal(["Reconnect background"], _window.Calls);
    }

    // ---- Shortcuts and palette ----

    [Fact]
    public void ShortcutOnPlaybackTabReportsNotHandled()
    {
        var window = Window();
        var tab = Open(window, Ssh(), TabConnectionState.Playback, terminal: false);
        tab.PlaybackPath = "demo.cast";

        var clone = window.Commands.ForShortcut(ShortcutIds.CloneTab);
        Assert.NotNull(clone);
        Assert.False(clone.Command.CanExecute(tab));
    }

    [Fact]
    public void OnlyWindowScopedKeysMapToCommands()
    {
        var window = Window();

        Assert.NotNull(window.Commands.ForShortcut(ShortcutIds.Settings));
        Assert.NotNull(window.Commands.ForShortcut(ShortcutIds.MoveTabLeft));
        // Terminal keys run in the page; layout keys are handled by the window itself.
        Assert.Null(window.Commands.ForShortcut(ShortcutIds.CommandsPanel));
        Assert.Null(window.Commands.ForShortcut(ShortcutIds.NextTab));
        foreach (var binding in KeyBindings.All.Where(b => b.Scope == ShortcutScope.Terminal))
            Assert.Null(window.Commands.ForShortcut(binding.Id));
    }

    [Fact]
    public void PaletteListsOnlyRunnableCommands()
    {
        var window = Window();
        Assert.DoesNotContain(window.Commands.PaletteCommands(), d => d.Category == "Tab");

        Open(window, Ssh() with { Kind = SessionKind.Local }, TabConnectionState.Connected);
        var titles = window.Commands.PaletteCommands().Select(d => d.Title()).ToList();

        Assert.Contains("Stop Tab", titles);
        Assert.DoesNotContain("Restart Tab", titles);
        Assert.DoesNotContain("Move Tab Left", titles); // shortcut-only
        Assert.Equal("Close Tab", titles[^1]);
    }

    [Fact]
    public void PaletteSettingsEntriesOpenSettingsAtTheirField()
    {
        var window = Window();
        var theme = window.Commands.PaletteCommands().Single(d => d.Title() == "Theme");

        theme.Command.Execute(theme.CurrentParameter);

        Assert.Equal(["ShowSettings Theme"], _window.Calls);
    }

    [Fact]
    public void PaletteListsEachWorkspaceThreeWays()
    {
        var window = Window();
        _window.WorkspaceList.Add(new Workspace { Id = Guid.NewGuid(), Name = "Lab" });

        var titles = window.Commands.PaletteCommands().Where(d => d.Category == "Workspaces").Select(d => d.Title());

        Assert.Equal(
            ["Save Current Layout as Workspace", "Open Lab", "Open Lab in New Window", "Open Lab Additively"],
            titles);
    }

    // ---- Session tree ----

    private (MainViewModel Window, TreeNodeViewModel LocalRoot, TreeNodeViewModel Folder, TreeNodeViewModel Session) Tree()
    {
        _store.CreateFolder("Core");
        _store.Add(Ssh("edge", "Core"));
        _store.Add(Ssh("spine"));
        _store.Add(new Session { Id = Guid.NewGuid(), Name = "pwsh", Kind = SessionKind.Local });
        var window = Window();
        var localRoot = window.RootNodes.Single(n => n.IsLocalRoot);
        var folder = window.RootNodes.Single(n => n is { IsFolder: true, IsLocalRoot: false });
        var session = window.RootNodes.Single(n => n.Session?.Name == "spine");
        return (window, localRoot, folder, session);
    }

    private static List<string> MenuTexts(MainViewModel window) =>
        window.BuildSelectionMenu().Select(entry => entry.IsSeparator ? "-" : entry.Text).ToList();

    [Fact]
    public void SelectionMenuMatchesTheSelectionKind()
    {
        var (window, localRoot, folder, session) = Tree();

        Assert.Empty(window.BuildSelectionMenu());

        window.TreeSelection.SelectOnly(session);
        Assert.Equal(["Connect", "Connect in new window", "-", "Edit…", "Delete"], MenuTexts(window));

        window.TreeSelection.SelectOnly(localRoot.Children.Single());
        Assert.Equal(["Open", "Open in new window", "-", "Edit…", "Set as Default", "Delete"], MenuTexts(window));

        window.TreeSelection.SelectOnly(localRoot);
        Assert.Equal(["New Local Profile…", "New Folder…", "-", "Expand All", "Collapse All"], MenuTexts(window));

        window.TreeSelection.SelectOnly(folder);
        Assert.Equal(
            ["Connect in Tabs", "Open in new window", "-", "Expand All", "Collapse All", "-",
             "New Session…", "New Folder…", "-", "Rename…", "Delete"],
            MenuTexts(window));

        window.TreeSelection.Toggle(session);
        Assert.Equal(["Connect in Tabs", "Open in new window", "-", "Delete"], MenuTexts(window));
    }

    [Fact]
    public void OpenSelectionConnectsEverySessionOnce()
    {
        var (window, _, folder, session) = Tree();
        window.TreeSelection.SelectOnly(folder);
        window.TreeSelection.Toggle(session);

        window.OpenSelectionCommand.Execute(null);

        Assert.Equal(["Connect edge", "Connect spine"], _window.Calls);
    }

    [Fact]
    public void LocalRootAloneCannotBeEditedOrDeleted()
    {
        var (window, localRoot, _, _) = Tree();
        window.TreeSelection.SelectOnly(localRoot);

        Assert.False(window.EditSelectionCommand.CanExecute(null));
        Assert.False(window.DeleteSelectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeletingAMixedSelectionSkipsTheLocalRoot()
    {
        var (window, localRoot, folder, session) = Tree();
        window.TreeSelection.SelectOnly(localRoot);
        window.TreeSelection.Toggle(folder);
        window.TreeSelection.Toggle(session);

        await window.DeleteSelectionCommand.ExecuteAsync(null);

        var (title, message) = Assert.Single(_window.Confirmations);
        Assert.Equal("Delete Selection", title);
        Assert.Equal(
            "Delete 1 folder(s) and 1 session(s)? 2 session(s) will be removed; their saved credentials are removed too.",
            message);
        Assert.Equal(["pwsh"], _store.Sessions.Select(s => s.Name));
        Assert.Contains(window.RootNodes, n => n.IsLocalRoot);
    }

    [Fact]
    public async Task DeclinedDeleteChangesNothing()
    {
        var (window, _, folder, _) = Tree();
        _window.ConfirmAnswer = false;
        window.TreeSelection.SelectOnly(folder);

        await window.DeleteSelectionCommand.ExecuteAsync(null);

        Assert.Equal("Delete the folder \"Core\" and the 1 session(s) inside it? Their saved credentials are removed too.",
            Assert.Single(_window.Confirmations).Message);
        Assert.Equal(3, _store.Sessions.Count);
    }

    [Fact]
    public async Task CancelledFolderPromptsChangeNothing()
    {
        var (window, _, folder, _) = Tree();

        await window.NewSubfolderCommand.ExecuteAsync(folder);
        await window.RenameFolderCommand.ExecuteAsync(folder);
        _window.PromptAnswers.Enqueue("   ");
        await window.NewFolderCommand.ExecuteAsync(null);

        Assert.Equal(["Core"], _store.Folders);
    }

    [Fact]
    public async Task RenameFolderKeepsItsParent()
    {
        _store.CreateFolder("Sites/North");
        var window = Window();
        var sites = window.RootNodes.Single(n => n.Name == "Sites");
        var north = sites.Children.Single();
        _window.PromptAnswers.Enqueue("South");

        await window.RenameFolderCommand.ExecuteAsync(north);

        Assert.Contains("Sites/South", _store.Folders);
        Assert.DoesNotContain("Sites/North", _store.Folders);
    }

    [Fact]
    public void EditSelectionOpensTheRightEditor()
    {
        var (window, localRoot, _, session) = Tree();

        window.TreeSelection.SelectOnly(session);
        window.EditSelectionCommand.Execute(null);
        window.TreeSelection.SelectOnly(localRoot.Children.Single());
        window.EditSelectionCommand.Execute(null);

        Assert.Equal(["EditSession spine in ''", "EditLocalProfile pwsh in ''"], _window.Calls);
    }

    // ---- Rails ----

    [Fact]
    public void RecentSessionsDropDeletedSessionsAndSaveTheCleanList()
    {
        var kept = Ssh("kept");
        _store.Add(kept);
        var deleted = Guid.NewGuid();
        _recentIds = [deleted, kept.Id];
        var window = Window();

        window.RefreshRecentSessions();

        Assert.Equal(["kept"], window.RecentSessions.Select(n => n.Name));
        Assert.Equal([kept.Id], _recentIds);
        Assert.False(window.HasNoRecentSessions);
    }

    [Fact]
    public async Task StaleRecordingLoadIsDiscarded()
    {
        _recordingDirectory = _directory;
        var window = Window();
        var slowStarted = new TaskCompletionSource();
        var releaseSlow = new TaskCompletionSource();
        var calls = 0;
        window.RecordingLoader = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                slowStarted.SetResult();
                releaseSlow.Task.Wait();
                return [new RecordingItemViewModel { Name = "stale" }];
            }
            return [new RecordingItemViewModel { Name = "fresh" }];
        };

        var slow = window.RefreshRecordingsAsync();
        await slowStarted.Task;
        await window.RefreshRecordingsAsync();
        releaseSlow.SetResult();
        await slow;

        Assert.Equal(["fresh"], window.Recordings.Select(r => r.Name));
        Assert.False(window.ShowRecordingsStatus);
    }

    [Fact]
    public async Task UnreadableRecordingFolderShowsTheError()
    {
        _recordingDirectory = _directory;
        var window = Window();
        window.RecordingLoader = _ => throw new UnauthorizedAccessException("Access denied.");

        await window.RefreshRecordingsAsync();

        Assert.True(window.ShowRecordingsStatus);
        Assert.Equal("Recordings could not be listed.\nAccess denied.", window.RecordingsStatusText);
    }

    [Fact]
    public async Task OpenRecordingWithoutAnItemAsksForAFile()
    {
        var window = Window();

        await window.OpenRecordingCommand.ExecuteAsync(null);
        _window.PickedRecording = "picked.cast";
        await window.OpenRecordingCommand.ExecuteAsync(null);
        await window.OpenRecordingCommand.ExecuteAsync(new RecordingItemViewModel { FilePath = "listed.cast" });

        Assert.Equal(["OpenRecording picked.cast", "OpenRecording listed.cast"], _window.Calls);
    }

    private sealed class Credentials : ICredentialService
    {
        public string? Read(Guid sessionId) => null;
        public void Write(Guid sessionId, string secret) { }
        public void Delete(Guid sessionId) { }
    }
}
