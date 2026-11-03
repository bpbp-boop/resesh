using CommunityToolkit.Mvvm.ComponentModel;
using Resesh.App.Controls;
using Resesh.Core.Layout;
using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>One row of the Workspaces pane: a saved workspace, described as it stands now.
/// The layout it summarises is the one the rail's unsaved mark is decided from: live where
/// a window has the workspace open, else the draft it will open from, else as saved.</summary>
public sealed partial class WorkspaceItemViewModel : ObservableObject
{
    private Workspace _workspace = new();
    private string _name = "";
    private string _layoutSummary = "";
    private WorkspaceLayoutIconNode _layout = new();

    public Workspace Workspace => _workspace;

    public Guid Id => _workspace.Id;

    public string Name => _name;

    /// <summary>Its panes and tabs, then its state as the rail shows it.</summary>
    public string LayoutSummary => _layoutSummary;

    public WorkspaceLayoutIconNode Layout => _layout;

    /// <summary>Takes the saved workspace, the layout it holds now, and the state of its rail
    /// button.</summary>
    public void Update(Workspace workspace, WorkspaceLayout current, WorkspaceRailItemViewModel state)
    {
        _workspace = workspace;
        SetProperty(ref _name, workspace.Name, nameof(Name));
        var summary = Summarize(current);
        if (StateText(state) is { Length: > 0 } text)
            summary += $" · {text}";
        SetProperty(ref _layoutSummary, summary, nameof(LayoutSummary));
        var icon = CreateLayoutIcon(current.Layout, current.Groups.Count);
        if (!SameIcon(icon, _layout))
        {
            _layout = icon;
            OnPropertyChanged(nameof(Layout));
        }
    }

    private static string Summarize(WorkspaceLayout layout)
    {
        var tabCount = layout.Groups.Sum(group => group.Tabs.Count);
        var groupLabel = layout.Groups.Count == 1 ? "group" : "groups";
        var tabLabel = tabCount == 1 ? "tab" : "tabs";
        return $"{layout.Groups.Count} {groupLabel}, {tabCount} {tabLabel}";
    }

    private static string StateText(WorkspaceRailItemViewModel state)
    {
        var parts = new List<string>();
        if (state.IsActive)
            parts.Add("showing");
        else if (state.IsOpen)
            parts.Add("open");
        else if (state.IsOpenElsewhere)
            parts.Add("open in another window");
        if (state.HasUnsavedChanges)
            parts.Add("unsaved changes");
        return string.Join(", ", parts);
    }

    private static bool SameIcon(WorkspaceLayoutIconNode a, WorkspaceLayoutIconNode b) =>
        a.SplitIntoColumns == b.SplitIntoColumns
        && a.Children.Count == b.Children.Count
        && a.Children.Zip(b.Children).All(pair => SameIcon(pair.First, pair.Second));

    private static WorkspaceLayoutIconNode CreateLayoutIcon(
        WorkspaceLayoutNode? node,
        int groupCount)
    {
        if (node is null)
        {
            return new WorkspaceLayoutIconNode
            {
                SplitIntoColumns = true,
                Children = Enumerable.Range(0, groupCount)
                    .Select(_ => new WorkspaceLayoutIconNode())
                    .ToList(),
            };
        }

        return new WorkspaceLayoutIconNode
        {
            SplitIntoColumns = node.Orientation == SplitOrientation.Columns,
            Children = node.Children.Select(child => CreateLayoutIcon(child, groupCount)).ToList(),
        };
    }
}
