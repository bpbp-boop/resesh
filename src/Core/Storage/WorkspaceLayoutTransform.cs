namespace Resesh.Core.Storage;

/// <summary>Transforms saved layout references without changing surviving pane measurements.</summary>
public static class WorkspaceLayoutTransform
{
    public static WorkspaceLayoutNode? Remap(WorkspaceLayoutNode node, IReadOnlyDictionary<int, int> indices)
    {
        if (node.Orientation is null)
            return indices.TryGetValue(node.GroupIndex, out var index) ? node with { GroupIndex = index } : null;
        var surviving = node.Children.Select((child, index) => (Node: Remap(child, indices), Index: index))
            .Where(item => item.Node is not null).ToList();
        return surviving.Count switch
        {
            0 => null,
            1 => surviving[0].Node,
            _ => node with
            {
                Children = surviving.Select(item => item.Node!).ToList(),
                Sizes = node.Sizes.Count == node.Children.Count
                    ? surviving.Select(item => node.Sizes[item.Index]).ToList() : [],
            },
        };
    }

    public static WorkspaceLayoutNode Offset(WorkspaceLayoutNode node, int offset) =>
        node.Orientation is null
            ? node with { GroupIndex = node.GroupIndex + offset }
            : node with { Children = node.Children.Select(child => Offset(child, offset)).ToList() };
}
