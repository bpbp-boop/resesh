namespace Resesh.Core.Sftp;

/// <summary>
/// Resolves untrusted SFTP leaf names below a user-selected Windows download folder.
/// POSIX servers can use characters such as backslash and colon in a file name, but
/// Windows interprets those characters as path syntax.
/// </summary>
public static class WindowsDownloadPath
{
    private static readonly char[] InvalidNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or "..")
            throw UnsafeName(name);
        if (name.IndexOfAny(InvalidNameCharacters) >= 0 || name.Any(c => c < ' '))
            throw UnsafeName(name);
        if (name.EndsWith(' ') || name.EndsWith('.'))
            throw UnsafeName(name);

        var deviceName = name.Split('.', 2)[0].TrimEnd(' ', '.').ToUpperInvariant();
        if (deviceName is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || IsNumberedDevice(deviceName, "COM")
            || IsNumberedDevice(deviceName, "LPT"))
        {
            throw UnsafeName(name);
        }

        return name;
    }

    public static string Combine(string rootDirectory, string parentDirectory, string remoteName)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDirectory));
        if (!IsContained(root, parent))
            throw new InvalidDataException("The local download path is outside the selected folder.");

        var candidate = Path.GetFullPath(Path.Combine(parent, ValidateName(remoteName)));
        if (!IsContained(root, candidate))
            throw new InvalidDataException($"The remote file name '{remoteName}' resolves outside the selected folder.");

        return candidate;
    }

    private static bool IsContained(string root, string path)
    {
        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumberedDevice(string name, string prefix) =>
        name.Length == 4
        && name.StartsWith(prefix, StringComparison.Ordinal)
        && name[3] is >= '1' and <= '9';

    private static InvalidDataException UnsafeName(string name) =>
        new($"The remote file name '{name}' cannot be saved safely on Windows.");
}
