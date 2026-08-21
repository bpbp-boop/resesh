namespace Resesh.Core.Sftp;

/// <summary>One entry of a remote directory listing, decoupled from SSH.NET's ISftpFile.</summary>
public sealed record RemoteFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsSymlink,
    long Size,
    DateTime Modified,
    /// <summary>Three-digit octal-as-decimal mode (e.g. 755); -1 when unknown.</summary>
    short Mode)
{
    public string PermissionText => UnixPermissions.Format(Mode, IsDirectory, IsSymlink);

    /// <summary>Directories first, then case-insensitive by name — the fixed listing order.</summary>
    public static IReadOnlyList<RemoteFileEntry> Sort(IEnumerable<RemoteFileEntry> entries) =>
        entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
}
