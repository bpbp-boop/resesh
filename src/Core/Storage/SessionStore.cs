using System.Text.Json;
using System.Text.Json.Serialization;
using Resesh.Core.Models;

namespace Resesh.Core.Storage;

/// <summary>
/// JSON-file-backed store for sessions and explicitly-created (possibly empty) folders.
/// Writes are atomic: serialize to a temp file, then swap it in, rotating the previous
/// file to ".bak". Load falls back to the .bak if the main file is missing or corrupt.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private bool _preserveBackup;

    private List<Session> _sessions = [];
    private List<string> _folders = [];
    private List<string> _localFolders = [];
    private List<FolderSettings> _folderSettings = [];

    private List<string> FolderListOf(SessionKind kind) =>
        kind == SessionKind.Local ? _localFolders : _folders;

    public SessionStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath =>
        AppDataPaths.Roaming("sessions.json");

    /// <summary>Set by <see cref="Load"/> when the file was recovered or unreadable.</summary>
    public string? LoadWarning { get; private set; }

    public IReadOnlyList<Session> Sessions
    {
        get { lock (_gate) return _sessions.ToList(); }
    }

    /// <summary>SSH folder paths (explicit plus mentioned by a session); see <see cref="FoldersOf"/>.</summary>
    public IReadOnlyList<string> Folders => FoldersOf(SessionKind.Ssh);

    /// <summary>
    /// Explicit folder paths of one kind, plus any folder mentioned by a session of that
    /// kind. SSH and local folders are separate namespaces: local paths are relative to
    /// the virtual Local root.
    /// </summary>
    public IReadOnlyList<string> FoldersOf(SessionKind kind)
    {
        lock (_gate)
        {
            var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in FolderListOf(kind)
                .Concat(_sessions.Where(s => s.FolderScope == kind).Select(s => s.FolderPath)))
            {
                foreach (var ancestor in FolderPaths.SelfAndAncestors(path))
                    all.Add(ancestor);
            }
            all.Remove("");
            return all.ToList();
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            var load = AtomicFile.Load(_path, Read, "Sessions");
            _preserveBackup = load.PreserveBackup;
            LoadWarning = load.Warning;
            var data = load.Data ?? new StoreData();
            _sessions = data.Sessions ?? [];
            _folders = data.Folders ?? [];
            _localFolders = data.LocalFolders ?? [];
            _folderSettings = (data.FolderSettings ?? [])
                .Select(f => f with { Path = FolderPaths.Normalize(f.Path) })
                .Where(f => f.IdentityId is not null)
                .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    // ---- Folder settings (remote tree only; local profiles have no logins) ----

    /// <summary>Settings saved on remote-tree folders. The empty path is the tree root.</summary>
    public IReadOnlyList<FolderSettings> FolderSettings
    {
        get { lock (_gate) return _folderSettings.ToList(); }
    }

    /// <summary>The identity attached directly to this folder (not inherited), or null.</summary>
    public Guid? FolderIdentity(string folderPath)
    {
        folderPath = FolderPaths.Normalize(folderPath);
        lock (_gate)
            return _folderSettings.FirstOrDefault(f => f.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase))?.IdentityId;
    }

    /// <summary>Attaches an identity to a remote-tree folder, or detaches it with null.</summary>
    public void SetFolderIdentity(string folderPath, Guid? identityId)
    {
        folderPath = FolderPaths.Normalize(folderPath);
        lock (_gate)
        {
            var index = _folderSettings.FindIndex(f => f.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && _folderSettings[index].IdentityId == identityId)
                return;
            if (index >= 0)
                _folderSettings.RemoveAt(index);
            if (identityId is not null)
                _folderSettings.Add(new FolderSettings { Path = folderPath, IdentityId = identityId });
            Save();
        }
    }

    /// <summary>Adds imported folder settings for folders that have none; local settings win.</summary>
    public void MergeFolderSettings(IEnumerable<FolderSettings> settings)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var setting in settings)
            {
                var path = FolderPaths.Normalize(setting.Path);
                if (setting.IdentityId is null
                    || _folderSettings.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _folderSettings.Add(setting with { Path = path });
                changed = true;
            }
            if (changed)
                Save();
        }
    }

    /// <summary>Detaches an identity everywhere: folders drop it, sessions that chose it
    /// inherit again. Returns the number of folders and sessions changed.</summary>
    public int DetachIdentity(Guid identityId)
    {
        lock (_gate)
        {
            var changed = _folderSettings.RemoveAll(f => f.IdentityId == identityId);
            for (var i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].IdentityId != identityId)
                    continue;
                _sessions[i] = _sessions[i] with { IdentityId = null, InheritLogin = true };
                changed++;
            }
            if (changed > 0)
                Save();
            return changed;
        }
    }

    public Session? Find(Guid id)
    {
        lock (_gate) return _sessions.FirstOrDefault(s => s.Id == id);
    }

    public void Add(Session session)
    {
        lock (_gate)
        {
            if (_sessions.Any(s => s.Id == session.Id))
                throw new InvalidOperationException($"Session {session.Id} already exists.");
            _sessions.Add(session);
            Save();
        }
    }

    public void Update(Session session)
    {
        lock (_gate)
        {
            var index = _sessions.FindIndex(s => s.Id == session.Id);
            if (index < 0)
                throw new InvalidOperationException($"Session {session.Id} not found.");
            _sessions[index] = session;
            Save();
        }
    }

    /// <summary>Removes the session. Caller is responsible for deleting its credential.</summary>
    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var removed = _sessions.RemoveAll(s => s.Id == id) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    public void MoveToFolder(Guid id, string folderPath) =>
        MoveToFolder([id], folderPath);

    public void MoveToFolder(IEnumerable<Guid> ids, string folderPath)
    {
        lock (_gate)
        {
            var remaining = ids.ToHashSet();
            if (remaining.Count == 0)
                return;

            folderPath = FolderPaths.Normalize(folderPath);
            var changed = false;
            for (var index = 0; index < _sessions.Count && remaining.Count > 0; index++)
            {
                var session = _sessions[index];
                if (!remaining.Remove(session.Id)
                    || session.FolderPath.Equals(folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _sessions[index] = session with { FolderPath = folderPath };
                changed = true;
            }

            if (changed)
                Save();
        }
    }

    public void CreateFolder(string folderPath, SessionKind kind = SessionKind.Ssh)
    {
        lock (_gate)
        {
            folderPath = FolderPaths.Normalize(folderPath);
            if (folderPath.Length == 0)
                return;
            var folders = FolderListOf(kind);
            if (!folders.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(folderPath);
                Save();
            }
        }
    }

    /// <summary>
    /// Adds or replaces a prepared set of imported sessions and unions its explicit folders
    /// into this store. The caller resolves conflicts and final ids before this method runs.
    /// The complete merge is saved in one atomic write.
    /// </summary>
    public void ApplyImport(
        IEnumerable<Session> sessions,
        IEnumerable<string> folders,
        IEnumerable<string> localFolders)
    {
        lock (_gate)
        {
            foreach (var session in sessions)
            {
                var index = _sessions.FindIndex(s => s.Id == session.Id);
                if (index >= 0)
                    _sessions[index] = session;
                else
                    _sessions.Add(session);
            }

            UnionFolders(_folders, folders);
            UnionFolders(_localFolders, localFolders);
            Save();
        }
    }

    private static void UnionFolders(List<string> destination, IEnumerable<string> source)
    {
        foreach (var folder in source.Select(FolderPaths.Normalize).Where(f => f.Length > 0))
        {
            if (!destination.Contains(folder, StringComparer.OrdinalIgnoreCase))
                destination.Add(folder);
        }
    }

    /// <summary>Renames/moves a folder; every same-kind session and subfolder under it follows.</summary>
    public void RenameFolder(string oldPath, string newPath, SessionKind kind = SessionKind.Ssh)
    {
        lock (_gate)
        {
            oldPath = FolderPaths.Normalize(oldPath);
            newPath = FolderPaths.Normalize(newPath);
            if (oldPath.Length == 0 || newPath.Length == 0 || oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
                return;

            for (var i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].FolderScope != kind)
                    continue;
                var reparented = FolderPaths.Reparent(_sessions[i].FolderPath, oldPath, newPath);
                if (reparented is not null)
                    _sessions[i] = _sessions[i] with { FolderPath = reparented };
            }

            var folders = FolderListOf(kind);
            var renamed = folders
                .Select(f => FolderPaths.Reparent(f, oldPath, newPath) ?? f)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!renamed.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                renamed.Add(newPath);
            if (kind == SessionKind.Local)
                _localFolders = renamed;
            else
            {
                _folders = renamed;
                ReparentFolderSettings(oldPath, newPath);
            }
            Save();
        }
    }

    /// <summary>Moves folder settings with their folder. When the destination already has
    /// its own setting, the destination keeps it: sessions already there keep their login.</summary>
    private void ReparentFolderSettings(string oldPath, string newPath)
    {
        var moved = _folderSettings
            .Select(f => (Setting: f, Path: FolderPaths.Reparent(f.Path, oldPath, newPath)))
            .Where(m => m.Path is not null)
            .ToList();
        if (moved.Count == 0)
            return;
        _folderSettings.RemoveAll(f => moved.Any(m => ReferenceEquals(m.Setting, f)));
        foreach (var (setting, path) in moved)
        {
            if (!_folderSettings.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                _folderSettings.Add(setting with { Path = path! });
        }
    }

    /// <summary>
    /// Deletes a folder of one kind and everything under it. Returns the removed sessions
    /// so the caller can delete their credentials.
    /// </summary>
    public IReadOnlyList<Session> DeleteFolder(string folderPath, SessionKind kind = SessionKind.Ssh)
    {
        lock (_gate)
        {
            folderPath = FolderPaths.Normalize(folderPath);
            if (folderPath.Length == 0)
                return [];

            var removed = _sessions
                .Where(s => s.FolderScope == kind && FolderPaths.IsSelfOrDescendant(s.FolderPath, folderPath))
                .ToList();
            _sessions.RemoveAll(s => s.FolderScope == kind && FolderPaths.IsSelfOrDescendant(s.FolderPath, folderPath));
            FolderListOf(kind).RemoveAll(f => FolderPaths.IsSelfOrDescendant(f, folderPath));
            if (kind != SessionKind.Local)
                _folderSettings.RemoveAll(f => f.Path.Length > 0 && FolderPaths.IsSelfOrDescendant(f.Path, folderPath));
            Save();
            return removed;
        }
    }

    private void Save()
    {
        var data = new StoreData
        {
            Sessions = _sessions,
            Folders = _folders,
            LocalFolders = _localFolders.Count > 0 ? _localFolders : null,
            FolderSettings = _folderSettings.Count > 0 ? _folderSettings : null,
        };
        var json = JsonSerializer.Serialize(data, JsonOptions);

        AtomicFile.Write(_path, json, _preserveBackup);
        _preserveBackup = false;
    }

    private static StoreData? Read(string path) =>
        JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path), JsonOptions);

    private sealed class StoreData
    {
        public List<Session>? Sessions { get; set; }
        public List<string>? Folders { get; set; }

        /// <summary>Folders under the virtual Local root (separate namespace from Folders).</summary>
        public List<string>? LocalFolders { get; set; }

        /// <summary>Remote-tree folder settings, keyed by path. Older builds ignore it.</summary>
        public List<FolderSettings>? FolderSettings { get; set; }
    }
}
