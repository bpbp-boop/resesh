namespace Resesh.Core.Storage;

/// <summary>
/// The one way a private key file's path is read, wherever it comes from (an imported
/// config, a saved session, the key list): quotes and surrounding spaces dropped,
/// environment variables and a leading "~" expanded, and a relative path taken from the
/// user's home (or a given folder, such as the config file's), never the process's current
/// directory, which for a GUI app is wherever it happened to be launched from. Paths that
/// name the same file then compare equal.
/// </summary>
public static class KeyFilePath
{
    /// <summary>The full path. Throws as <see cref="Path.GetFullPath(string, string)"/> does
    /// for text that is not a path, blank text included: resolved against the home folder it
    /// would otherwise name the home folder itself, and a key list entry or a match against
    /// one would point at a folder.</summary>
    public static string Normalize(string path, string? baseDirectory = null)
    {
        var value = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A key file path is required.", nameof(path));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (value == "~")
            value = home;
        else if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith(@"~\", StringComparison.Ordinal))
            value = Path.Combine(home, value[2..]);
        return Path.GetFullPath(value, baseDirectory ?? home);
    }
}
