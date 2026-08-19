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

    /// <summary>Resolves an absolute shell path or a path below '~' against the SFTP home.
    /// Other relative text is not a safe remote directory and returns null.</summary>
    public static string? ResolveShellPath(string? path, string homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Normalize(homeDirectory);

        var trimmed = path.Trim();
        if (trimmed == "~")
            return Normalize(homeDirectory);
        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
            return Join(homeDirectory, trimmed[2..]);
        return trimmed.StartsWith('/') ? Normalize(trimmed) : null;
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
