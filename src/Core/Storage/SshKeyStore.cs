using System.Text.Json;
using System.Text.Json.Serialization;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.Core.Storage;

/// <summary>Atomic JSON store for external SSH private-key references and public metadata.</summary>
public sealed class SshKeyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly string _bakPath;
    private readonly object _gate = new();
    private List<SshKeyReference> _keys = [];

    public SshKeyStore(string path)
    {
        _path = path;
        _bakPath = path + ".bak";
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Resesh", "ssh-keys.json");

    public IReadOnlyList<SshKeyReference> Keys
    {
        get { lock (_gate) return _keys.OrderBy(key => key.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public void Load()
    {
        lock (_gate)
            _keys = TryRead(_path)?.Keys ?? TryRead(_bakPath)?.Keys ?? [];
    }

    public SshKeyReference? Find(Guid id)
    {
        lock (_gate) return _keys.FirstOrDefault(key => key.Id == id);
    }

    public SshKeyReference RegisterExternal(string path, string? name = null, bool allowMissing = false)
    {
        var normalized = NormalizePath(path);
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

            var key = new SshKeyReference
            {
                Name = UniqueName(string.IsNullOrWhiteSpace(name)
                    ? Path.GetFileName(normalized)
                    : name.Trim()),
                Path = normalized,
                Algorithm = inspection?.Algorithm,
                KeyLength = inspection?.KeyLength,
                Fingerprint = inspection?.Fingerprint,
                IsEncrypted = inspection?.IsEncrypted,
                PublicKey = inspection?.PublicKey,
            };
            _keys.Add(key);
            Save();
            return key;
        }
    }

    public void Rename(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A key name is required.", nameof(name));
        lock (_gate)
        {
            var index = IndexOf(id);
            _keys[index] = _keys[index] with { Name = UniqueName(name.Trim(), id) };
            Save();
        }
    }

    public SshKeyReference Relocate(Guid id, string path)
    {
        var normalized = NormalizePath(path);
        var inspection = SshKeyInspector.Inspect(normalized);
        lock (_gate)
        {
            var index = IndexOf(id);
            var current = _keys[index];
            EnsureFingerprintUnchanged(current, inspection);
            var updated = ApplyInspection(current with { Path = normalized }, inspection);
            _keys[index] = updated;
            Save();
            return updated;
        }
    }

    /// <summary>Checks that the file still contains the registered public key and refreshes metadata.</summary>
    public SshKeyReference Validate(Guid id, string? passphrase, bool acceptChanged = false)
    {
        SshKeyReference current;
        lock (_gate)
            current = _keys[IndexOf(id)];
        var inspection = SshKeyInspector.Inspect(current.Path, passphrase);

        lock (_gate)
        {
            current = _keys[IndexOf(id)];
            if (!acceptChanged)
                EnsureFingerprintUnchanged(current, inspection);
            var updated = ApplyInspection(current, inspection);
            if (updated != current)
            {
                _keys[IndexOf(id)] = updated;
                Save();
            }
            return updated;
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var removed = _keys.RemoveAll(key => key.Id == id) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    /// <summary>Merges backup metadata without opening or copying any key file.</summary>
    public IReadOnlyDictionary<Guid, Guid> MergeImport(IEnumerable<SshKeyReference> importedKeys)
    {
        lock (_gate)
        {
            var map = new Dictionary<Guid, Guid>();
            var changed = false;
            foreach (var imported in importedKeys)
            {
                var match = _keys.FirstOrDefault(key => key.Id == imported.Id)
                    ?? _keys.FirstOrDefault(key => key.Fingerprint is { Length: > 0 }
                        && key.Fingerprint.Equals(imported.Fingerprint, StringComparison.Ordinal))
                    ?? _keys.FirstOrDefault(key => TryPathsEqual(key.Path, imported.Path));
                if (match is not null)
                {
                    map[imported.Id] = match.Id;
                    continue;
                }

                var target = imported with
                {
                    Id = _keys.Any(key => key.Id == imported.Id) ? Guid.NewGuid() : imported.Id,
                    Name = UniqueName(imported.Name),
                };
                _keys.Add(target);
                map[imported.Id] = target.Id;
                changed = true;
            }
            if (changed)
                Save();
            return map;
        }
    }

    /// <summary>Converts legacy per-session key paths into shared key references. Files are never copied.</summary>
    public int MigrateLegacySessions(SessionStore sessions, ICredentialService credentials)
    {
        var legacy = sessions.Sessions
            .Where(session => session.Kind == SessionKind.Ssh
                && session.AuthMethod == AuthMethod.PrivateKey
                && session.PrivateKeyId is null
                && !string.IsNullOrWhiteSpace(session.PrivateKeyPath))
            .ToList();
        var migrated = 0;
        var normalized = legacy.Select(session =>
            TryNormalizePath(session.PrivateKeyPath!, out var path) ? (Session: session, Path: path) : default)
            .Where(item => item.Session is not null)
            .ToList();
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
            foreach (var item in group)
            {
                var session = item.Session;
                sessions.Update(session with
                {
                    PrivateKeyId = key.Id,
                    PrivateKeyPath = null,
                    PassphraseRequired = false,
                });
                migrated++;
            }
        }
        return migrated;
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

    private string UniqueName(string requested, Guid? except = null)
    {
        var baseName = requested;
        var candidate = baseName;
        var suffix = 2;
        while (_keys.Any(key => key.Id != except && key.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
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

    private void Save()
    {
        var json = JsonSerializer.Serialize(new StoreData { Keys = _keys }, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(_path))
            File.Replace(temp, _path, _bakPath);
        else
            File.Move(temp, _path);
    }

    private static StoreData? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private sealed class StoreData
    {
        public List<SshKeyReference>? Keys { get; set; }
    }
}
