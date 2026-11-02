using System.Text.Json;

namespace Resesh.Core.Storage;

/// <summary>Where a JSON store's in-memory state came from, and so whether it may be saved.</summary>
public enum StoreLoadState
{
    /// <summary><c>Load</c> has not run. Saving would replace a file nobody has read, so it is
    /// refused while the file (or its .bak) exists.</summary>
    NotLoaded,

    /// <summary>Read from the primary file, or started empty because there was no file, or
    /// started empty after an invalid file was copied aside. Saves are allowed.</summary>
    Loaded,

    /// <summary>Read from the .bak after the primary was found unreadable and copied aside.
    /// The first save must not rotate the primary over the only good copy.</summary>
    RecoveredFromBackup,

    /// <summary>A file exists that could not be read and could not be copied aside either
    /// (an antivirus scanner or sync client held it, or access was denied). Whatever the
    /// store holds is shown, but every save is refused: rotating the file into .bak and then
    /// over .bak would destroy the user's data.</summary>
    Unreadable,
}

/// <summary>Thrown by a store save that would overwrite a file it could not read. An
/// <see cref="IOException"/> so callers that already report failed writes report this too.</summary>
public sealed class StoreReadOnlyException(string message) : IOException(message);

/// <summary>What <see cref="AtomicFile.Load{T}"/> read. <see cref="Warning"/> is null when
/// nothing needs the user's attention.</summary>
internal readonly record struct StoreLoad<T>(T? Data, StoreLoadState State, string? Warning) where T : class;

/// <summary>How reading one file went.</summary>
internal enum FileReadOutcome
{
    Read,
    Missing,
    /// <summary>The bytes were read but did not parse or validate.</summary>
    Invalid,
    /// <summary>The file exists but could not be opened or read.</summary>
    Inaccessible,
}

