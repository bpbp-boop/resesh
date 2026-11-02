using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Resesh.Core.Storage;

namespace Resesh.Core.Ssh;

public enum HostKeyVerdict
{
    Unknown,
    Match,
    Mismatch,
}

public sealed record KnownHostEntry(string KeyType, string Sha256, string? PublicKeyBase64 = null)
{
    /// <summary>OpenSSH uses the blob's key type, not the negotiated signature algorithm.</summary>
    public string ToKnownHostsLine(string host, int port)
    {
        if (string.IsNullOrEmpty(PublicKeyBase64))
            throw new IOException("No public host key is stored. Connect the terminal first, then open Explorer again.");
        if (string.IsNullOrWhiteSpace(host) || host.Any(c => char.IsWhiteSpace(c) || c is ',' or '*' or '?' or '!' or '|'))
            throw new IOException("The SSH host cannot be represented in a known_hosts file.");
        byte[] blob;
        try { blob = Convert.FromBase64String(PublicKeyBase64); }
        catch (FormatException e) { throw new IOException("The stored public host key is invalid. Reconnect the terminal.", e); }
        if (Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=') != Sha256
            || blob.Length < 4)
            throw new IOException("The stored public host key does not match its trusted fingerprint. Reconnect the terminal.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (length == 0 || length > blob.Length - 4)
            throw new IOException("The stored public host key has an invalid key type.");
        var typeBytes = blob.AsSpan(4, (int)length);
        foreach (var c in typeBytes)
            if (c <= 32 || c >= 127)
                throw new IOException("The stored public host key has an invalid key type.");
        var type = Encoding.ASCII.GetString(typeBytes);
        var endpoint = port == 22 ? host : $"[{host}]:{port}";
        return $"{endpoint} {type} {Convert.ToBase64String(blob)}";
    }
}

/// <summary>
/// Accepted host keys, stored as JSON keyed by "host:port". SHA256 fingerprints are
/// base64 of the SHA-256 over the raw host key blob (OpenSSH-style).
/// </summary>
public sealed class KnownHostsStore
{
    private static readonly JsonSerializerOptions JsonOptions = CoreJson.With(new() { WriteIndented = true });

    private readonly string _path;
    private readonly StoreFile _file;
    private readonly object _gate = new();
    private Dictionary<string, KnownHostEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public string? LoadWarning { get; private set; }
    public string? LoadError { get; private set; }

    /// <summary>Whether saves are allowed; see <see cref="StoreLoadState"/>.</summary>
    public StoreLoadState LoadState
    {
        get { lock (_gate) return _file.State; }
    }

    public KnownHostsStore(string path)
    {
        _path = path;
        _file = new StoreFile(path, "Accepted host keys");
    }

    public static string DefaultPath =>
        AppDataPaths.Roaming("known_hosts.json");

    /// <summary>
    /// Stricter than the other stores: an invalid file with no good backup blocks SSH instead
    /// of starting empty, because an empty list would turn a changed host key into an
    /// innocent-looking "unknown host" prompt. A primary that cannot be read (locked) or
    /// copied aside, with a good backup, is used read-only so the newer primary survives.
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            LoadWarning = null;
            LoadError = null;
            _entries = new(StringComparer.OrdinalIgnoreCase);
            var primary = AtomicFile.TryRead(_path, Read, out var primaryOutcome, out var primaryError);
            if (primary is not null)
            {
                _entries = primary;
                _file.SetLoaded(StoreLoadState.Loaded, null);
                return;
            }

            var backup = AtomicFile.TryRead(_path + ".bak", Read, out var backupOutcome, out var backupError);
            if (backup is null)
            {
                if (primaryOutcome == FileReadOutcome.Missing && backupOutcome == FileReadOutcome.Missing)
                {
                    _file.SetLoaded(StoreLoadState.Loaded, null);
                    return;
                }
                LoadError = $"Accepted host keys could not be loaded. SSH is blocked. Restore {_path} from a trusted backup and restart resesh. {primaryError ?? backupError}";
                _file.SetLoaded(StoreLoadState.Unreadable, null);
                return;
            }

