using Sessions.Core.Models;

namespace Sessions.Core.Storage;

public sealed class FolderNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public List<FolderNode> Folders { get; } = [];
    public List<Session> Sessions { get; } = [];
}

/// <summary>
/// Builds the folder tree shown in the UI from flat session folder paths plus
/// explicitly-created folders. Folders sort before sessions; both alphabetical.
/// </summary>
public static class SessionTreeBuilder
{
    /// <summary>Returns the virtual root node (FullPath = ""); its children are the top level.</summary>
    public static FolderNode Build(IEnumerable<Session> sessions, IEnumerable<string> folders)
    {
        var root = new FolderNode { Name = "", FullPath = "" };
        var byPath = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };

        FolderNode GetOrCreate(string path)
        {
            path = FolderPaths.Normalize(path);
            if (byPath.TryGetValue(path, out var existing))
                return existing;
            var node = new FolderNode { Name = FolderPaths.Name(path), FullPath = path };
            byPath[path] = node;
            GetOrCreate(FolderPaths.Parent(path)).Folders.Add(node);
            return node;
        }

        foreach (var folder in folders)
        {
            if (FolderPaths.Normalize(folder).Length > 0)
                GetOrCreate(folder);
        }

        foreach (var session in sessions)
            GetOrCreate(session.FolderPath).Sessions.Add(session);

        Sort(root);
        return root;
    }

    private static void Sort(FolderNode node)
    {
        node.Folders.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        node.Sessions.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        foreach (var child in node.Folders)
            Sort(child);
    }
}
