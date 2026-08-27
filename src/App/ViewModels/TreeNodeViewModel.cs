using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Resesh.Core.Models;

namespace Resesh.App.ViewModels;

/// <summary>A node in the session tree: either a folder or a session leaf.</summary>
public sealed class TreeNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Explorer-style selection is managed by the window, not the TreeView.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Null for folders. Session leaves can replace their immutable model in place
    /// when an edit does not change tree structure or ordering.</summary>
    public Session? Session { get; private set; }

    /// <summary>Folder full path for folders; owning folder path for sessions. Local-scope
    /// paths are relative to the virtual Local root (their own namespace).</summary>
    public string FolderPath { get; }

    /// <summary>True for the virtual Local root and everything beneath it.</summary>
    public bool IsLocalScope { get; }

    /// <summary>The permanent virtual Local root: cannot be renamed, deleted, or moved.</summary>
    public bool IsLocalRoot { get; }

    /// <summary>Key for the expansion-state map; local folders live in their own namespace.</summary>
    public string ExpansionKey => IsLocalScope ? "\u0000local\u0000" + FolderPath : FolderPath;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public bool IsFolder => Session is null;

    public string Name => IsLocalRoot
        ? "Local"
        : Session?.Name ?? Resesh.Core.Storage.FolderPaths.Name(FolderPath);

    public string HostSummary => Session switch
    {
        null => "",
        { IsLocal: true } => Path.GetFileNameWithoutExtension(Session.Local?.Executable ?? ""),
        _ => Session.Port == 22 ? Session.Host : $"{Session.Host}:{Session.Port}",
    };

    /// <summary>The active tree filter, used only to highlight matching session names.</summary>
    public string HighlightQuery { get; set; } = "";

    public string? ColorTag => Session?.ColorTag;

    public string? IconKey => Session?.Icon;

    private TreeNodeViewModel(Session? session, string folderPath, bool isLocalScope, bool isLocalRoot = false)
    {
        Session = session;
        FolderPath = folderPath;
        IsLocalScope = isLocalScope;
        IsLocalRoot = isLocalRoot;
    }

    public static TreeNodeViewModel ForFolder(string fullPath, bool isExpanded, bool isLocalScope = false)
        => new(null, fullPath, isLocalScope) { IsExpanded = isExpanded };

    public static TreeNodeViewModel ForSession(Session session, string highlightQuery = "")
        => new(session, session.FolderPath, session.IsLocal) { HighlightQuery = highlightQuery };

    public static TreeNodeViewModel ForLocalRoot(bool isExpanded)
        => new(null, "", isLocalScope: true, isLocalRoot: true) { IsExpanded = isExpanded };

    /// <summary>Refreshes a session leaf without replacing its realized tree container.</summary>
    public void UpdateSession(Session session)
    {
        if (Session is null || Session.Id != session.Id)
            throw new InvalidOperationException("Only the session represented by this leaf can be updated.");

        Session = session;
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(HostSummary));
        OnPropertyChanged(nameof(ColorTag));
        OnPropertyChanged(nameof(IconKey));
    }
}
