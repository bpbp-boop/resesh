using System.Text.Json;

namespace Resesh.Core.Storage;

/// <summary>What <see cref="AtomicFile.Load{T}"/> read. <see cref="PreserveBackup"/> is true
/// when the data came from the .bak, so the first save must not rotate the unreadable primary
/// over the only good copy. <see cref="Warning"/> is null when nothing needs the user's attention.</summary>
internal readonly record struct StoreLoad<T>(T? Data, bool PreserveBackup, string? Warning) where T : class;

internal static class AtomicFile
{
    public static void Write(string path, string contents, bool preserveBackup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        if (File.Exists(path))
            File.Replace(temporary, path, preserveBackup ? null : path + ".bak");
        else
            File.Move(temporary, path);
    }

    /// <summary>
    /// Reads a store file, falling back to its .bak. A file that exists but cannot be read is
    /// copied aside to "*.unreadable-{timestamp}" first, so the saves that follow (which rotate
    /// the primary into .bak and then overwrite it) can never destroy the user's only copy.
    /// <paramref name="read"/> may throw or return null for an invalid file.
    /// </summary>
    public static StoreLoad<T> Load<T>(string path, Func<string, T?> read, string contents) where T : class
    {
        var primary = TryRead(path, read, out var primaryUnreadable);
        if (primary is not null)
            return new(primary, false, null);

        var backupPath = path + ".bak";
        var backup = TryRead(backupPath, read, out var backupUnreadable);
        var kept = new List<string>();
        if (primaryUnreadable)
            Quarantine(path, kept);
        if (backupUnreadable)
            Quarantine(backupPath, kept);
        var keptNote = kept.Count > 0 ? $" The unreadable file was kept as {string.Join(" and ", kept)}." : "";

        if (backup is not null)
            return new(backup, true, $"{contents} were recovered from the backup file.{keptNote}");
        return new(null, false, primaryUnreadable || backupUnreadable
            ? $"{contents} could not be loaded.{keptNote}"
            : null);
    }

    private static T? TryRead<T>(string path, Func<string, T?> read, out bool unreadable) where T : class
    {
        unreadable = false;
        try
        {
            if (!File.Exists(path))
                return null;
            var data = read(path);
            unreadable = data is null;
            return data;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            unreadable = true;
            return null;
        }
    }

    private static void Quarantine(string path, List<string> kept)
    {
        var copy = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Copy(path, copy, overwrite: false);
            kept.Add(copy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the .bak rotation still keeps one generation of the original.
        }
    }
}
