namespace Sessions.Core.Sftp;

/// <summary>
/// Pure helpers for POSIX-style remote paths (always forward slashes, "/" = root).
/// Kept free of any SSH.NET dependency so they unit-test without a server.
/// </summary>
public static class RemotePath
{
    /// <summary>Collapses duplicate slashes and trims the trailing slash (except for root).</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "/" : "/" + string.Join('/', parts);
    }

    public static string Join(string directory, string name)
    {
        var dir = Normalize(directory);
        return dir == "/" ? "/" + name : dir + "/" + name;
    }

    /// <summary>Parent directory; the parent of root is root.</summary>
    public static string Parent(string path)
    {
        var normalized = Normalize(path);
        if (normalized == "/")
            return "/";
        var cut = normalized.LastIndexOf('/');
        return cut == 0 ? "/" : normalized[..cut];
    }

    public static string FileName(string path)
    {
        var normalized = Normalize(path);
        return normalized == "/" ? "/" : normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// First name (base, "base (2)", "base (3)", …) for which <paramref name="taken"/> is
    /// false — the Explorer-style collision policy for downloads landing next to a file
    /// that already exists. The extension is preserved ("log.txt" → "log (2).txt").
    /// </summary>
    public static string UniqueName(string name, Func<string, bool> taken)
    {
        if (!taken(name))
            return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){extension}";
            if (!taken(candidate))
                return candidate;
        }
    }
}
