namespace Resesh.Core.Import;

internal static class ImportKeyPath
{
    public static string? Resolve(string? value, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;
        var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/") || path.StartsWith(@"~\"))
            path = Path.Combine(home, path[2..]);
        return Path.GetFullPath(path, baseDirectory ?? home);
    }
}
