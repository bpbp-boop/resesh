using Resesh.App.Controls;
using Resesh.Core.Layout;
using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>Display data and the backing saved layout for one workspace rail item.</summary>
public sealed class WorkspaceItemViewModel
{
    public Workspace Workspace { get; set; } = new();
    public string Name { get; set; } = "";
    public string LayoutSummary { get; set; } = "";
    public WorkspaceLayoutIconNode Layout { get; set; } = new();

    public static WorkspaceItemViewModel FromWorkspace(Workspace workspace)
    {
        var tabCount = workspace.Groups.Sum(group => group.Tabs.Count);
        var groupLabel = workspace.Groups.Count == 1 ? "group" : "groups";
        var tabLabel = tabCount == 1 ? "tab" : "tabs";
        return new WorkspaceItemViewModel
        {
            Workspace = workspace,
            Name = workspace.Name,
            Layout = CreateLayoutIcon(workspace.Layout, workspace.Groups.Count),
            LayoutSummary = $"{workspace.Groups.Count} {groupLabel}, {tabCount} {tabLabel}",
        };
    }

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
