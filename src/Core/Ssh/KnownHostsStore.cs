using System.Text.Json;

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
            try
            {
                if (File.Exists(_path))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, KnownHostEntry>>(
                        File.ReadAllText(_path), JsonOptions);
                    _entries = loaded is null
                        ? new(StringComparer.OrdinalIgnoreCase)
                        : new(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                _entries = new(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public HostKeyVerdict Check(string host, int port, string keyType, string sha256)
    {
        lock (_gate)
        {
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
            return _entries.GetValueOrDefault(Key(host, port));
    }

    /// <summary>A stable copy for backup export.</summary>
    public IReadOnlyDictionary<string, KnownHostEntry> Entries
    {
        get
        {
            lock (_gate)
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
            var added = 0;
            foreach (var (key, value) in entries)
            {
                if (_entries.TryAdd(key, value))
                    added++;
            }

            if (added > 0)
                Save();
            return added;
        }
    }

    public void Accept(string host, int port, string keyType, string sha256)
    {
        lock (_gate)
        {
            _entries[Key(host, port)] = new KnownHostEntry(keyType, sha256);
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmpPath = _path + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(_entries, JsonOptions));
        if (File.Exists(_path))
            File.Replace(tmpPath, _path, null);
        else
            File.Move(tmpPath, _path);
    }

    private static string Key(string host, int port) => $"{host}:{port}";
}
