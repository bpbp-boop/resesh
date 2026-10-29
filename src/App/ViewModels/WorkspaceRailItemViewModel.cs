using CommunityToolkit.Mvvm.ComponentModel;
using Resesh.Core.Agents;
using Resesh.Core.Input;
using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>What a workspace in the background wants the user to know, most urgent last.</summary>
public enum WorkspaceAlert
{
    None,

    /// <summary>A tab printed output nobody has seen.</summary>
    Activity,

    /// <summary>A session dropped its connection.</summary>
    Disconnected,

    /// <summary>An agent is waiting for an approval or an answer.</summary>
    Attention,
}

public static class WorkspaceAlerts
{
    /// <summary>The one alert a rail button shows for these tabs: the most urgent any has.</summary>
    public static WorkspaceAlert Summarize(IEnumerable<TabViewModel> tabs)
    {
        var alert = WorkspaceAlert.None;
        foreach (var tab in tabs)
        {
            if (tab.Agent.Attention is AgentAttention.NeedsApproval or AgentAttention.NeedsAnswer)
                return WorkspaceAlert.Attention;
            if (tab.State == TabConnectionState.Disconnected && !tab.IsAppPage && !tab.IsPlayback)
                alert = WorkspaceAlert.Disconnected;
            else if (tab.HasUnseenOutput && alert == WorkspaceAlert.None)
                alert = WorkspaceAlert.Activity;
        }
        return alert;
    }

    public static string Describe(WorkspaceAlert alert) => alert switch
    {
        WorkspaceAlert.Attention => "an agent needs you",
        WorkspaceAlert.Disconnected => "a session disconnected",
        WorkspaceAlert.Activity => "new output",
        _ => "",
    };
}

/// <summary>One button in the window's workspace rail: Home (the tabs that belong to no saved
/// workspace) or a saved workspace, with whether this window has it open or showing.</summary>
public sealed partial class WorkspaceRailItemViewModel : ObservableObject
{
    public const string HomeName = "Home";

    private Workspace? _workspace;
    private string _name;
    private string _monogram;
    private string _glyph = "";
    private string _iconPath = "";
    private bool _labelIcon;
    private bool _isOpen;
    private bool _isActive;
    private bool _isOpenElsewhere;
    private WorkspaceAlert _alert;
    private bool _hasUnsavedChanges;
    private int _position;

    private WorkspaceRailItemViewModel(Guid id, string name)
    {
        Id = id;
        _name = name;
        _monogram = WorkspaceAppearance.Monogram(name);
    }

    public static WorkspaceRailItemViewModel CreateHome() =>
        new(Guid.Empty, HomeName) { _isOpen = true, _glyph = WorkspaceAppearance.HomeGlyph };

    public static WorkspaceRailItemViewModel For(Workspace workspace)
    {
        var item = new WorkspaceRailItemViewModel(workspace.Id, workspace.Name);
        item.Update(workspace);
        return item;
    }

    /// <summary>The saved workspace's id, or <see cref="Guid.Empty"/> for Home.</summary>
    public Guid Id { get; }

    public bool IsHome => Id == Guid.Empty;

    /// <summary>The saved workspace this button opens; null for Home.</summary>
    public Workspace? Workspace => _workspace;

    public string Name => _name;

    public string Monogram => _monogram;

    /// <summary>The icon's glyph, or "" to draw the monogram. Never null: x:Bind skips an
    /// update to null and would keep showing the old icon.</summary>
    public string Glyph => _glyph;

    /// <summary>Path data for an icon the font has no glyph for, or "".</summary>
    public string IconPath => _iconPath;

    /// <summary>A glyph or a path is drawn; otherwise the monogram is.</summary>
    public bool HasIcon => _glyph.Length > 0 || _iconPath.Length > 0;

    /// <summary>The initials drawn small under the icon, or "" for none.</summary>
    public string Caption => HasIcon && _labelIcon ? _monogram : "";

    public bool HasCaption => Caption.Length > 0;

    /// <summary>This window holds the workspace's tabs, shown or not. Home is always open.</summary>
    public bool IsOpen => _isOpen;

    /// <summary>The window is showing this workspace.</summary>
    public bool IsActive => _isActive;

    /// <summary>Another window has the workspace open; clicking goes to that window.</summary>
    public bool IsOpenElsewhere => _isOpenElsewhere;

    /// <summary>The background alert, shown only while the workspace is not on screen.</summary>
    public WorkspaceAlert Alert => _alert;

    public bool HasAlert => _alert != WorkspaceAlert.None;

    /// <summary>Its tabs or panes differ from its saved layout, live or in a kept draft.</summary>
    public bool HasUnsavedChanges => _hasUnsavedChanges;

