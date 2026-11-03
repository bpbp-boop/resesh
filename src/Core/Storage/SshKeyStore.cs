using System.Text.Json;
using System.Text.Json.Serialization;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.Core.Storage;

/// <summary>Atomic JSON store for external SSH private-key references and public metadata.</summary>
public sealed class SshKeyStore
{
    private static readonly JsonSerializerOptions JsonOptions = CoreJson.With(new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { CamelCaseEnumConverter.Instance },
    });

    private readonly StoreFile _file;
    private readonly object _gate = new();
    private List<SshKeyReference> _keys = [];

    public SshKeyStore(string path)
    {
        _file = new StoreFile(path, "SSH keys");
    }

    public static string DefaultPath => AppDataPaths.Roaming("ssh-keys.json");

    /// <summary>Set by <see cref="Load"/> when the file was recovered or unreadable.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Whether saves are allowed; see <see cref="StoreLoadState"/>.</summary>
    public StoreLoadState LoadState
    {
        get { lock (_gate) return _file.State; }
    }

    /// <summary>Raised after any change is saved, outside the store's lock.</summary>
    public event Action? Changed;

    public IReadOnlyList<SshKeyReference> Keys
    {
        get { lock (_gate) return _keys.OrderBy(key => key.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public void Load()
    {
        lock (_gate)
        {
            _keys = _file.Load(Read)?.Keys ?? [];
            LoadWarning = _file.Warning;
        }
    }

    public SshKeyReference? Find(Guid id)
    {
        lock (_gate) return _keys.FirstOrDefault(key => key.Id == id);
    }

    public SshKeyReference RegisterExternal(string path, string? name = null, bool allowMissing = false)
    {
        var normalized = NormalizePath(path);
        SshKeyReference key;
        lock (_gate)
        {
            var existing = _keys.FirstOrDefault(key => PathsEqual(key.Path, normalized));
            if (existing is not null)
                return existing;

            SshKeyInspection? inspection = null;
            if (File.Exists(normalized))
            {
                try
                {
                    inspection = SshKeyInspector.Inspect(normalized);
                }
                catch (Exception ex) when (allowMissing
                    && (ex is IOException or UnauthorizedAccessException or ArgumentException))
                {
                    // Migration must preserve an existing reference even when the current
                    // file cannot be inspected. Connection will report the exact problem.
                }
            }
            else if (!allowMissing)
                throw new FileNotFoundException("The private-key file was not found.", normalized);

            var candidate = new List<SshKeyReference>(_keys);
            key = new SshKeyReference
            {
                Name = UniqueName(candidate, string.IsNullOrWhiteSpace(name)
                    ? Path.GetFileName(normalized)
                    : name.Trim()),
                Path = normalized,
                Algorithm = inspection?.Algorithm,
                KeyLength = inspection?.KeyLength,
                Fingerprint = inspection?.Fingerprint,
                IsEncrypted = inspection?.IsEncrypted,
                PublicKey = inspection?.PublicKey,
            };
            candidate.Add(key);
            Save(candidate);
        }
        Changed?.Invoke();
        return key;
    }

    public void Rename(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A key name is required.", nameof(name));
        lock (_gate)
        {
            var index = IndexOf(id);
            var candidate = new List<SshKeyReference>(_keys);
            candidate[index] = candidate[index] with { Name = UniqueName(candidate, name.Trim(), id) };
            Save(candidate);
        }
        Changed?.Invoke();
    }

    public SshKeyReference Relocate(Guid id, string path)
    {
        var normalized = NormalizePath(path);
        var inspection = SshKeyInspector.Inspect(normalized);
        SshKeyReference updated;
        lock (_gate)
        {
            var index = IndexOf(id);
            var current = _keys[index];
            EnsureFingerprintUnchanged(current, inspection);
            updated = ApplyInspection(current with { Path = normalized }, inspection);
            var candidate = new List<SshKeyReference>(_keys);
            candidate[index] = updated;
            Save(candidate);
        }
        Changed?.Invoke();
        return updated;
    }

    /// <summary>Checks that the file still contains the registered public key and refreshes metadata.</summary>
    public SshKeyReference Validate(Guid id, string? passphrase, bool acceptChanged = false)
    {
        SshKeyReference current;
        lock (_gate)
            current = _keys[IndexOf(id)];
        var inspection = SshKeyInspector.Inspect(current.Path, passphrase);

        SshKeyReference updated;
        lock (_gate)
        {
            current = _keys[IndexOf(id)];
            if (!acceptChanged)
                EnsureFingerprintUnchanged(current, inspection);
            updated = ApplyInspection(current, inspection);
            if (updated == current)
                return updated;
            var candidate = new List<SshKeyReference>(_keys);
            candidate[IndexOf(id)] = updated;
            Save(candidate);
        }
        Changed?.Invoke();
        return updated;
    }

    public bool Remove(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _keys.Any(key => key.Id == id);
            if (removed)
            {
                var candidate = new List<SshKeyReference>(_keys);
                candidate.RemoveAll(key => key.Id == id);
                Save(candidate);
            }
        }
        if (removed)
            Changed?.Invoke();
        return removed;
    }

    /// <summary>Merges backup metadata without opening or copying any key file.</summary>
    public IReadOnlyDictionary<Guid, Guid> MergeImport(IEnumerable<SshKeyReference> importedKeys)
    {
        var map = new Dictionary<Guid, Guid>();
        var changed = false;
        lock (_gate)
        {
            var candidate = _keys;
            foreach (var imported in importedKeys)
            {
                var match = candidate.FirstOrDefault(key => key.Id == imported.Id)
                    ?? candidate.FirstOrDefault(key => key.Fingerprint is { Length: > 0 }
                        && key.Fingerprint.Equals(imported.Fingerprint, StringComparison.Ordinal))
                    ?? candidate.FirstOrDefault(key => TryPathsEqual(key.Path, imported.Path));
                if (match is not null)
                {
                    map[imported.Id] = match.Id;
                    continue;
                }

                var target = imported with
                {
                    Id = candidate.Any(key => key.Id == imported.Id) ? Guid.NewGuid() : imported.Id,
                    Name = UniqueName(candidate, imported.Name),
                };
                if (!changed)
                    candidate = new List<SshKeyReference>(_keys);
                candidate.Add(target);
                map[imported.Id] = target.Id;
                changed = true;
            }
            if (changed)
                Save(candidate);
        }
        if (changed)
            Changed?.Invoke();
        return map;
    }

    /// <summary>Converts legacy per-session key paths into shared key references. Files are never
    /// copied. A passphrase saved on the session moves to the key: once the key holds one and
    /// the sessions point at it, the sessions' own copies are deleted, so it lives in one place.
    /// Runs at startup and after anything that can bring in old sessions (a backup import).</summary>
    public int MigrateLegacySessions(SessionStore sessions, ICredentialService credentials)
    {
        var legacy = sessions.Sessions
            .Where(session => session.Kind == SessionKind.Ssh
                && session.AuthMethod == AuthMethod.PrivateKey
                && session.PrivateKeyId is null
                && !string.IsNullOrWhiteSpace(session.PrivateKeyPath))
            .ToList();
        var normalized = legacy.Select(session =>
            TryNormalizePath(session.PrivateKeyPath!, out var path) ? (Session: session, Path: path) : default)
            .Where(item => item.Session is not null)
            .ToList();
        var updated = new List<Session>();
        var movedSecrets = new List<Guid>();
        foreach (var group in normalized.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            var key = RegisterExternal(group.Key, allowMissing: true);
            if (credentials.ReadKey(key.Id) is null)
            {
                var legacySecret = group.Select(item => credentials.Read(item.Session.Id))
                    .FirstOrDefault(secret => !string.IsNullOrEmpty(secret));
                if (legacySecret is not null)
                    credentials.WriteKey(key.Id, legacySecret);
            }
            var keyHasSecret = !string.IsNullOrEmpty(credentials.ReadKey(key.Id));
            foreach (var item in group)
            {
                updated.Add(item.Session with
                {
                    PrivateKeyId = key.Id,
                    PrivateKeyPath = null,
                    PassphraseRequired = false,
                });
                if (keyHasSecret)
                    movedSecrets.Add(item.Session.Id);
            }
        }
        // One write; the sessions' copies go only once nothing still reads them.
        sessions.UpdateMany(updated);
        foreach (var sessionId in movedSecrets)
            credentials.Delete(sessionId);
        return updated.Count;
    }

    private static SshKeyReference ApplyInspection(SshKeyReference key, SshKeyInspection inspection) => key with
    {
        Algorithm = inspection.Algorithm ?? key.Algorithm,
        KeyLength = inspection.KeyLength ?? key.KeyLength,
        Fingerprint = inspection.Fingerprint ?? key.Fingerprint,
        IsEncrypted = inspection.IsEncrypted ?? key.IsEncrypted,
        PublicKey = inspection.PublicKey ?? key.PublicKey,
    };

    private static void EnsureFingerprintUnchanged(SshKeyReference key, SshKeyInspection inspection)
    {
        if (key.Fingerprint is { Length: > 0 } previous
            && inspection.Fingerprint is { Length: > 0 } current
            && !previous.Equals(current, StringComparison.Ordinal))
        {
            throw new SshKeyChangedException(key.Name, previous, current);
        }
    }

    private int IndexOf(Guid id)
    {
        var index = _keys.FindIndex(key => key.Id == id);
        return index >= 0 ? index : throw new KeyNotFoundException($"SSH key {id} was not found.");
    }

    private static string UniqueName(List<SshKeyReference> keys, string requested, Guid? except = null)
    {
        var baseName = requested;
        var candidate = baseName;
        var suffix = 2;
        while (keys.Any(key => key.Id != except && key.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{baseName} ({suffix++})";
        return candidate;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(
        Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));

    private static bool PathsEqual(string left, string right) =>
        NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static bool TryPathsEqual(string left, string right)
    {
        try { return PathsEqual(left, right); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool TryNormalizePath(string path, out string normalized)
    {
        try
        {
            normalized = NormalizePath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = "";
            return false;
        }
    }

    private void Save(List<SshKeyReference> candidate)
    {
        var json = JsonSerializer.Serialize(new StoreData { Keys = candidate }, JsonOptions.Info<StoreData>());
        _file.Write(json);
        _keys = candidate;
    }

    private static StoreData? Read(string path)
    {
        var data = JsonSerializer.Deserialize(File.ReadAllText(path), JsonOptions.Info<StoreData>());
        // Inside the loader, so a bad file falls back to .bak instead of failing later.
        Validate(data?.Keys);
        return data;
    }

    /// <summary>The one check for stored key references, used by the loader and by backup
    /// reading: no null entries, names or paths, which every caller dereferences.</summary>
    internal static void Validate(IEnumerable<SshKeyReference?>? keys)
    {
        if (keys?.Any(key => key is null || key.Name is null || key.Path is null) == true)
            throw new InvalidDataException("The SSH key list contains an invalid key.");
    }

    /// <summary>Throws unless a save may replace the file now; lets an import refuse before it
    /// changes anything.</summary>
    internal void EnsureWritable()
    {
        lock (_gate)
            _file.EnsureWritable();
    }

    internal sealed class StoreData
    {
        public List<SshKeyReference>? Keys { get; set; }
    }
}
