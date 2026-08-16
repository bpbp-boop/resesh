using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sessions.Core.Models;

namespace Sessions.App.ViewModels;

/// <summary>A node in the session tree: either a folder or a session leaf.</summary>
public sealed class TreeNodeViewModel : ObservableObject
{
    // Theme-agnostic selection fill (mid-gray at low alpha reads correctly on dark and light).
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SelectedBrush =
        new(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80));

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
        set
        {
            if (SetProperty(ref _isSelected, value))
                OnPropertyChanged(nameof(SelectionBackground));
        }
    }

    public Microsoft.UI.Xaml.Media.Brush? SelectionBackground => _isSelected ? SelectedBrush : null;

    /// <summary>Null for folders.</summary>
    public Session? Session { get; }

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
        : Session?.Name ?? Sessions.Core.Storage.FolderPaths.Name(FolderPath);

    public string HostSummary => Session switch
    {
        null => "",
        { IsLocal: true } => Path.GetFileNameWithoutExtension(Session.Local?.Executable ?? ""),
        _ => Session.Port == 22 ? Session.Host : $"{Session.Host}:{Session.Port}",
    };

    /// <summary>Per-session color tag as a renderable color; transparent when unset.</summary>
    public Windows.UI.Color TagColor => ParseColor(Session?.ColorTag);

    /// <summary>Session icon image; null shows the default terminal glyph instead.
    /// Tree nodes are recreated on every rebuild, so no change notification is needed.</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? IconSource =>
        App.Icons.GetImage(Session?.Icon, Icons.SessionIconCatalog.ListIconSize);

    public Microsoft.UI.Xaml.Visibility IconVisibility =>
        IconSource is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility DefaultIconVisibility =>
        IconSource is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private static Windows.UI.Color ParseColor(string? hex)
    {
        if (hex is not null && hex.Length == 7 && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Windows.UI.Color.FromArgb(255, r, g, b);
        }
        return Windows.UI.Color.FromArgb(0, 0, 0, 0);
    }

    private TreeNodeViewModel(Session? session, string folderPath, bool isLocalScope, bool isLocalRoot = false)
    {
        Session = session;
        FolderPath = folderPath;
        IsLocalScope = isLocalScope;
        IsLocalRoot = isLocalRoot;
    }

    public static TreeNodeViewModel ForFolder(string fullPath, bool isExpanded, bool isLocalScope = false)
        => new(null, fullPath, isLocalScope) { IsExpanded = isExpanded };

    public static TreeNodeViewModel ForSession(Session session)
        => new(session, session.FolderPath, session.IsLocal);

    public static TreeNodeViewModel ForLocalRoot(bool isExpanded)
        => new(null, "", isLocalScope: true, isLocalRoot: true) { IsExpanded = isExpanded };
}