    /// <summary>The unsaved mark shares the button's corner with the other-window mark, which
    /// wins: that window is where the changes are.</summary>
    public bool ShowsUnsavedMark => _hasUnsavedChanges && !_isOpenElsewhere;

    /// <summary>1-based place in the rail, which its Ctrl+Shift+number shortcut follows.</summary>
    public int Position => _position;

    /// <summary>Discord-style marker at the rail's edge: tall for the shown workspace,
    /// a short stub for one open in the background, nothing for a closed one.</summary>
    public double MarkerHeight => _isActive ? 20 : _isOpen || _isOpenElsewhere ? 8 : 0;

    /// <summary>Closed workspaces draw dimmed, so live ones stand out.</summary>
    public double TileOpacity => _isActive || _isOpen || _isOpenElsewhere ? 1.0 : 0.6;

    public string Tooltip
    {
        get
        {
            var parts = new List<string>();
            if (IsHome)
                parts.Add("tabs outside saved workspaces");
            else if (_isActive)
                parts.Add("showing");
            else if (_isOpen)
                parts.Add("open");
            else if (_isOpenElsewhere)
                parts.Add("open in another window");
            if (_hasUnsavedChanges)
                parts.Add("unsaved changes");
            if (_alert != WorkspaceAlert.None)
                parts.Add(WorkspaceAlerts.Describe(_alert));
            var text = parts.Count == 0 ? _name : $"{_name} — {string.Join(", ", parts)}";
            if (ShortcutLabel is { } shortcut)
                text += $" ({shortcut})";
            return text;
        }
    }

    public string AutomationName => Tooltip;

    /// <summary>The hover card's status line.</summary>
    public string StatusText
    {
        get
        {
            var state =
                IsHome ? (_isActive ? "Showing · tabs outside saved workspaces" : "Tabs outside saved workspaces")
                : _isActive ? "Showing"
                : _isOpen ? "Open in the background"
                : _isOpenElsewhere ? "Open in another window"
                : "Not open";
            return _hasUnsavedChanges ? state + " · unsaved changes" : state;
        }
    }

    /// <summary>The background alert as a sentence for the hover card, or "".</summary>
    public string AlertText
    {
        get
        {
            var text = WorkspaceAlerts.Describe(_alert);
            return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];
        }
    }

    /// <summary>The switch shortcut for this position, or "".</summary>
    public string ShortcutText => ShortcutLabel ?? "";

    public string AutomationId => IsHome ? "WorkspaceRail_Home" : $"WorkspaceRail_{Id:N}";

    private string? ShortcutLabel =>
        _position is >= 1 and <= 9 ? KeyBindings.Get(ShortcutIds.GoToWorkspace).Chords[_position - 1].Label : null;

    /// <summary>Takes the saved name and icon after a rename or a new icon.</summary>
    public void Update(Workspace workspace)
    {
        _workspace = workspace;
        Set(ref _name, workspace.Name, nameof(Name));
        Set(ref _monogram, WorkspaceAppearance.Initials(workspace), nameof(Monogram));
        var icon = WorkspaceAppearance.Find(workspace.Icon);
        var iconChanged = Set(ref _glyph, icon?.Glyph ?? "", nameof(Glyph));
        iconChanged |= Set(ref _iconPath, icon?.Path ?? "", nameof(IconPath));
        if (iconChanged)
            OnPropertyChanged(nameof(HasIcon));
        _labelIcon = workspace.LabelIcon;
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(HasCaption));
        NotifyState();
    }

    public void SetState(
        bool isOpen, bool isActive, bool isOpenElsewhere, WorkspaceAlert alert, int position, bool hasUnsavedChanges = false)
    {
        var changed = false;
        changed |= Set(ref _hasUnsavedChanges, hasUnsavedChanges && !IsHome, nameof(HasUnsavedChanges));
        changed |= Set(ref _isOpen, isOpen || IsHome, nameof(IsOpen));
        changed |= Set(ref _isActive, isActive, nameof(IsActive));
        changed |= Set(ref _isOpenElsewhere, isOpenElsewhere && !isOpen, nameof(IsOpenElsewhere));
        changed |= Set(ref _alert, isActive ? WorkspaceAlert.None : alert, nameof(Alert));
        changed |= Set(ref _position, position, nameof(Position));
        if (changed)
            NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasAlert));
        OnPropertyChanged(nameof(ShowsUnsavedMark));
        OnPropertyChanged(nameof(MarkerHeight));
        OnPropertyChanged(nameof(TileOpacity));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AlertText));
        OnPropertyChanged(nameof(ShortcutText));
    }

    private bool Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
