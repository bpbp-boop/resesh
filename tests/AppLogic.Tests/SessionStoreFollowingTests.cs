using Resesh.App.ViewModels;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

/// <summary>Windows hold copies of saved sessions (tabs, tree nodes, Recent entries). They
/// follow the session store whoever writes to it, and act on the saved record, not a copy.</summary>
public sealed class SessionStoreFollowingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("resesh-following-tests-").FullName;
    private readonly FakeMainWindowServices _services = new();
    private readonly SessionStore _store;
    private readonly List<Guid> _deleted = [];
    private IReadOnlyList<Guid> _recentIds = [];
    private Func<Session, bool> _isVisible = _ => true;
    private int _appearanceApplications;

    public SessionStoreFollowingTests()
    {
        _store = new SessionStore(Path.Combine(_directory, "sessions.json"));
        _store.Load();
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private MainViewModel Window(IdentityStore? identities = null) => new(_store, new Credentials(), new ViewModelEnvironment
    {
        CurrentTheme = () => "dark",
        ResolveTheme = theme => theme,
        ShowAgentIcons = () => true,
        IsSessionVisible = session => _isVisible(session),
        ApplySessionSettings = _ => _appearanceApplications++,
        ReportError = error => throw error,
        RecentSessionIds = () => _recentIds,
        SaveRecentSessionIds = ids => _recentIds = ids,
        SessionsDeleted = _deleted.AddRange,
        LoginUsername = session => _loginOverride ?? session.Username,
    }, _services, identities);

    private string? _loginOverride;

    private static Session Ssh(string name, string folder = "") =>
        new() { Name = name, Host = name + ".example", Username = "admin", FolderPath = folder };

    private static Session Telnet(string name, string folder = "") =>
        new() { Name = name, Kind = SessionKind.Telnet, Host = name + ".example", Port = 23, FolderPath = folder };

    private static IEnumerable<TreeNodeViewModel> AllNodes(IEnumerable<TreeNodeViewModel> nodes) =>
        nodes.SelectMany(node => AllNodes(node.Children).Prepend(node));

    private static TreeNodeViewModel Node(MainViewModel window, Guid id) =>
        AllNodes(window.RootNodes).Single(node => node.Session?.Id == id);

    private static TreeNodeViewModel Folder(MainViewModel window, string path) =>
        AllNodes(window.RootNodes).Single(node => node is { IsFolder: true, IsLocalScope: false } && node.FolderPath == path);

    // ---- Every writer reaches every window ----

    [Fact]
    public void UnrelatedSaveDoesNotReapplyUnchangedNestedSessionSettings()
    {
        var profile = new Session
        {
            Name = "local", Kind = SessionKind.Local,
            Local = new LocalTarget { Executable = "cmd.exe", Arguments = ["/q"] },
            Overrides = new TerminalOverrides { EnabledRules = ["rule"] },
            Algorithms = new SshAlgorithms { DisabledCiphers = ["cipher"] },
        };
        _store.Add(profile);
        var window = Window();
        var tab = window.Connect(_store.Find(profile.Id)!);
        _store.Add(Ssh("other"));
        Assert.Equal(0, _appearanceApplications);

        _store.Update(_store.Find(profile.Id)! with { Overrides = new TerminalOverrides { FontSize = 20 } });
        Assert.Equal(1, _appearanceApplications);
        Assert.Equal(20, tab.Session.Overrides!.FontSize);
    }

    [Fact]
    public void DetachingAnIdentityUpdatesTabsAndTreesOfEveryWindow()
    {
        var identity = Guid.NewGuid();
        var session = Ssh("web") with { IdentityId = identity };
        _store.Add(session);
        var first = Window();
        var second = Window();
        var tab = second.Connect(_store.Find(session.Id)!);

        _store.DetachIdentity(identity); // the Settings page writes the store directly

        Assert.Equal(SessionLogin.Inherit, tab.Session.Login);
        Assert.Equal(SessionLogin.Inherit, Node(first, session.Id).Session!.Login);
        Assert.Equal(SessionLogin.Inherit, Node(second, session.Id).Session!.Login);
    }

    [Fact]
    public void FolderIdentityChangeRefreshesTabEndpointsThoughTheSessionIsUnchanged()
    {
        var session = Ssh("web", "prod") with { InheritLogin = true };
        _store.Add(session);
        var window = Window();
        var tab = window.Connect(_store.Find(session.Id)!);
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _store.SetFolderIdentity("prod", Guid.NewGuid()); // only the folder's settings change

        Assert.Contains(nameof(TabViewModel.Endpoint), raised);
        Assert.Contains(nameof(TabViewModel.Subtitle), raised);
    }

    [Fact]
    public void PresentationEditInOneWindowIsPatchedInPlaceInEvery()
    {
        var session = Ssh("web", "prod");
        _store.Add(session);
        var first = Window();
        var second = Window();
        var firstNode = Node(first, session.Id);
        var secondNode = Node(second, session.Id);

        first.UpdateSession(session with { Host = "10.0.0.9", Icon = "linux" }, null);

        Assert.Same(firstNode, Node(first, session.Id));
        Assert.Same(secondNode, Node(second, session.Id));
        Assert.Equal("10.0.0.9", secondNode.HostSummary);
        Assert.Equal("linux", secondNode.IconKey);
    }

    [Fact]
    public void RenameInOneWindowReordersTheOthers()
    {
        var a = Ssh("a");
        var b = Ssh("b");
        _store.AddMany([a, b]);
        var first = Window();
        var second = Window();

        first.UpdateSession(a with { Name = "c" }, null);

        Assert.Equal(["b", "c"], second.RootNodes.Where(n => n.Session is not null).Select(n => n.Name));
    }

    [Fact]
    public void LoginSavedFromAConnectPromptKeepsEditsMadeSinceTheTabOpened()
    {
        var session = Ssh("web") with { IdentityId = Guid.NewGuid() };
        _store.Add(session);
        var window = Window();
        var tabCopy = _store.Find(session.Id)!;
        window.UpdateSession(tabCopy with { Icon = "linux", FolderPath = "prod" }, null); // e.g. another window

        window.UpdateLogin(tabCopy with { IdentityId = null, InheritLogin = false, Username = "root" }, null);

        var saved = _store.Find(session.Id)!;
        Assert.Equal(SessionLogin.Own, saved.Login);
        Assert.Equal("root", saved.Username);
        Assert.Equal("linux", saved.Icon);
        Assert.Equal("prod", saved.FolderPath);
    }

    // ---- Recent and tree actions use the saved record ----

    [Fact]
    public void RecentFollowsEditsAndConnectsTheSavedRecord()
    {
        var session = Ssh("web");
        _store.Add(session);
        _recentIds = [session.Id];
        var window = Window();
        window.RefreshRecentSessions();
        var entry = window.RecentSessions.Single();

        window.UpdateSession(session with { Host = "10.0.0.9" }, null);
        Assert.Equal("10.0.0.9", window.RecentSessions.Single().Session!.Host);

        _store.Update(_store.Find(session.Id)! with { Port = 2222 });
        window.ConnectRecentCommand.Execute(entry); // a stale entry still connects what is saved

        Assert.Equal(2222, Assert.Single(_services.ConnectedSessions).Port);
    }

    [Fact]
    public void TreeCommandsConnectTheSavedRecordAndSkipDeletedSessions()
    {
        var kept = Ssh("kept");
        var gone = Ssh("gone");
        _store.AddMany([kept, gone]);
        var window = Window();
        var nodes = new[] { Node(window, kept.Id), Node(window, gone.Id) };
        _store.RemoveMany([gone.Id]);
        _store.Update(kept with { Host = "moved.example" });

        window.OpenSelectionCommand.Execute(nodes);

        Assert.Equal("moved.example", Assert.Single(_services.ConnectedSessions).Host);
    }

    // ---- Counts ----

    [Fact]
    public async Task DeletingAFolderOfTelnetSessionsCountsThem()
    {
        _store.AddMany([Telnet("console-1", "Lab"), Telnet("console-2", "Lab/Rack")]);
        var window = Window();
        window.TreeSelection.SelectOnly(Folder(window, "Lab"));

        await window.DeleteSelectionCommand.ExecuteAsync(null);

        Assert.Equal(
            "Delete the folder \"Lab\" and the 2 session(s) inside it? Their saved credentials are removed too.",
            Assert.Single(_services.Confirmations).Message);
        Assert.Empty(_store.Sessions);
    }

    [Fact]
    public async Task DeletingASelectionWhileFilteringCountsWhatTheFolderDeleteRemoves()
    {
        _store.AddMany([Ssh("web-1", "prod"), Ssh("db-1", "prod"), Telnet("console", "prod"), Ssh("web-2")]);
        var window = Window();
        window.SearchText = "web";
        window.TreeSelection.SelectOnly(Folder(window, "prod"));
        window.TreeSelection.Toggle(window.RootNodes.Single(n => n.Name == "web-2"));

        await window.DeleteSelectionCommand.ExecuteAsync(null);

        Assert.Equal(
            "Delete 1 folder(s) and 1 session(s)? 4 session(s) will be removed; their saved credentials are removed too.",
            Assert.Single(_services.Confirmations).Message);
        Assert.Empty(_store.Sessions);
    }

    [Fact]
    public void CountsLeaveOutHiddenProfiles()
    {
        _store.AddMany([Ssh("web"), new Session { Name = "missing shell", Kind = SessionKind.Local, BuiltIn = true }]);
        _isVisible = session => !session.BuiltIn;
        var window = Window();

        Assert.Equal(1, window.TotalSessionCount);
        Assert.StartsWith("1 sessions", window.StatusText);
    }

    // ---- Filtering ----

    [Fact]
    public void EditingAProfilesShellRefiltersTheTree()
    {
        var profile = new Session
        {
            Name = "shell",
            Kind = SessionKind.Local,
            Local = new LocalTarget { Executable = "bash.exe" },
        };
        _store.Add(profile);
        var window = Window();
        window.SearchText = "zsh";
        Assert.Equal(0, window.MatchCount);

        window.UpdateSession(profile with { Local = new LocalTarget { Executable = "zsh.exe" } }, null);

        Assert.Equal(1, window.MatchCount);
        Assert.Contains(AllNodes(window.RootNodes), node => node.Session?.Id == profile.Id);
    }

    // ---- Folder expansion ----

    [Fact]
    public void RenamingAFolderMovesTheExpansionOfEverythingBeneathIt()
    {
        _store.Add(Ssh("web", "a/b/c"));
        var window = Window();
        window.NoteExpansion(Folder(window, "a/b"), expanded: false);
        window.NoteExpansion(Folder(window, "a/b/c"), expanded: false);

        window.RenameFolder("a", "z");

        Assert.True(Folder(window, "z").IsExpanded);
        Assert.False(Folder(window, "z/b").IsExpanded);
        Assert.False(Folder(window, "z/b/c").IsExpanded);
    }

    [Fact]
    public void DeletingAFolderForgetsItsExpansion()
    {
        _store.CreateFolder("a/b");
        var window = Window();
        window.NoteExpansion(Folder(window, "a/b"), expanded: false);

        window.DeleteFolder("a");
        window.CreateFolder("a/b");

        Assert.True(Folder(window, "a/b").IsExpanded);
    }

    [Fact]
    public void FailedRenameKeepsTheExpansion()
    {
        _store.Add(Ssh("web", "a/b"));
        var window = Window();
        window.NoteExpansion(Folder(window, "a/b"), expanded: false);

        using (new FileStream(Path.Combine(_directory, "sessions.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => window.RenameFolder("a", "z"));
        window.RebuildTree();

        Assert.False(Folder(window, "a/b").IsExpanded);
        window.RenameFolder("a", "z");
        Assert.False(Folder(window, "z/b").IsExpanded);
    }

    // ---- Deleted sessions leave saved workspaces ----

    [Fact]
    public void EveryDeletePathReportsTheDeletedSessions()
    {
        var one = Ssh("one");
        var two = Ssh("two");
        var three = Ssh("three", "lab");
        var telnet = Telnet("console", "lab");
        _store.AddMany([one, two, three, telnet]);
        var window = Window();

        window.DeleteSession(one);
        window.DeleteSessions([two]);
        window.DeleteFolder("lab");

        Assert.Equal([one.Id, two.Id], _deleted.Take(2));
        Assert.Equal(new[] { three.Id, telnet.Id }.Order(), _deleted.Skip(2).Order());
    }

    // ---- Bulk edits apply to the saved records ----

    [Fact]
    public void BulkEditKeepsChangesMadeSinceTheTableWasFilled()
    {
        var session = Ssh("web");
        _store.Add(session);
        var window = Window();
        var row = _store.Find(session.Id)!;
        _store.Update(row with { Icon = "linux" }); // another window, while the table was open

        var changed = window.EditSessions([row.Id], new SessionBulkEdit { Login = SessionLogin.Inherit });

        Assert.Single(changed);
        var saved = _store.Find(session.Id)!;
        Assert.Equal(SessionLogin.Inherit, saved.Login);
        Assert.Equal("linux", saved.Icon);
    }

    private sealed class Credentials : ICredentialService
    {
        public string? Read(Guid sessionId) => null;
        public void Write(Guid sessionId, string secret) { }
        public void Delete(Guid sessionId) { }
    }

    [Fact]
    public void IdentityChangeRefreshesEveryWindowsTabsThoughNoSessionChanged()
    {
        var identities = new IdentityStore(Path.Combine(_directory, "identities.json"));
        identities.Load();
        var session = Ssh("web");
        _store.Add(session);
        var first = Window(identities);
        var second = Window(identities);
        var firstTab = first.Connect(_store.Find(session.Id)!);
        var secondTab = second.Connect(_store.Find(session.Id)!);

        _loginOverride = "deploy"; // as an identity's new username would resolve
        identities.Add(new Identity { Name = "deploy", Username = "deploy" });

        Assert.Contains("deploy@", firstTab.Endpoint);
        Assert.Contains("deploy@", secondTab.Endpoint);

        second.Dispose();
        _loginOverride = "ops";
        identities.Add(new Identity { Name = "ops", Username = "ops" });
        Assert.Contains("ops@", firstTab.Endpoint);
    }
}
