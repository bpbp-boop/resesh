using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Search;
using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ViewModelEnvironment _environment;
    private readonly SessionStore _store;
    private readonly ICredentialService _credentials;
    private readonly IMainWindowServices? _services;

    // Folder expansion is keyed by TreeNodeViewModel.ExpansionKey (path, with a reserved
    // prefix for the Local scope) so it survives tree rebuilds. Default: expanded.
    private readonly Dictionary<string, bool> _expansion = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _filterExpansion = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, TreeNodeViewModel> _sessionNodes = [];
    private readonly Dictionary<string, TreeNodeViewModel> _folderNodes = new(StringComparer.Ordinal);

    private static string ExpansionKeyFor(string folderPath, SessionKind kind) =>
        kind == SessionKind.Local ? "\u0000local\u0000" + folderPath : folderPath;

    private string _searchText = "";
    private TabGroupViewModel _focusedGroup;

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = [];

    /// <summary>All tab groups in visual traversal order.</summary>
    public ObservableCollection<TabGroupViewModel> Groups { get; } = [];

    /// <summary>Tab groups of the window's other open workspaces. Their tabs stay connected
    /// and belong to this window, but they are not laid out until their workspace is shown.</summary>
    public List<TabGroupViewModel> BackgroundGroups { get; } = [];

    /// <summary>Raised when a property of any tab in the window changes.</summary>
    public event Action<TabViewModel, string?>? TabChanged;

    public MainViewModel(
        SessionStore store,
        ICredentialService credentials,
        ViewModelEnvironment environment,
        IMainWindowServices? services = null)
    {
        _environment = environment;
        _store = store;
        _credentials = credentials;
        _services = services;
        TreeSelection = new((node, selected) => node.IsSelected = selected);
        var initial = new TabGroupViewModel();
        Groups.Add(initial);
        _focusedGroup = initial;
        RebuildTree();
        Commands = new AppCommandCatalog(this);
        _store.Changed += Store_Changed;
    }

    /// <summary>Stops following the session store. The window calls this when it closes.</summary>
    public void Dispose() => _store.Changed -= Store_Changed;

    // Every saved change reaches every window, whoever made it: a dialog, another window,
    // a connect prompt or an import. The tree, tabs and Recent re-read the store here.
    private void Store_Changed() => _environment.RunOnUiThread(RefreshSessions);

    /// <summary>The window that carries out commands. Only commands need it.</summary>
    private IMainWindowServices Services =>
        _services ?? throw new InvalidOperationException("This view model has no window services.");

    /// <summary>Window state the palette reads for titles such as Show/Hide.</summary>
    internal IMainWindowServices WindowServices => Services;

    /// <summary>Every command with its palette text and shortcut, for the palette and keys.</summary>
    public AppCommandCatalog Commands { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            var wasSearching = IsSearching;
            if (SetProperty(ref _searchText, value))
            {
                if (!wasSearching && IsSearching)
                    _filterExpansion.Clear();
                RebuildTree();
            }
        }
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchText);

    public bool IsFiltering => IsSearching;

    /// <summary>How many sessions the unfiltered tree shows. Every count the user reads (the
    /// status bar, the filter summary, the empty tree) uses this one, so hidden built-in
    /// profiles are never counted in one place and not another.</summary>
    public int TotalSessionCount => VisibleSessions.Count();

    public int MatchCount { get; private set; }

    public string MatchSummary => MatchCount == 0
        ? "No matching sessions"
        : $"{MatchCount} of {TotalSessionCount} sessions";

    /// <summary>The group that receives sessions opened from the tree (last-focused).</summary>
    public TabGroupViewModel FocusedGroup
    {
        get => _focusedGroup;
        set
        {
            if (SetProperty(ref _focusedGroup, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnActiveTabStateChanged();
                SyncGroupFocus();
            }
        }
    }

    /// <summary>Pushes group focus onto every tab: only the focused group's selected tab
    /// renders as "active" in split view. Call after moving tabs between groups (the
    /// FocusedGroup setter no-ops when the target group was already focused).</summary>
    public void SyncGroupFocus()
    {
        foreach (var group in Groups)
        {
            foreach (var tab in group.Tabs)
                tab.IsGroupFocused = group == _focusedGroup;
        }
    }

    public bool IsSplit => Groups.Count > 1;

    private bool _isZoomed;

    /// <summary>One tab fills the window while the split layout waits to come back.</summary>
    public bool IsZoomed
    {
        get => _isZoomed;
        set
        {
            if (SetProperty(ref _isZoomed, value))
                OnGroupsChanged();
        }
    }

    public void OnGroupsChanged()
    {
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(ZoomTabTitle));
        OnPropertyChanged(nameof(StatusText));
        OnActiveTabStateChanged();
    }

    /// <summary>The focused group's selected tab — what the status bar describes.</summary>
    public TabViewModel? ActiveTab => FocusedGroup.SelectedTab;

    /// <summary>The active tab's session theme override, if any. The focused pane's border
    /// takes this theme so a session such as production is flagged where keystrokes go.</summary>
    public string? ActiveSessionTheme => ActiveTab?.Session.Overrides?.Theme;

    public void NotifyActiveTabChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnActiveTabStateChanged();
    }

    public string StatusText
    {
        get
        {
            var baseText = $"{TotalSessionCount} sessions";
            var tab = ActiveTab;
            if (tab is null)
                return baseText;
            if (tab.IsAppPage)
                return $"{baseText}  •  {tab.Header} — {tab.StateText}";
            var status = $"{baseText}  •  {tab.Header} — {tab.Endpoint} • {tab.StateText}";
            return tab.ConnectionSummary.Length > 0 ? $"{status} • {tab.ConnectionSummary}" : status;
        }
    }

    /// <summary>The status bar's tooltip: the active tab's connection details while it is
    /// connected, otherwise none.</summary>
    public string? StatusDetails => ActiveTab is { State: TabConnectionState.Connected, IsLocked: false, ConnectionDetails.Length: > 0 } tab
        ? tab.ConnectionDetails
        : null;

    // Every change to the status text can change its tooltip too.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(StatusText))
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusDetails)));
    }

    public IReadOnlyList<string> FolderPathsForPicker => _store.Folders;

    public IReadOnlyList<string> LocalFolderPathsForPicker => _store.FoldersOf(SessionKind.Local);

    /// <summary>Built-in local profiles whose shell is not installed right now are hidden
    /// everywhere (tree, search, quick connect) but never deleted.</summary>
    private bool IsVisible(Session session) => _environment.IsSessionVisible(session);

    public IEnumerable<Session> VisibleSessions => _store.Sessions.Where(IsVisible);

    public IReadOnlyList<Session> RankedMatches(string query) => SessionSearch.Rank(VisibleSessions, query);

    // ---- Tabs / groups ----

    /// <summary>Every tab the window owns, in every open workspace.</summary>
    public IEnumerable<TabViewModel> AllTabs => Groups.Concat(BackgroundGroups).SelectMany(g => g.Tabs);

    /// <summary>The tabs of the workspace the window is showing.</summary>
    public IEnumerable<TabViewModel> ShownTabs => Groups.SelectMany(g => g.Tabs);

    public bool IsBackgroundGroup(TabGroupViewModel group) => BackgroundGroups.Contains(group);

    /// <summary>This window's tabs as one indicator, for its taskbar button.</summary>
    public TerminalProgress Progress => TerminalProgress.Combine(AllTabs.Select(tab => tab.Progress));

    /// <summary>The tab already hosting this app page, so opening it again reuses the tab.</summary>
    public TabViewModel? FindAppPage(AppPage page) => AllTabs.FirstOrDefault(tab => tab.Page == page);

    public TabGroupViewModel GroupOf(TabViewModel tab) =>
        Groups.Concat(BackgroundGroups).First(g => g.Tabs.Contains(tab));

    /// <summary>Creates a tab in the given group (or the focused one) and selects it.</summary>
    public TabViewModel Connect(Session session, TabGroupViewModel? group = null, TabViewModel? insertAfter = null)
    {
        group ??= insertAfter is null ? FocusedGroup : GroupOf(insertAfter);
        var insertionIndex = group.Tabs.Count;
        if (insertAfter is not null)
        {
            var sourceIndex = group.Tabs.IndexOf(insertAfter);
            if (sourceIndex < 0)
                throw new ArgumentException("The source tab must belong to the target group.", nameof(insertAfter));
            // Clones are unpinned, so they must follow the complete pinned prefix.
            insertionIndex = Math.Max(sourceIndex + 1, group.Tabs.Count(t => t.IsPinned));
        }
        var tab = new TabViewModel(session, _environment);
        AttachTab(tab, group, insertionIndex);
        return tab;
    }

    /// <summary>Attaches a live tab that was detached from another window.</summary>
    public void AttachTab(TabViewModel tab, TabGroupViewModel group, int index)
    {
        tab.PropertyChanged += Tab_PropertyChanged;
        group.Tabs.Insert(Math.Clamp(index, 0, group.Tabs.Count), tab);
        group.SelectedTab = tab;
        tab.IsGroupFocused = group == _focusedGroup;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Progress));
        OnActiveTabStateChanged();
    }

    /// <summary>Detaches a live tab without disposing its terminal session.</summary>
    public TabGroupViewModel DetachTab(TabViewModel tab)
    {
        var group = GroupOf(tab);
        tab.PropertyChanged -= Tab_PropertyChanged;
        group.RemoveTab(tab);
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Progress));
        OnActiveTabStateChanged();
        return group;
    }

    private void Tab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.State) or nameof(TabViewModel.ConnectionSummary)
            or nameof(TabViewModel.ConnectionDetails) or nameof(TabViewModel.Header) or nameof(TabViewModel.IsLocked))
        {
            OnPropertyChanged(nameof(StatusText));
        }
        else if (e.PropertyName == nameof(TabViewModel.Progress))
        {
            OnPropertyChanged(nameof(Progress));
        }
        if (sender is TabViewModel changed)
            TabChanged?.Invoke(changed, e.PropertyName);

        // Tab commands target the active tab by default, so its state decides what they allow.
        if (e.PropertyName is nameof(TabViewModel.State) or nameof(TabViewModel.IsLocked)
            or nameof(TabViewModel.IsPinned) or nameof(TabViewModel.IsPlayback)
            or nameof(TabViewModel.View) or nameof(TabViewModel.CanNotifyCommandCompletion))
        {
            OnActiveTabStateChanged();
        }
    }

    /// <summary>Removes and disposes the tab. Collapsing empty groups is the window's job.</summary>
    public void CloseTab(TabViewModel tab)
    {
        DetachTab(tab);
        (tab.View as IDisposable)?.Dispose();
    }

    public void CloseAllTabs()
    {
        foreach (var group in Groups.Concat(BackgroundGroups))
        {
            foreach (var tab in group.Tabs.ToList())
            {
                tab.PropertyChanged -= Tab_PropertyChanged;
                Resesh.Core.Backend.CleanupActions.Run(_environment.ReportError,
                    () => (tab.View as IDisposable)?.Dispose());
            }
            group.Tabs.Clear();
            group.SelectedTab = null;
        }
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Progress));
        OnActiveTabStateChanged();
    }

    // ---- Session CRUD ----
    // Each change is one store write. The tree, tabs and Recent of every window follow it
    // through the store's Changed event (RefreshSessions), not through these methods.

    public void AddSession(Session session, string? password)
    {
        _store.Add(session);
        if (!string.IsNullOrEmpty(password))
            _credentials.Write(session.Id, password);
    }

    public void UpdateSession(Session session, string? password)
    {
        var previous = _store.Find(session.Id);
        _store.Update(session);
        if (!string.IsNullOrEmpty(password))
            _credentials.Write(session.Id, password);
        else if (UsesOwnPassword(previous) && !UsesOwnPassword(session))
            _credentials.Delete(session.Id);
    }

    /// <summary>Saves a login chosen at a connect prompt. Only the login fields come from
    /// <paramref name="login"/>, which is the tab's copy of the session; everything else is
    /// the saved record, so a copy taken before other edits cannot write them back.</summary>
    public void UpdateLogin(Session login, string? password)
    {
        if (_store.Find(login.Id) is not { } current)
            return;
        UpdateSession(current with
        {
            Username = login.Username,
            AuthMethod = login.AuthMethod,
            PrivateKeyId = login.PrivateKeyId,
            PrivateKeyPath = login.PrivateKeyPath,
            IdentityId = login.IdentityId,
            InheritLogin = login.InheritLogin,
        }, password);
    }

    private static bool UsesOwnPassword(Session? session) => session?.UsesOwnPassword == true;

    public void DeleteSession(Session session)
    {
        _store.Remove(session.Id);
        ForgetDeleted([session.Id]);
    }

    public void MoveSessionsToFolder(IEnumerable<Guid> sessionIds, string folderPath) =>
        _store.MoveToFolder(sessionIds, folderPath);

    /// <summary>Cleans up after deleted sessions: their saved passwords, and their tabs in
    /// saved workspaces. Every delete path ends here.</summary>
    private void ForgetDeleted(IReadOnlyCollection<Guid> sessionIds)
    {
        foreach (var id in sessionIds)
        {
            _credentials.Delete(id);
            _credentials.DeleteProxy(id);
        }
        if (sessionIds.Count > 0)
            _environment.SessionsDeleted(sessionIds);
    }

    /// <summary>The saved record of a session shown somewhere as a copy (a tree node, a
    /// Recent entry), or null once it is deleted or hidden. Actions read this at the moment
    /// they run, so they never act on details that changed since the copy was taken.</summary>
    public Session? CurrentSession(Guid id) =>
        _store.Find(id) is { } session && IsVisible(session) ? session : null;

    // ---- Bulk session changes (Settings > Sessions): one store write per action ----

    /// <summary>Saves already-edited sessions. A session that stops using its own password
    /// loses the stored copy, as it does when edited one at a time.</summary>
    public void UpdateSessions(IReadOnlyList<Session> sessions)
    {
        if (sessions.Count == 0)
            return;
        var dropsPassword = sessions
            .Where(session => UsesOwnPassword(_store.Find(session.Id)) && !UsesOwnPassword(session))
            .ToList();
        _store.UpdateMany(sessions);
        foreach (var session in dropsPassword)
            _credentials.Delete(session.Id);
    }

    /// <summary>Applies a bulk edit to the saved records of these sessions, not to copies a
    /// table showed earlier, so a change made meanwhile (in another window, say) is kept.
    /// Returns the sessions the edit changed.</summary>
    public IReadOnlyList<Session> EditSessions(IEnumerable<Guid> sessionIds, SessionBulkEdit edit)
    {
        var changed = edit.ApplyTo(sessionIds.Distinct().Select(_store.Find).OfType<Session>().ToList());
        UpdateSessions(changed);
        return changed;
    }

    public void DeleteSessions(IEnumerable<Session> sessions) =>
        ForgetDeleted(_store.RemoveMany(sessions.Select(session => session.Id)).Select(removed => removed.Id).ToList());

    /// <summary>Copies the sessions beside the originals, saved passwords included. Copies
    /// are made from the saved records, not from the copies passed in.</summary>
    public IReadOnlyList<Session> DuplicateSessions(IEnumerable<Session> sessions)
    {
        var sources = sessions.Select(session => _store.Find(session.Id)).OfType<Session>().ToList();
        var copies = SessionDuplicator.Duplicate(sources, _store.Sessions);
        _store.AddMany(copies.Select(pair => pair.Copy));
        foreach (var (source, copy) in copies)
        {
            if (source.Proxy is not null && _credentials.ReadProxy(source.Id) is { Length: > 0 } proxyPassword)
                _credentials.WriteProxy(copy.Id, proxyPassword);
            if (UsesOwnPassword(source) && _credentials.Read(source.Id) is { Length: > 0 } password)
                _credentials.Write(copy.Id, password);
        }
        return copies.Select(pair => pair.Copy).ToList();
    }

    // ---- Folder CRUD (SSH and Local folders are separate namespaces) ----

    public void CreateFolder(string path, SessionKind kind = SessionKind.Ssh)
    {
        path = FolderPaths.Normalize(path);
        if (path.Length == 0 || _store.FoldersOf(kind).Contains(path, StringComparer.OrdinalIgnoreCase))
            return;

        _store.CreateFolder(path, kind);
    }

    public void RenameFolder(string oldPath, string newPath, SessionKind kind = SessionKind.Ssh)
    {
        // The save rebuilds the tree, so the expansion of the folder and the folders beneath
        // it moves first, and moves back if the save fails.
        var expansion = new Dictionary<string, bool>(_expansion, _expansion.Comparer);
        var filterExpansion = new Dictionary<string, bool>(_filterExpansion, _filterExpansion.Comparer);
        MoveExpansion(_expansion, oldPath, newPath, kind);
        MoveExpansion(_filterExpansion, oldPath, newPath, kind);
        try
        {
            _store.RenameFolder(oldPath, newPath, kind);
        }
        catch
        {
            Restore(_expansion, expansion);
            Restore(_filterExpansion, filterExpansion);
            throw;
        }

        static void Restore(Dictionary<string, bool> target, Dictionary<string, bool> saved)
        {
            target.Clear();
            foreach (var (key, value) in saved)
                target[key] = value;
        }
    }

    /// <summary>The number of sessions deleting this folder removes: the store's own answer,
    /// so telnet and serial sessions in the folder are counted too.</summary>
    public int CountSessionsUnder(string folderPath, SessionKind kind = SessionKind.Ssh) =>
        _store.SessionsInFolder(folderPath, kind).Count;

    public void DeleteFolder(string path, SessionKind kind = SessionKind.Ssh)
    {
        var removed = _store.DeleteFolder(path, kind);
        ForgetExpansion(_expansion, path, kind);
        ForgetExpansion(_filterExpansion, path, kind);
        ForgetDeleted(removed.Select(session => session.Id).ToList());
    }

    /// <summary>Moves the expansion state of a folder and every folder beneath it to their new paths.</summary>
    private static void MoveExpansion(Dictionary<string, bool> expansion, string oldPath, string newPath, SessionKind kind)
    {
        oldPath = FolderPaths.Normalize(oldPath);
        newPath = FolderPaths.Normalize(newPath);
        if (oldPath.Length == 0 || newPath.Length == 0)
            return;
        var moved = FolderKeys(expansion, kind)
            .Select(item => (item.Key, Path: FolderPaths.Reparent(item.Path, oldPath, newPath)))
            .Where(item => item.Path is not null)
            .Select(item => (item.Key, NewKey: ExpansionKeyFor(item.Path!, kind), Expanded: expansion[item.Key]))
            .ToList();
        foreach (var (key, _, _) in moved)
            expansion.Remove(key);
        foreach (var (_, newKey, expanded) in moved)
            expansion[newKey] = expanded;
    }

    private static void ForgetExpansion(Dictionary<string, bool> expansion, string folderPath, SessionKind kind)
    {
        folderPath = FolderPaths.Normalize(folderPath);
        if (folderPath.Length == 0)
            return;
        foreach (var (key, path) in FolderKeys(expansion, kind))
        {
            if (FolderPaths.IsSelfOrDescendant(path, folderPath))
                expansion.Remove(key);
        }
    }

    /// <summary>The expansion keys of one folder namespace with their paths, as a snapshot
    /// the caller may change the map under.</summary>
    private static List<(string Key, string Path)> FolderKeys(Dictionary<string, bool> expansion, SessionKind kind)
    {
        var localPrefix = ExpansionKeyFor("", SessionKind.Local);
        var local = kind == SessionKind.Local;
        return expansion.Keys
            .Where(key => key.StartsWith(localPrefix, StringComparison.Ordinal) == local)
            .Select(key => (key, local ? key[localPrefix.Length..] : key))
            .ToList();
    }

    /// <summary>Brings the window up to date with the store: tabs take their session's
    /// saved record, the tree is patched in place or rebuilt (see <see cref="TreeShapeChanged"/>),
    /// and Recent is re-read.</summary>
    public void RefreshSessions()
    {
        foreach (var tab in AllTabs)
        {
            if (_store.Find(tab.Session.Id) is { } session && session != tab.Session)
            {
                tab.Session = session;
                // Appearance overrides take effect immediately; connection fields apply on next connect.
                _environment.ApplySessionSettings(tab);
            }
            else
            {
                // A folder identity can change the login without changing the session record.
                tab.NotifyLoginChanged();
            }
        }
        OnPropertyChanged(nameof(ActiveSessionTheme));
        OnPropertyChanged(nameof(StatusText)); // the session count, and the login in the endpoint
        OnActiveTabStateChanged();
        RefreshTree();
        RefreshRecentSessions();
    }

    // ---- Tree ----

    // What the displayed tree was built from, so a store change can tell an edit that only
    // changes how a leaf looks (patched in place, keeping realized containers) from one
    // that adds, removes, moves, reorders or re-filters nodes (rebuilt).
    private Dictionary<Guid, Session> _treeSessions = [];
    private IReadOnlyList<string> _treeSshFolders = [];
    private IReadOnlyList<string> _treeLocalFolders = [];

    private void RefreshTree()
    {
        var visible = VisibleSessions.ToList();
        if (TreeShapeChanged(visible))
        {
            RebuildTree();
            return;
        }
        foreach (var session in visible)
        {
            if (_treeSessions[session.Id] == session)
                continue;
            _treeSessions[session.Id] = session;
            // A session the filter hides has no node, and the edit did not make it match.
            if (_sessionNodes.TryGetValue(session.Id, out var node))
                node.UpdateSession(session);
        }
    }

    /// <summary>Whether the tree must be rebuilt rather than patched: sessions or folders
    /// came or went, a session moved, changed kind or was renamed (ordering), or an edit
    /// changed whether it matches the filter. The filter question goes to
    /// <see cref="SessionSearch"/> itself, so it covers every field the search reads.</summary>
    private bool TreeShapeChanged(IReadOnlyList<Session> visible)
    {
        if (visible.Count != _treeSessions.Count
            || !_store.FoldersOf(SessionKind.Ssh).SequenceEqual(_treeSshFolders, StringComparer.Ordinal)
            || !_store.FoldersOf(SessionKind.Local).SequenceEqual(_treeLocalFolders, StringComparer.Ordinal))
        {
            return true;
        }
        var query = _searchText.Trim();
        foreach (var session in visible)
        {
            if (!_treeSessions.TryGetValue(session.Id, out var previous))
                return true;
            if (previous == session)
                continue;
            if (previous.Kind != session.Kind
                || !previous.FolderPath.Equals(session.FolderPath, StringComparison.Ordinal)
                || !previous.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase)
                || (IsSearching && SessionSearch.Matches(previous, query) != SessionSearch.Matches(session, query)))
            {
                return true;
            }
        }
        return false;
    }

    // Nodes belonging to the tree currently displayed. The TreeView raises Collapsed
    // for nodes being removed during a rebuild — sometimes after the rebuild returns —
    // so expansion changes are only recorded for nodes of the current generation.
    private readonly HashSet<TreeNodeViewModel> _currentNodes = [];

    /// <summary>True while the node is part of the displayed tree (rebuilds reuse nodes).</summary>
    public bool IsInTree(TreeNodeViewModel node) => _currentNodes.Contains(node);

    public void NoteExpansion(TreeNodeViewModel node, bool expanded)
    {
        if (node.IsFolder && _currentNodes.Contains(node))
        {
            (IsSearching ? _filterExpansion : _expansion)[node.ExpansionKey] = expanded;
            node.IsExpanded = expanded; // keep the VM in sync; the view binding is OneWay
        }
    }

    /// <summary>Expands or collapses a folder and every folder beneath it.</summary>
    public void SetExpansionUnder(TreeNodeViewModel node, bool expanded)
    {
        if (!node.IsFolder)
            return;
        node.IsExpanded = expanded;
        (IsSearching ? _filterExpansion : _expansion)[node.ExpansionKey] = expanded;
        foreach (var child in node.Children)
            SetExpansionUnder(child, expanded);
    }

    /// <summary>Expands or collapses every folder in the tree.</summary>
    public void SetExpansionAll(bool expanded)
    {
        foreach (var node in RootNodes)
            SetExpansionUnder(node, expanded);
    }

    /// <summary>Raised after the tree collections are repopulated so the view can re-apply expansion.</summary>
    public event Action? TreeRebuilt;

    public void RebuildTree()
    {
        var allSessions = VisibleSessions.ToList();
        var query = _searchText.Trim();
        _treeSessions = allSessions.ToDictionary(session => session.Id);
        _treeSshFolders = _store.FoldersOf(SessionKind.Ssh);
        _treeLocalFolders = _store.FoldersOf(SessionKind.Local);

        // A matching folder reveals its complete subtree. Otherwise only matching leaves
        // and their ancestors are projected into the filtered view.
        var matchingSshFolders = IsSearching
            ? MatchingFolders(_treeSshFolders, query)
            : [];
        var matchingLocalFolders = IsSearching
            ? MatchingFolders(_treeLocalFolders, query)
            : [];
        var localRootMatches = IsSearching && "Local".Contains(query, StringComparison.OrdinalIgnoreCase);
        var sessions = !IsSearching
            ? allSessions
            : allSessions.Where(s => SessionSearch.Matches(s, query)
                || (s.IsLocal && localRootMatches)
                || IsUnderMatchingFolder(s, s.IsLocal ? matchingLocalFolders : matchingSshFolders)).ToList();
        MatchCount = sessions.Count;
        var localSessions = sessions.Where(s => s.IsLocal).ToList();
        var sshSessions = sessions.Where(s => !s.IsLocal).ToList();

        // While searching, project matching folders/leaves plus the ancestors needed to reach them.
        IEnumerable<string> FoldersFor(SessionKind kind, IEnumerable<Session> matched) => IsSearching
            ? matched.SelectMany(s => FolderPaths.SelfAndAncestors(s.FolderPath))
                .Concat((kind == SessionKind.Local ? matchingLocalFolders : matchingSshFolders)
                    .SelectMany(FolderPaths.SelfAndAncestors))
                .Distinct(StringComparer.OrdinalIgnoreCase)
            : kind == SessionKind.Local ? _treeLocalFolders : _treeSshFolders;

        // Reuse the previous generation's nodes wherever they still describe the same
        // folder or session, then patch the bound collections in place. Clearing and
        // repopulating RootNodes would recreate every container and reset the scroll position.
        var previousFolders = new Dictionary<string, TreeNodeViewModel>(_folderNodes, StringComparer.Ordinal);
        var previousSessions = new Dictionary<Guid, TreeNodeViewModel>(_sessionNodes);
        _currentNodes.Clear();
        _folderNodes.Clear();
        _sessionNodes.Clear();
        var desired = new Dictionary<TreeNodeViewModel, List<TreeNodeViewModel>>();
        var highlight = IsSearching ? query : "";

        TreeNodeViewModel FolderNodeFor(string path, bool isLocalScope, bool isLocalRoot = false)
        {
            var key = ExpansionKeyFor(path, isLocalScope ? SessionKind.Local : SessionKind.Ssh);
            var node = previousFolders.GetValueOrDefault(key)
                ?? (isLocalRoot
                    ? TreeNodeViewModel.ForLocalRoot(false)
                    : TreeNodeViewModel.ForFolder(path, false, isLocalScope));
            node.IsExpanded = ExpansionFor(key);
            _folderNodes[key] = node;
            _currentNodes.Add(node);
            return node;
        }

        TreeNodeViewModel SessionNodeFor(Session session)
        {
            // FolderPath and scope are fixed per node, so a session that moved gets a new leaf.
            if (previousSessions.GetValueOrDefault(session.Id) is { } node
                && node.IsLocalScope == session.IsLocal
                && node.FolderPath.Equals(session.FolderPath, StringComparison.Ordinal))
            {
                if (!ReferenceEquals(node.Session, session))
                    node.UpdateSession(session);
                node.HighlightQuery = highlight;
            }
            else
            {
                node = TreeNodeViewModel.ForSession(session, highlight);
            }
            _sessionNodes[session.Id] = node;
            _currentNodes.Add(node);
            desired[node] = [];
            return node;
        }

        List<TreeNodeViewModel> ChildrenOf(FolderNode folder, bool isLocalScope)
        {
            var children = new List<TreeNodeViewModel>();
            foreach (var sub in folder.Folders)
            {
                var node = FolderNodeFor(sub.FullPath, isLocalScope);
                desired[node] = ChildrenOf(sub, isLocalScope);
                children.Add(node);
            }
            foreach (var session in folder.Sessions)
                children.Add(SessionNodeFor(session));
            return children;
        }

        var roots = new List<TreeNodeViewModel>();

        // The permanent virtual Local root sits first while browsing. While filtering it
        // follows normal match rules: no matching local profile, no Local node.
        if (!IsSearching || localSessions.Count > 0 || matchingLocalFolders.Count > 0 || localRootMatches)
        {
            var localRoot = FolderNodeFor("", isLocalScope: true, isLocalRoot: true);
            var localTree = SessionTreeBuilder.Build(localSessions, FoldersFor(SessionKind.Local, localSessions));
            desired[localRoot] = ChildrenOf(localTree, isLocalScope: true);
            roots.Add(localRoot);
        }

        var root = SessionTreeBuilder.Build(sshSessions, FoldersFor(SessionKind.Ssh, sshSessions));
        roots.AddRange(ChildrenOf(root, isLocalScope: false));

        // Detach everything first (including nodes WinUI moved during a drag) so no node is
        // ever parented twice, then insert and reorder to match the desired tree.
        Detach(RootNodes, roots, desired);
        Place(RootNodes, roots, desired);

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(IsFiltering));
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(MatchSummary));
        TreeRebuilt?.Invoke();
    }

    private static List<string> MatchingFolders(IEnumerable<string> folders, string query) =>
        folders.Where(path => path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

    private static bool IsUnderMatchingFolder(Session session, IEnumerable<string> folders) =>
        folders.Any(folder => FolderPaths.IsSelfOrDescendant(session.FolderPath, folder));

    private bool ExpansionFor(string key) => IsSearching
        ? _filterExpansion.GetValueOrDefault(key, true)
        : _expansion.GetValueOrDefault(key, true);

    /// <summary>Removes nodes from collections they no longer belong in, recursively.</summary>
    private static void Detach(
        ObservableCollection<TreeNodeViewModel> collection,
        List<TreeNodeViewModel>? wanted,
        Dictionary<TreeNodeViewModel, List<TreeNodeViewModel>> desired)
    {
        for (var index = collection.Count - 1; index >= 0; index--)
        {
            var node = collection[index];
            Detach(node.Children, desired.GetValueOrDefault(node), desired);
            if (wanted is null || !wanted.Contains(node))
                collection.RemoveAt(index);
        }
    }

    /// <summary>Inserts and reorders so the collection matches the wanted order. Assumes
    /// <see cref="Detach"/> already removed everything not wanted here.</summary>
    private static void Place(
        ObservableCollection<TreeNodeViewModel> collection,
        List<TreeNodeViewModel> wanted,
        Dictionary<TreeNodeViewModel, List<TreeNodeViewModel>> desired)
    {
        for (var index = 0; index < wanted.Count; index++)
        {
            var node = wanted[index];
            if (index >= collection.Count || !ReferenceEquals(collection[index], node))
            {
                var existing = collection.IndexOf(node);
                if (existing >= 0)
                    collection.RemoveAt(existing);
                collection.Insert(index, node);
            }
            Place(node.Children, desired[node], desired);
        }
    }
}