            _entries = backup;
            if (primaryOutcome == FileReadOutcome.Missing)
            {
                LoadWarning = "Accepted host keys were recovered from the backup file.";
                _file.SetLoaded(StoreLoadState.RecoveredFromBackup, LoadWarning);
            }
            else if (AtomicFile.TryQuarantine(_path) is { } kept)
            {
                LoadWarning = $"Accepted host keys were recovered from the backup file. The unreadable file was kept as {kept}.";
                _file.SetLoaded(StoreLoadState.RecoveredFromBackup, LoadWarning);
            }
            else
            {
                LoadWarning = $"Accepted host keys could not be read from {_path} ({primaryError}). The backup copy is in use, "
                    + "but newly accepted host keys will not be saved. Close any program that may be holding the file and restart resesh.";
                _file.SetLoaded(StoreLoadState.Unreadable, LoadWarning);
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
            // Same key, whichever signature algorithm it was presented with (RSA has three).
            return entry.Sha256 == sha256 && SshHostKeyTypes.SameKey(entry.KeyType, keyType)
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
            {
                EnsureAvailable();
                return new Dictionary<string, KnownHostEntry>(_entries, StringComparer.OrdinalIgnoreCase);
            }
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

    public void Accept(string host, int port, string keyType, string sha256, byte[]? hostKey = null)
    {
        lock (_gate)
        {
            EnsureAvailable();
            var updated = new Dictionary<string, KnownHostEntry>(_entries, StringComparer.OrdinalIgnoreCase);
            if (hostKey is not null && Fingerprint(hostKey) != sha256)
                throw new ArgumentException("The public host key does not match its fingerprint.", nameof(hostKey));
            updated[Key(host, port)] = new KnownHostEntry(keyType, sha256,
                hostKey is null ? null : Convert.ToBase64String(hostKey));
            Save(updated);
        }
    }

    /// <summary>Upgrades fingerprint-only trust only when the received blob is that same key.</summary>
    public bool BackfillHostKey(string host, int port, string keyType, byte[] hostKey)
    {
        lock (_gate)
        {
            EnsureAvailable();
            var key = Key(host, port);
            if (!_entries.TryGetValue(key, out var entry) || !string.IsNullOrEmpty(entry.PublicKeyBase64)
                || !SshHostKeyTypes.SameKey(entry.KeyType, keyType)
                || entry.Sha256 != Fingerprint(hostKey))
                return false;
            var updated = new Dictionary<string, KnownHostEntry>(_entries, StringComparer.OrdinalIgnoreCase)
            {
                [key] = entry with { PublicKeyBase64 = Convert.ToBase64String(hostKey) },
            };
            Save(updated);
            return true;
        }
    }

    private static string Fingerprint(byte[] hostKey) =>
        Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    private void Save(Dictionary<string, KnownHostEntry> entries)
    {
        _file.Write(JsonSerializer.Serialize(entries, JsonOptions.Info<Dictionary<string, KnownHostEntry>>()));
        _entries = entries;
    }

    private void EnsureAvailable()
    {
        if (LoadError is not null) throw new IOException(LoadError);
        _file.EnsureLoaded();
    }

    private static Dictionary<string, KnownHostEntry> Read(string path)
    {
        var entries = JsonSerializer.Deserialize(
            File.ReadAllText(path), JsonOptions.Info<Dictionary<string, KnownHostEntry>>()) ?? throw new JsonException("The host-key file is empty.");
        if (entries.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null
            || string.IsNullOrWhiteSpace(pair.Value.KeyType) || string.IsNullOrWhiteSpace(pair.Value.Sha256)))
            throw new JsonException("The host-key file contains an invalid entry.");
        return new(entries, StringComparer.OrdinalIgnoreCase);
    }

    private static string Key(string host, int port) => $"{host}:{port}";
}
