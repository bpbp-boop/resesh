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

    // Folder expansion is keyed by path so it survives tree rebuilds. Default: expanded.
    private readonly Dictionary<string, bool> _expansion = new(StringComparer.OrdinalIgnoreCase);

    private string _searchText = "";
    private TabGroupViewModel _focusedGroup;

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = [];

    /// <summary>1 or 2 tab groups (v1 caps at 2, side by side).</summary>
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
                OnPropertyChanged(nameof(StatusText));
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

    public IReadOnlyList<Session> RankedMatches(string query) => SessionSearch.Rank(_store.Sessions, query);

    // ---- Tabs / groups ----

    public IEnumerable<TabViewModel> AllTabs => Groups.SelectMany(g => g.Tabs);

    public TabGroupViewModel GroupOf(TabViewModel tab) =>
        Groups.First(g => g.Tabs.Contains(tab));

    /// <summary>Creates a tab in the given group (or the focused one) and selects it.</summary>
    public TabViewModel Connect(Session session, TabGroupViewModel? group = null)
    {
        group ??= FocusedGroup;
        var tab = new TabViewModel(session);
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
            tab.Session = session;
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

    // ---- Folder CRUD ----

    public void CreateFolder(string path)
    {
        _store.CreateFolder(path);
        RebuildTree();
    }

    public void RenameFolder(string oldPath, string newPath)
    {
        if (_expansion.Remove(oldPath, out var wasExpanded))
            _expansion[FolderPaths.Normalize(newPath)] = wasExpanded;
        _store.RenameFolder(oldPath, newPath);
        RebuildTree();
    }

    /// <summary>Number of sessions that would be removed by deleting this folder.</summary>
    public int CountSessionsUnder(string folderPath) =>
        _store.Sessions.Count(s => FolderPaths.IsSelfOrDescendant(s.FolderPath, folderPath));

    public void DeleteFolder(string path)
    {
        foreach (var removed in _store.DeleteFolder(path))
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
            _expansion[node.FolderPath] = expanded;
            node.IsExpanded = expanded; // keep the VM in sync; the view binding is OneWay
        }
    }

    /// <summary>Raised after the tree collections are repopulated so the view can re-apply expansion.</summary>
    public event Action? TreeRebuilt;

    public void RebuildTree()
    {
        var sessions = SessionSearch.Filter(_store.Sessions, _searchText);

        // While searching, show only folders that contain a match (ancestors included), all expanded.
        IEnumerable<string> folders = IsSearching
            ? sessions.SelectMany(s => FolderPaths.SelfAndAncestors(s.FolderPath)).Distinct(StringComparer.OrdinalIgnoreCase)
            : _store.Folders;

        var root = SessionTreeBuilder.Build(sessions, folders);

        _currentNodes.Clear();
        RootNodes.Clear();
        foreach (var child in BuildChildren(root))
            RootNodes.Add(child);

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsSearching));
        TreeRebuilt?.Invoke();
    }

    private IEnumerable<TreeNodeViewModel> BuildChildren(FolderNode folder)
    {
        foreach (var sub in folder.Folders)
        {
            var expanded = IsSearching || _expansion.GetValueOrDefault(sub.FullPath, true);
            var node = TreeNodeViewModel.ForFolder(sub.FullPath, expanded);
            _currentNodes.Add(node);
            foreach (var child in BuildChildren(sub))
                node.Children.Add(child);
            yield return node;
        }

        foreach (var session in folder.Sessions)
            yield return TreeNodeViewModel.ForSession(session);
    }
}
