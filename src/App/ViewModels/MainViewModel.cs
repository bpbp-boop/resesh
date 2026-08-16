using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sessions.Core.Credentials;
using Sessions.Core.Models;
using Sessions.Core.Search;
using Sessions.Core.Storage;

namespace Sessions.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SessionStore _store;
    private readonly ICredentialService _credentials;

    // Folder expansion is keyed by TreeNodeViewModel.ExpansionKey (path, with a reserved
    // prefix for the Local scope) so it survives tree rebuilds. Default: expanded.
    private readonly Dictionary<string, bool> _expansion = new(StringComparer.OrdinalIgnoreCase);

    private static string ExpansionKeyFor(string folderPath, SessionKind kind) =>
        kind == SessionKind.Local ? "\u0000local\u0000" + folderPath : folderPath;

    private string _searchText = "";
    private TabGroupViewModel _focusedGroup;

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = [];

    /// <summary>All tab groups in visual traversal order.</summary>
    public ObservableCollection<TabGroupViewModel> Groups { get; } = [];

    public MainViewModel(SessionStore store, ICredentialService credentials)
    {
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
            if (SetProperty(ref _searchText, value))
                RebuildTree();
        }
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchText);

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
            var status = $"{baseText}  •  {tab.Header} — {tab.Endpoint} • {tab.StateText}";
            return tab.ConnectionSummary.Length > 0 ? $"{status} • {tab.ConnectionSummary}" : status;
        }
    }

    public IReadOnlyList<string> FolderPathsForPicker => _store.Folders;

    public IReadOnlyList<string> LocalFolderPathsForPicker => _store.FoldersOf(SessionKind.Local);

    /// <summary>Built-in local profiles whose shell is not installed right now are hidden
    /// everywhere (tree, search, quick connect) but never deleted.</summary>
    private static bool IsVisible(Session session) =>
        !session.IsLocal || !session.BuiltIn || App.AvailableLocalShells.Contains(session.Id);

    public IEnumerable<Session> VisibleSessions => _store.Sessions.Where(IsVisible);

    public IReadOnlyList<Session> RankedMatches(string query) => SessionSearch.Rank(VisibleSessions, query);

    // ---- Tabs / groups ----

    public IEnumerable<TabViewModel> AllTabs => Groups.SelectMany(g => g.Tabs);

    public TabGroupViewModel GroupOf(TabViewModel tab) =>
        Groups.First(g => g.Tabs.Contains(tab));

    /// <summary>Creates a tab in the given group (or the focused one) and selects it.</summary>
    public TabViewModel Connect(Session session, TabGroupViewModel? group = null)
    {
        group ??= FocusedGroup;
        var tab = new TabViewModel(session);
        if (session.Persistent)
        {
            // Lowest unused slot, so a clone gets its own tmux session and a reopened
            // tab (all others closed) attaches back to the primary.
            var used = AllTabs.Where(t => t.Session.Id == session.Id).Select(t => t.TmuxSlot).ToHashSet();
            while (used.Contains(tab.TmuxSlot))
                tab.TmuxSlot++;
        }
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TabViewModel.State) or nameof(TabViewModel.ConnectionSummary)
                or nameof(TabViewModel.Header))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        };
        group.Tabs.Add(tab);
        group.SelectedTab = tab;
        tab.IsGroupFocused = group == _focusedGroup;
        OnPropertyChanged(nameof(StatusText));
        return tab;
    }

    /// <summary>Removes and disposes the tab. Collapsing empty groups is the window's job.</summary>
    public void CloseTab(TabViewModel tab)
    {
        var group = GroupOf(tab);
        group.Tabs.Remove(tab);
        (tab.View as IDisposable)?.Dispose();
        if (group.SelectedTab == tab)
            group.SelectedTab = group.Tabs.LastOrDefault();
        OnPropertyChanged(nameof(StatusText));
    }

    public void CloseAllTabs()
    {
        foreach (var group in Groups)
        {
            foreach (var tab in group.Tabs.ToList())
                (tab.View as IDisposable)?.Dispose();
            group.Tabs.Clear();
            group.SelectedTab = null;
        }
        OnPropertyChanged(nameof(StatusText));
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
        _store.Update(session);
        if (!string.IsNullOrEmpty(password))
            _credentials.Write(session.Id, password);
        foreach (var tab in AllTabs.Where(t => t.Session.Id == session.Id))
        {
            tab.Session = session;
            // Appearance overrides take effect immediately; connection fields apply on next connect.
            (tab.View as Terminal.TerminalTabView)?.ApplySettings(App.Settings.Current);
        }
        RebuildTree();
    }

    public void DeleteSession(Session session)
    {
        _store.Remove(session.Id);
        _credentials.Delete(session.Id);
        RebuildTree();
    }

    public void MoveSessionToFolder(Guid sessionId, string folderPath)
    {
        _store.MoveToFolder(sessionId, folderPath);
        RebuildTree();
    }

    // ---- Folder CRUD (SSH and Local folders are separate namespaces) ----

    public void CreateFolder(string path, SessionKind kind = SessionKind.Ssh)
    {
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

    public void NoteExpansion(TreeNodeViewModel node, bool expanded)
    {
        if (node.IsFolder && !IsSearching && _currentNodes.Contains(node))
        {
            _expansion[node.ExpansionKey] = expanded;
            node.IsExpanded = expanded; // keep the VM in sync; the view binding is OneWay
        }
    }

    /// <summary>Expands or collapses a folder and every folder beneath it.</summary>
    public void SetExpansionUnder(TreeNodeViewModel node, bool expanded)
    {
        if (!node.IsFolder)
            return;
        node.IsExpanded = expanded;
        if (!IsSearching)
            _expansion[node.ExpansionKey] = expanded;
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
        var sessions = SessionSearch.Filter(VisibleSessions, _searchText);
        var localSessions = sessions.Where(s => s.IsLocal).ToList();
        var sshSessions = sessions.Where(s => !s.IsLocal).ToList();

        // While searching, show only folders that contain a match (ancestors included), all expanded.
        IEnumerable<string> FoldersFor(SessionKind kind, IEnumerable<Session> matched) => IsSearching
            ? matched.SelectMany(s => FolderPaths.SelfAndAncestors(s.FolderPath)).Distinct(StringComparer.OrdinalIgnoreCase)
            : _store.FoldersOf(kind);

        _currentNodes.Clear();
        RootNodes.Clear();

        // The permanent virtual Local root sits first while browsing. While filtering it
        // follows normal match rules: no matching local profile, no Local node.
        if (!IsSearching || localSessions.Count > 0)
        {
            var localRoot = TreeNodeViewModel.ForLocalRoot(
                IsSearching || _expansion.GetValueOrDefault(ExpansionKeyFor("", SessionKind.Local), true));
            _currentNodes.Add(localRoot);
            var localTree = SessionTreeBuilder.Build(localSessions, FoldersFor(SessionKind.Local, localSessions));
            foreach (var child in BuildChildren(localTree, isLocalScope: true))
                localRoot.Children.Add(child);
            RootNodes.Add(localRoot);
        }

        var root = SessionTreeBuilder.Build(sshSessions, FoldersFor(SessionKind.Ssh, sshSessions));
        foreach (var child in BuildChildren(root, isLocalScope: false))
            RootNodes.Add(child);

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsSearching));
        TreeRebuilt?.Invoke();
    }

    private IEnumerable<TreeNodeViewModel> BuildChildren(FolderNode folder, bool isLocalScope)
    {
        foreach (var sub in folder.Folders)
        {
            var kind = isLocalScope ? SessionKind.Local : SessionKind.Ssh;
            var expanded = IsSearching || _expansion.GetValueOrDefault(ExpansionKeyFor(sub.FullPath, kind), true);
            var node = TreeNodeViewModel.ForFolder(sub.FullPath, expanded, isLocalScope);
            _currentNodes.Add(node);
            foreach (var child in BuildChildren(sub, isLocalScope))
                node.Children.Add(child);
            yield return node;
        }

        foreach (var session in folder.Sessions)
            yield return TreeNodeViewModel.ForSession(session);
    }
}
