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
    private readonly string _bakPath;
    private readonly object _gate = new();

    private List<Session> _sessions = [];
    private List<string> _folders = [];
    private List<string> _localFolders = [];

    private List<string> FolderListOf(SessionKind kind) =>
        kind == SessionKind.Local ? _localFolders : _folders;

    public SessionStore(string path)
    {
        _path = path;
        _bakPath = path + ".bak";
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Resesh", "sessions.json");

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
                .Concat(_sessions.Where(s => s.Kind == kind).Select(s => s.FolderPath)))
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
            var data = TryRead(_path) ?? TryRead(_bakPath) ?? new StoreData();
            _sessions = data.Sessions ?? [];
            _folders = data.Folders ?? [];
            _localFolders = data.LocalFolders ?? [];
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

    public void MoveToFolder(Guid id, string folderPath)
    {
        lock (_gate)
        {
            var index = _sessions.FindIndex(s => s.Id == id);
            if (index < 0)
                return;
            _sessions[index] = _sessions[index] with { FolderPath = FolderPaths.Normalize(folderPath) };
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
                if (_sessions[i].Kind != kind)
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
                _folders = renamed;
            Save();
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
                .Where(s => s.Kind == kind && FolderPaths.IsSelfOrDescendant(s.FolderPath, folderPath))
                .ToList();
            _sessions.RemoveAll(s => s.Kind == kind && FolderPaths.IsSelfOrDescendant(s.FolderPath, folderPath));
            FolderListOf(kind).RemoveAll(f => FolderPaths.IsSelfOrDescendant(f, folderPath));
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
        };
        var json = JsonSerializer.Serialize(data, JsonOptions);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmpPath = _path + ".tmp";
        File.WriteAllText(tmpPath, json);

        if (File.Exists(_path))
            File.Replace(tmpPath, _path, _bakPath);
        else
            File.Move(tmpPath, _path);
    }

    private static StoreData? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    private sealed class StoreData
    {
        public List<Session>? Sessions { get; set; }
        public List<string>? Folders { get; set; }

        /// <summary>Folders under the virtual Local root (separate namespace from Folders).</summary>
        public List<string>? LocalFolders { get; set; }
    }
}
