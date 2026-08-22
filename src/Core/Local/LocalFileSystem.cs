using Resesh.Core.Sftp;

namespace Resesh.Core.Local;

/// <summary>Local filesystem operations used by the per-tab file pane.</summary>
public sealed class LocalFileSystem
{
    public string HomeDirectory { get; }

    public LocalFileSystem(string? startingDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(startingDirectory?.Trim() ?? "");
        HomeDirectory = Directory.Exists(expanded)
            ? Path.GetFullPath(expanded)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Resolves a path box or shell-reported path. Null selects the profile home.</summary>
    public string ResolveDirectory(string? path, string? currentDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return HomeDirectory;

        var value = Environment.ExpandEnvironmentVariables(path.Trim());
        if (value == "~")
            value = HomeDirectory;
        else if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            value = Path.Combine(HomeDirectory, value[2..]);
        else if (value.Length >= 3 && value[0] == '/' && char.IsAsciiLetter(value[1]) && value[2] == ':')
            value = value[1..]; // OSC 7 reports Windows drive paths as /C:/folder.

        var basis = currentDirectory is { } current &&
            Path.IsPathFullyQualified(current) && Directory.Exists(current)
            ? current
            : HomeDirectory;
        var resolved = Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(value, basis);
        if (!Directory.Exists(resolved))
            throw new DirectoryNotFoundException($"Folder not found: {resolved}");
        return resolved;
    }

    public IReadOnlyList<RemoteFileEntry> ListDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
            throw new DirectoryNotFoundException($"Folder not found: {directory.FullName}");

        return RemoteFileEntry.Sort(directory.EnumerateFileSystemInfos().Select(ToEntry));
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Rename(RemoteFileEntry entry, string destination)
    {
        if (entry.IsDirectory)
            Directory.Move(entry.FullPath, destination);
        else
            File.Move(entry.FullPath, destination);
    }

    public void Delete(RemoteFileEntry entry)
    {
        if (entry.IsDirectory)
            Directory.Delete(entry.FullPath, recursive: true);
        else
            File.Delete(entry.FullPath);
    }

    private static RemoteFileEntry ToEntry(FileSystemInfo item)
    {
        var isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
        var isSymlink = (item.Attributes & FileAttributes.ReparsePoint) != 0;
        var size = isDirectory ? 0 : ((FileInfo)item).Length;
        return new RemoteFileEntry(item.Name, item.FullName, isDirectory, isSymlink, size, item.LastWriteTime, Mode: -1);
    }
}
