namespace Resesh.Core.Storage;

/// <summary>
/// Helpers for the forward-slash folder path strings used by <see cref="Models.Session.FolderPath"/>.
/// Comparisons are case-insensitive to match Windows user expectations.
/// </summary>
public static class FolderPaths
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var parts = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('/', parts);
    }

    public static string Combine(string parent, string name)
    {
        parent = Normalize(parent);
        name = Normalize(name);
        if (parent.Length == 0)
            return name;
        if (name.Length == 0)
            return parent;
        return $"{parent}/{name}";
    }

    public static string Name(string path)
    {
        path = Normalize(path);
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    public static string Parent(string path)
    {
        path = Normalize(path);
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    /// <summary>Yields the path itself and each ancestor, e.g. "a/b/c" → "a/b/c", "a/b", "a".</summary>
    public static IEnumerable<string> SelfAndAncestors(string path)
    {
        path = Normalize(path);
        while (path.Length > 0)
        {
            yield return path;
            path = Parent(path);
        }
    }

    public static bool IsSelfOrDescendant(string path, string ancestor)
    {
        path = Normalize(path);
        ancestor = Normalize(ancestor);
        return path.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(ancestor + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// If <paramref name="path"/> is <paramref name="oldAncestor"/> or below it, returns the path
    /// re-rooted under <paramref name="newAncestor"/>; otherwise null.
    /// </summary>
    public static string? Reparent(string path, string oldAncestor, string newAncestor)
    {
        path = Normalize(path);
        oldAncestor = Normalize(oldAncestor);
        newAncestor = Normalize(newAncestor);
        if (path.Equals(oldAncestor, StringComparison.OrdinalIgnoreCase))
            return newAncestor;
        if (path.StartsWith(oldAncestor + "/", StringComparison.OrdinalIgnoreCase))
            return newAncestor + path[oldAncestor.Length..];
        return null;
    }
}
