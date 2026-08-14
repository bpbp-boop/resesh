using System.Text.Json;

namespace Sessions.Core.Ssh;

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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sessions", "known_hosts.json");

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    _entries = JsonSerializer.Deserialize<Dictionary<string, KnownHostEntry>>(
                        File.ReadAllText(_path), JsonOptions) ?? new(StringComparer.OrdinalIgnoreCase);
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

    public void Accept(string host, int port, string keyType, string sha256)
    {
        lock (_gate)
        {
            _entries[Key(host, port)] = new KnownHostEntry(keyType, sha256);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
        }
    }

    private static string Key(string host, int port) => $"{host}:{port}";
}
