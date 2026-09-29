namespace Resesh.Core.Storage;

/// <summary>Default data locations: %APPDATA%\Resesh for roaming user data (sessions,
/// settings, keys, icons) and %LOCALAPPDATA%\Resesh for machine-local data (history,
/// caches, logs). The app redirects stores for --data-dir and demo mode on top of these.</summary>
public static class AppDataPaths
{
    public static string Roaming(params string[] parts) =>
        Combine(Environment.SpecialFolder.ApplicationData, parts);

    public static string Local(params string[] parts) =>
        Combine(Environment.SpecialFolder.LocalApplicationData, parts);

    private static string Combine(Environment.SpecialFolder root, string[] parts) =>
        Path.Combine([Environment.GetFolderPath(root), "Resesh", .. parts]);
}
