namespace Resesh.Core.Sftp;

/// <summary>
/// Pure helpers for POSIX-style remote paths (always forward slashes, "/" = root).
/// Kept free of any SSH.NET dependency so they unit-test without a server.
/// </summary>
public static class RemotePath
{
    /// <summary>Collapses duplicate slashes, "." and ".." (lexically, as POSIX "cd -L" does:
    /// ".." at root stays at root) and trims the trailing slash (except for root).</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
    }

    /// <summary>A directory the remote side reported (OSC 7, the /proc probe, tmux, the
    /// session agent, shell integration), or null when it is not one to act on: not
    /// absolute, or holding any control character, C1 included, which could steer the
    /// terminal when the path is shown.</summary>
    public static string? FromUntrusted(string? path) =>
        string.IsNullOrEmpty(path) || !path.StartsWith('/') || path.Any(char.IsControl) ? null : Normalize(path);

    public static string Join(string directory, string name)
    {
        var dir = Normalize(directory);
        return dir == "/" ? "/" + name : dir + "/" + name;
    }

    /// <summary>Resolves path box or shell text: absolute, "~" or below it (against the SFTP
    /// home), or relative to <paramref name="currentDirectory"/> (home when there is none
    /// yet), as a shell's cd would. Null for "~user", whose home only the server knows.</summary>
    public static string? ResolveShellPath(string? path, string homeDirectory, string? currentDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Normalize(homeDirectory);

        var trimmed = path.Trim();
        if (trimmed == "~")
            return Normalize(homeDirectory);
        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
            return Normalize(Join(homeDirectory, trimmed[2..]));
        if (trimmed.StartsWith('~'))
            return null;
        if (trimmed.StartsWith('/'))
            return Normalize(trimmed);
        return Normalize(Join(currentDirectory is ['/', ..] ? currentDirectory : homeDirectory, trimmed));
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

    /// <summary>A name a new or renamed remote entry can take: POSIX forbids only '/' and NUL,
    /// but "." and ".." name the folder itself and its parent.</summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && name is not "." and not ".." && name.IndexOfAny(['/', '\0']) < 0;

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