internal static class AtomicFile
{
    /// <summary>Waits between attempts to read a file another process holds unshared.
    /// Antivirus scanners and sync clients usually let go within a few hundred milliseconds.
    /// Settable so tests need not wait.</summary>
    internal static TimeSpan[] SharingViolationRetryDelays { get; set; } =
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300)];

    /// <summary>Test hook run before each sharing-violation retry, so a test can let go of the
    /// file at a known point instead of racing a timer. Per thread: the retry runs on the
    /// reading thread, and other tests holding locks in parallel must not see it.</summary>
    [ThreadStatic]
    internal static Action? BeforeSharingViolationRetry;

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
    /// When that copy cannot be made, the result is <see cref="StoreLoadState.Unreadable"/> and
    /// the store must not save. <paramref name="read"/> may throw or return null for an invalid file.
    /// </summary>
    public static StoreLoad<T> Load<T>(string path, Func<string, T?> read, string contents) where T : class
    {
        var primary = TryRead(path, read, out var primaryOutcome, out var primaryError);
        if (primary is not null)
            return new(primary, StoreLoadState.Loaded, null);

        var backupPath = path + ".bak";
        var backup = TryRead(backupPath, read, out var backupOutcome, out var backupError);
        var kept = new List<string>();
        var lost = new List<string>();
        string? reason = null;
        foreach (var (file, outcome, error) in new[] { (path, primaryOutcome, primaryError), (backupPath, backupOutcome, backupError) })
        {
            if (outcome is not (FileReadOutcome.Invalid or FileReadOutcome.Inaccessible))
                continue;
            if (TryQuarantine(file) is { } copy)
                kept.Add(copy);
            else
            {
                lost.Add(file);
                reason ??= error;
            }
        }

        if (lost.Count > 0)
        {
            var shown = backup is not null ? " The backup copy is shown instead." : "";
            return new(backup, StoreLoadState.Unreadable,
                $"{contents} could not be read from {string.Join(" and ", lost)} ({reason}).{shown} "
                + "Changes will not be saved, so the file is not overwritten. Close any program that "
                + "may be holding it and restart resesh.");
        }
        var keptNote = kept.Count > 0 ? $" The unreadable file was kept as {string.Join(" and ", kept)}." : "";
        if (backup is not null)
            return new(backup, StoreLoadState.RecoveredFromBackup, $"{contents} were recovered from the backup file.{keptNote}");
        return new(null, StoreLoadState.Loaded, kept.Count > 0 ? $"{contents} could not be loaded.{keptNote}" : null);
    }

    /// <summary>Reads one file, retrying briefly while another process holds it unshared.</summary>
    internal static T? TryRead<T>(string path, Func<string, T?> read, out FileReadOutcome outcome, out string? error)
        where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    (outcome, error) = (FileReadOutcome.Missing, null);
                    return null;
                }
                var data = read(path);
                (outcome, error) = data is null ? (FileReadOutcome.Invalid, "the file holds no data") : (FileReadOutcome.Read, null);
                return data;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                (outcome, error) = (FileReadOutcome.Missing, null);
                return null;
            }
            catch (IOException e) when (IsSharingViolation(e) && attempt < SharingViolationRetryDelays.Length)
            {
                BeforeSharingViolationRetry?.Invoke();
                Thread.Sleep(SharingViolationRetryDelays[attempt]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                (outcome, error) = (FileReadOutcome.Inaccessible, e.Message);
                return null;
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException)
            {
                (outcome, error) = (FileReadOutcome.Invalid, e.Message);
                return null;
            }
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION: another process has it open.</summary>
    private static bool IsSharingViolation(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;

    /// <summary>Copies an unreadable file aside and returns the copy's path, or null when the
    /// copy failed, in which case no save may rotate over the original.</summary>
    internal static string? TryQuarantine(string path)
    {
        var stem = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
        var copy = stem;
        for (var suffix = 2; File.Exists(copy); suffix++)
            copy = $"{stem}-{suffix}";
        try
        {
            File.Copy(path, copy, overwrite: false);
            return copy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// One store's file and its <see cref="StoreLoadState"/>. Stores load and save through this
/// so none can save before it has loaded, or over a file it could not read.
/// Not thread-safe: callers hold their store's lock.
/// </summary>
internal sealed class StoreFile(string path, string contents)
{
    public string Path { get; } = path;
    public StoreLoadState State { get; private set; } = StoreLoadState.NotLoaded;
    public string? Warning { get; private set; }

    public T? Load<T>(Func<string, T?> read) where T : class
    {
        var load = AtomicFile.Load(Path, read, contents);
        State = load.State;
        Warning = load.Warning;
        return load.Data;
    }

    /// <summary>Takes a state worked out by a store with its own load rules (known hosts).</summary>
    public void SetLoaded(StoreLoadState state, string? warning)
    {
        State = state;
        Warning = warning;
    }

    /// <summary>Throws when the store has not loaded a file that exists. A programming error
    /// rather than a disk problem: the empty in-memory state is not what the file holds. With
    /// no file at all there is nothing to misrepresent or overwrite, so that is allowed.</summary>
    public void EnsureLoaded()
    {
        if (State == StoreLoadState.NotLoaded && (File.Exists(Path) || File.Exists(Path + ".bak")))
            throw new InvalidOperationException($"{contents} must be loaded before they are used or saved.");
    }

    /// <summary>Throws unless a save may replace the file now.</summary>
    public void EnsureWritable()
    {
        EnsureLoaded();
        if (State == StoreLoadState.Unreadable)
        {
            throw new StoreReadOnlyException(
                $"{contents} were not saved: {Path} could not be read when resesh started, and saving "
                + "would overwrite it. Restart resesh once the file can be read.");
        }
    }

    /// <summary>Writes atomically and, only once that succeeds, counts the state as loaded.</summary>
    public void Write(string json)
    {
        EnsureWritable();
        AtomicFile.Write(Path, json, preserveBackup: State == StoreLoadState.RecoveredFromBackup);
        State = StoreLoadState.Loaded;
    }
}
