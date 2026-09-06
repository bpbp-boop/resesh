using System.Text.Json;
using Resesh.Core.Storage;

namespace Resesh.Core.Ssh;

public enum HostKeyVerdict
{
    Unknown,
    Match,
    Mismatch,
}

public sealed record KnownHostEntry(string KeyType, string Sha256);

/// <summary>
/// Accepted host keys, stored as JSON keyed by "host:port". SHA256 fingerprints are
/// base64 of the SHA-256 over the raw host key blob (OpenSSH-style).
/// </summary>
public sealed class KnownHostsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, KnownHostEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public string? LoadWarning { get; private set; }
    public string? LoadError { get; private set; }
    private bool _preserveBackup;

    public KnownHostsStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Resesh", "known_hosts.json");

    public void Load()
    {
        lock (_gate)
        {
            LoadWarning = null;
            LoadError = null;
            _preserveBackup = false;
            try
            {
                _entries = Read(_path);
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                try
                {
                    _entries = Read(_path + ".bak");
                    _preserveBackup = true;
                    LoadWarning = "Accepted host keys were recovered from the backup file.";
                }
                catch (Exception backupError) when (backupError is JsonException or IOException or UnauthorizedAccessException)
                {
                    if (error is (FileNotFoundException or DirectoryNotFoundException)
                        && backupError is (FileNotFoundException or DirectoryNotFoundException))
                        _entries = new(StringComparer.OrdinalIgnoreCase);
                    else
                        LoadError = $"Accepted host keys could not be loaded. SSH is blocked. Restore {_path} from a trusted backup and restart resesh. {error.Message}";
                }
            }
        }
    }

    public HostKeyVerdict Check(string host, int port, string keyType, string sha256)
    {
        lock (_gate)
        {
            EnsureAvailable();
            if (!_entries.TryGetValue(Key(host, port), out var entry))
                return HostKeyVerdict.Unknown;
            return entry.Sha256 == sha256 && entry.KeyType.Equals(keyType, StringComparison.OrdinalIgnoreCase)
                ? HostKeyVerdict.Match
                : HostKeyVerdict.Mismatch;
        }
    }

    public KnownHostEntry? Lookup(string host, int port)
    {
        lock (_gate)
        {
            EnsureAvailable();
            return _entries.GetValueOrDefault(Key(host, port));
        }
    }

    /// <summary>A stable copy for backup export.</summary>
    public IReadOnlyDictionary<string, KnownHostEntry> Entries
    {
        get
        {
            lock (_gate)
                EnsureAvailable();
                return new Dictionary<string, KnownHostEntry>(_entries, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Adds host keys that are not already known. An import never replaces a different
    /// key for an existing endpoint because that could hide a host-key mismatch.
    /// Returns the number of added entries.
    /// </summary>
    public int Merge(IReadOnlyDictionary<string, KnownHostEntry> entries)
    {
        lock (_gate)
        {
            EnsureAvailable();
            var updated = new Dictionary<string, KnownHostEntry>(_entries, StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var (key, value) in entries)
            {
                if (updated.TryAdd(key, value))
                    added++;
            }

            if (added > 0)
                Save(updated);
            return added;
        }
    }

    public void Accept(string host, int port, string keyType, string sha256)
    {
        lock (_gate)
        {
            EnsureAvailable();
            var updated = new Dictionary<string, KnownHostEntry>(_entries, StringComparer.OrdinalIgnoreCase);
            updated[Key(host, port)] = new KnownHostEntry(keyType, sha256);
            Save(updated);
        }
    }

    private void Save(Dictionary<string, KnownHostEntry> entries)
    {
        AtomicFile.Write(_path, JsonSerializer.Serialize(entries, JsonOptions), _preserveBackup);
        _entries = entries;
        _preserveBackup = false;
    }

    private void EnsureAvailable()
    {
        if (LoadError is not null) throw new IOException(LoadError);
    }

    private static Dictionary<string, KnownHostEntry> Read(string path)
    {
        var entries = JsonSerializer.Deserialize<Dictionary<string, KnownHostEntry>>(
            File.ReadAllText(path), JsonOptions) ?? throw new JsonException("The host-key file is empty.");
        if (entries.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null
            || string.IsNullOrWhiteSpace(pair.Value.KeyType) || string.IsNullOrWhiteSpace(pair.Value.Sha256)))
            throw new JsonException("The host-key file contains an invalid entry.");
        return new(entries, StringComparer.OrdinalIgnoreCase);
    }

    private static string Key(string host, int port) => $"{host}:{port}";
}
