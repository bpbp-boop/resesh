using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Search;
using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ViewModelEnvironment _environment;
    private readonly SessionStore _store;
    private readonly ICredentialService _credentials;

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

    public MainViewModel(SessionStore store, ICredentialService credentials, ViewModelEnvironment environment)
    {
        _environment = environment;
        _store = store;
        _credentials = credentials;
        var initial = new TabGroupViewModel();
        Groups.Add(initial);
        _focusedGroup = initial;
        RebuildTree();
    }

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

    public void OnGroupsChanged()
    {
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>The focused group's selected tab — what the status bar describes.</summary>
    public TabViewModel? ActiveTab => FocusedGroup.SelectedTab;

    public void NotifyActiveTabChanged() => OnPropertyChanged(nameof(StatusText));

    public string StatusText
    {
        get
        {
            var baseText = $"{_store.Sessions.Count} sessions";
            var tab = ActiveTab;
            if (tab is null)
                return baseText;
            if (tab.IsOnboarding)
                return $"{baseText}  •  Welcome — setup";
            var status = $"{baseText}  •  {tab.Header} — {tab.Endpoint} • {tab.StateText}";
            return tab.ConnectionSummary.Length > 0 ? $"{status} • {tab.ConnectionSummary}" : status;
        }
    }

    public IReadOnlyList<string> FolderPathsForPicker => _store.Folders;

    public IReadOnlyList<string> LocalFolderPathsForPicker => _store.FoldersOf(SessionKind.Local);

    /// <summary>Built-in local profiles whose shell is not installed right now are hidden
    /// everywhere (tree, search, quick connect) but never deleted.</summary>
    private bool IsVisible(Session session) => _environment.IsSessionVisible(session);

    public IEnumerable<Session> VisibleSessions => _store.Sessions.Where(IsVisible);

    public IReadOnlyList<Session> RankedMatches(string query) => SessionSearch.Rank(VisibleSessions, query);

    // ---- Tabs / groups ----

    public IEnumerable<TabViewModel> AllTabs => Groups.SelectMany(g => g.Tabs);

    /// <summary>This window's tabs as one indicator, for its taskbar button.</summary>
    public TerminalProgress Progress => TerminalProgress.Combine(AllTabs.Select(tab => tab.Progress));

    public TabGroupViewModel GroupOf(TabViewModel tab) =>
        Groups.First(g => g.Tabs.Contains(tab));

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
        if (session.Persistent)
        {
            // Lowest unused slot, so a clone gets its own tmux session and a reopened
            // tab (all others closed) attaches back to the primary.
            var used = AllTabs.Where(t => t.Session.Id == session.Id).Select(t => t.TmuxSlot).ToHashSet();
            while (used.Contains(tab.TmuxSlot))
                tab.TmuxSlot++;
        }
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
    }

    /// <summary>Detaches a live tab without disposing its terminal session.</summary>
    public TabGroupViewModel DetachTab(TabViewModel tab)
    {
        var group = GroupOf(tab);
        tab.PropertyChanged -= Tab_PropertyChanged;
        group.RemoveTab(tab);
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Progress));
        return group;
    }

    private void Tab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.State) or nameof(TabViewModel.ConnectionSummary)
            or nameof(TabViewModel.Header) or nameof(TabViewModel.IsLocked))
        {
            OnPropertyChanged(nameof(StatusText));
        }
        else if (e.PropertyName == nameof(TabViewModel.Progress))
        {
            OnPropertyChanged(nameof(Progress));
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
        foreach (var group in Groups)
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
    }

    // ---- Session CRUD ----

    public void AddSession(Session session, string? password)
    {
        _store.Add(session);
        if (!string.IsNullOrEmpty(password))
            _credentials.Write(session.Id, password);
        RebuildTree();
    }

    public void UpdateSession(Session session, string? password)
    {
        var previous = _store.Find(session.Id);
        _store.Update(session);
        if (!string.IsNullOrEmpty(password))
            _credentials.Write(session.Id, password);
        else if (previous?.AuthMethod == AuthMethod.Password && session.AuthMethod != AuthMethod.Password)
            _credentials.Delete(session.Id);
        foreach (var tab in AllTabs.Where(t => t.Session.Id == session.Id))
        {
            tab.Session = session;
            // Appearance overrides take effect immediately; connection fields apply on next connect.
            _environment.ApplySessionSettings(tab);
        }
        // Preserve realized containers for presentation-only edits (icon, color, endpoint,
        // terminal settings, etc.). Rebuild only when membership, hierarchy, ordering, or
        // the active filter projection may have changed.
        var needsRebuild = previous is null
            || previous.Kind != session.Kind
            || !previous.FolderPath.Equals(session.FolderPath, StringComparison.OrdinalIgnoreCase)
            || !previous.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase)
            || IsVisible(previous) != IsVisible(session)
            || (IsSearching && SearchableFieldsChanged(previous, session));

        if (needsRebuild || !_sessionNodes.TryGetValue(session.Id, out var node))
            RebuildTree();
        else
            node.UpdateSession(session);
    }

    private static bool SearchableFieldsChanged(Session before, Session after) =>
        before.Name != after.Name
        || before.Host != after.Host
        || before.Username != after.Username
        || before.FolderPath != after.FolderPath
        || before.Notes != after.Notes;

    public void DeleteSession(Session session)
    {
        _store.Remove(session.Id);
        _credentials.Delete(session.Id);
        RebuildTree();
    }

    public void MoveSessionsToFolder(IEnumerable<Guid> sessionIds, string folderPath)
    {
        _store.MoveToFolder(sessionIds, folderPath);
        RebuildTree();
    }

    // ---- Folder CRUD (SSH and Local folders are separate namespaces) ----

    public void CreateFolder(string path, SessionKind kind = SessionKind.Ssh)
    {
        path = FolderPaths.Normalize(path);
        if (path.Length == 0 || _store.FoldersOf(kind).Contains(path, StringComparer.OrdinalIgnoreCase))
            return;

        _store.CreateFolder(path, kind);
        RebuildTree();
    }

    public void RenameFolder(string oldPath, string newPath, SessionKind kind = SessionKind.Ssh)
    {
        if (_expansion.Remove(ExpansionKeyFor(oldPath, kind), out var wasExpanded))
            _expansion[ExpansionKeyFor(FolderPaths.Normalize(newPath), kind)] = wasExpanded;
        _store.RenameFolder(oldPath, newPath, kind);
        RebuildTree();
    }

    /// <summary>Number of same-kind sessions that would be removed by deleting this folder.</summary>
    public int CountSessionsUnder(string folderPath, SessionKind kind = SessionKind.Ssh) =>
        _store.Sessions.Count(s => s.Kind == kind && FolderPaths.IsSelfOrDescendant(s.FolderPath, folderPath));

    public void DeleteFolder(string path, SessionKind kind = SessionKind.Ssh)
    {
        foreach (var removed in _store.DeleteFolder(path, kind))
            _credentials.Delete(removed.Id);
        RebuildTree();
    }

    // ---- Tree ----

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

        // A matching folder reveals its complete subtree. Otherwise only matching leaves
        // and their ancestors are projected into the filtered view.
        var matchingSshFolders = IsSearching
            ? MatchingFolders(_store.FoldersOf(SessionKind.Ssh), query)
            : [];
        var matchingLocalFolders = IsSearching
            ? MatchingFolders(_store.FoldersOf(SessionKind.Local), query)
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
            : _store.FoldersOf(kind);

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
