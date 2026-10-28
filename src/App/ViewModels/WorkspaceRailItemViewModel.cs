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
    private string _iconKey = "";
    private string _colorTag = "";
    private bool _isOpen;
    private bool _isActive;
    private bool _isOpenElsewhere;
    private WorkspaceAlert _alert;
    private int _position;

    private WorkspaceRailItemViewModel(Guid id, string name)
    {
        Id = id;
        _name = name;
        _monogram = WorkspaceAppearance.Monogram(name);
    }

    public static WorkspaceRailItemViewModel CreateHome() => new(Guid.Empty, HomeName) { _isOpen = true };

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

    /// <summary>The chosen icon's catalog key, or "" to draw the monogram. Never null: x:Bind
    /// function bindings skip an update to null and would keep showing the old icon.</summary>
    public string IconKey => _iconKey;

    public string ColorTag => _colorTag;

    /// <summary>This window holds the workspace's tabs, shown or not. Home is always open.</summary>
    public bool IsOpen => _isOpen;

    /// <summary>The window is showing this workspace.</summary>
    public bool IsActive => _isActive;

    /// <summary>Another window has the workspace open; clicking goes to that window.</summary>
    public bool IsOpenElsewhere => _isOpenElsewhere;

    /// <summary>The background alert, shown only while the workspace is not on screen.</summary>
    public WorkspaceAlert Alert => _alert;

    public bool HasAlert => _alert != WorkspaceAlert.None;

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
            if (_alert != WorkspaceAlert.None)
                parts.Add(WorkspaceAlerts.Describe(_alert));
            var text = parts.Count == 0 ? _name : $"{_name} — {string.Join(", ", parts)}";
            if (ShortcutLabel is { } shortcut)
                text += $" ({shortcut})";
            return text;
        }
    }

    public string AutomationName => Tooltip;

    public string AutomationId => IsHome ? "WorkspaceRail_Home" : $"WorkspaceRail_{Id:N}";

    private string? ShortcutLabel =>
        _position is >= 1 and <= 9 ? KeyBindings.Get(ShortcutIds.GoToWorkspace).Chords[_position - 1].Label : null;

    /// <summary>Takes the saved name, icon and colour after a rename or customisation.</summary>
    public void Update(Workspace workspace)
    {
        _workspace = workspace;
        Set(ref _name, workspace.Name, nameof(Name));
        Set(ref _monogram, WorkspaceAppearance.Monogram(workspace.Name), nameof(Monogram));
        Set(ref _iconKey, workspace.Icon ?? "", nameof(IconKey));
        Set(ref _colorTag, WorkspaceAppearance.Color(workspace), nameof(ColorTag));
        NotifyState();
    }

    public void SetState(bool isOpen, bool isActive, bool isOpenElsewhere, WorkspaceAlert alert, int position)
    {
        var changed = false;
        changed |= Set(ref _isOpen, isOpen || IsHome, nameof(IsOpen));
        changed |= Set(ref _isActive, isActive, nameof(IsActive));
        changed |= Set(ref _isOpenElsewhere, isOpenElsewhere && !isOpen, nameof(IsOpenElsewhere));
        changed |= Set(ref _alert, isActive ? WorkspaceAlert.None : alert, nameof(Alert));
        changed |= Set(ref _position, position, nameof(Position));
        if (changed)
            NotifyState();
    }

    /// <summary>Re-reads the colour and icon, for a theme change the view draws them for.</summary>
    public void NotifyAppearanceChanged()
    {
        OnPropertyChanged(nameof(ColorTag));
        OnPropertyChanged(nameof(IconKey));
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasAlert));
        OnPropertyChanged(nameof(MarkerHeight));
        OnPropertyChanged(nameof(TileOpacity));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(AutomationName));
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
