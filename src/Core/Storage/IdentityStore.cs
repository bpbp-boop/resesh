using System.Text.Json;
using System.Text.Json.Serialization;
using Resesh.Core.Models;

namespace Resesh.Core.Storage;

/// <summary>Atomic JSON store for identities (username plus proof). Secrets stay in
/// Credential Manager under each identity's id.</summary>
public sealed class IdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = CoreJson.With(new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { CamelCaseEnumConverter.Instance },
    });

    private readonly StoreFile _file;
    private readonly object _gate = new();
    private List<Identity> _identities = [];

    public IdentityStore(string path)
    {
        _file = new StoreFile(path, "Identities");
    }

    public static string DefaultPath => AppDataPaths.Roaming("identities.json");

    /// <summary>Set by <see cref="Load"/> when the file was recovered or unreadable.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Whether saves are allowed; see <see cref="StoreLoadState"/>.</summary>
    public StoreLoadState LoadState
    {
        get { lock (_gate) return _file.State; }
    }

    /// <summary>Raised after any change is saved.</summary>
    public event Action? Changed;

    public IReadOnlyList<Identity> Identities
    {
        get { lock (_gate) return _identities.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                _identities = _file.Load(Read)?.Identities ?? [];
                LoadWarning = _file.Warning;
            }
            catch
            {
                // Bad data is handled by the load itself; a throw here is a bug, and
                // a half-filled or empty store must not replace the file: refuse saves.
                _file.RefuseSaves();
                throw;
            }
        }
    }

    public Identity? Find(Guid id)
    {
        lock (_gate) return _identities.FirstOrDefault(i => i.Id == id);
    }

    /// <summary>Adds an identity, making its name unique. Returns the stored record.</summary>
    public Identity Add(Identity identity)
    {
        Identity stored;
        lock (_gate)
        {
            if (_identities.Any(i => i.Id == identity.Id))
                throw new InvalidOperationException($"Identity {identity.Id} already exists.");
            var candidate = new List<Identity>(_identities);
            stored = Normalize(identity) with { Name = UniqueName(candidate, NameOf(identity)) };
            candidate.Add(stored);
            Save(candidate);
        }
        Changed?.Invoke();
        return stored;
    }

    public Identity Update(Identity identity)
    {
        Identity stored;
        lock (_gate)
        {
            var index = IndexOf(identity.Id);
            var candidate = new List<Identity>(_identities);
            stored = Normalize(identity) with { Name = UniqueName(candidate, NameOf(identity), identity.Id) };
            candidate[index] = stored;
            Save(candidate);
        }
        Changed?.Invoke();
        return stored;
    }

    /// <summary>Removes the identity. The caller deletes its secret and detaches its users.</summary>
    public bool Remove(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _identities.Any(i => i.Id == id);
            if (removed)
            {
                var candidate = new List<Identity>(_identities);
                candidate.RemoveAll(i => i.Id == id);
                Save(candidate);
            }
        }
        if (removed)
            Changed?.Invoke();
        return removed;
    }

    /// <summary>Merges backup identities: same id is kept as-is, otherwise added under a
    /// unique name. Returns imported id → local id.</summary>
    public IReadOnlyDictionary<Guid, Guid> MergeImport(IEnumerable<Identity> imported, Func<Guid, Guid?>? mapKeyId = null)
    {
        var map = new Dictionary<Guid, Guid>();
        var changed = false;
        lock (_gate)
        {
            var candidate = _identities;
            foreach (var identity in imported)
            {
                if (candidate.Any(i => i.Id == identity.Id))
                {
                    map[identity.Id] = identity.Id;
                    continue;
                }
                var keyId = identity.KeyId is { } key && mapKeyId is not null ? mapKeyId(key) ?? key : identity.KeyId;
                var added = Normalize(identity with { KeyId = keyId }) with { Name = UniqueName(candidate, NameOf(identity)) };
                if (!changed)
                    candidate = new List<Identity>(_identities);
                candidate.Add(added);
                map[identity.Id] = added.Id;
                changed = true;
            }
            if (changed)
                Save(candidate);
        }
        if (changed)
            Changed?.Invoke();
        return map;
    }

    private static string NameOf(Identity identity) =>
        string.IsNullOrWhiteSpace(identity.Name) ? identity.Username.Trim() : identity.Name.Trim();

    private static Identity Normalize(Identity identity) => identity with
    {
        Username = identity.Username.Trim(),
        KeyId = identity.Proof == IdentityProof.Key ? identity.KeyId : null,
    };

    private int IndexOf(Guid id)
    {
        var index = _identities.FindIndex(i => i.Id == id);
        return index >= 0 ? index : throw new KeyNotFoundException($"Identity {id} was not found.");
    }

    private static string UniqueName(List<Identity> identities, string requested, Guid? except = null)
    {
        var baseName = requested.Length > 0 ? requested : "Identity";
        var candidate = baseName;
        var suffix = 2;
        while (identities.Any(i => i.Id != except && i.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{baseName} ({suffix++})";
        return candidate;
    }

    private void Save(List<Identity> candidate)
    {
        var json = JsonSerializer.Serialize(new StoreData { Identities = candidate }, JsonOptions.Info<StoreData>());
        _file.Write(json);
        _identities = candidate;
    }

    private static StoreData? Read(string path)
    {
        var data = JsonSerializer.Deserialize(File.ReadAllText(path), JsonOptions.Info<StoreData>());
        // Inside the loader, so a bad file falls back to .bak instead of failing later.
        Validate(data?.Identities);
        return data;
    }

    /// <summary>The one check for stored identities, used by the loader and by backup reading:
    /// no null entries or text, which naming, login resolution and the editor dereference.</summary>
    internal static void Validate(IEnumerable<Identity?>? identities)
    {
        if (identities?.Any(identity => identity is null || identity.Name is null
                || identity.Username is null || identity.Notes is null) == true)
            throw new InvalidDataException("The identity list contains an invalid identity.");
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
        public List<Identity>? Identities { get; set; }
    }
}
