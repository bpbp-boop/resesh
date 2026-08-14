using System.Text.Json;
using System.Text.Json.Serialization;
using Sessions.Core.Models;

namespace Sessions.Core.Storage;

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

    public SessionStore(string path)
    {
        _path = path;
        _bakPath = path + ".bak";
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sessions", "sessions.json");

    public IReadOnlyList<Session> Sessions
    {
        get { lock (_gate) return _sessions.ToList(); }
    }

    /// <summary>Explicit folder paths, plus any folder mentioned by a session.</summary>
    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_gate)
            {
                var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in _folders.Concat(_sessions.Select(s => s.FolderPath)))
                {
                    foreach (var ancestor in FolderPaths.SelfAndAncestors(path))
                        all.Add(ancestor);
                }
                all.Remove("");
                return all.ToList();
            }
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            var data = TryRead(_path) ?? TryRead(_bakPath) ?? new StoreData();
            _sessions = data.Sessions ?? [];
            _folders = data.Folders ?? [];
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

    public void CreateFolder(string folderPath)
    {
        lock (_gate)
        {
            folderPath = FolderPaths.Normalize(folderPath);
            if (folderPath.Length == 0)
                return;
            if (!_folders.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
            {
                _folders.Add(folderPath);
                Save();
            }
        }
    }

    /// <summary>Renames/moves a folder; every session and subfolder under it follows.</summary>
    public void RenameFolder(string oldPath, string newPath)
    {
        lock (_gate)
        {
            oldPath = FolderPaths.Normalize(oldPath);
            newPath = FolderPaths.Normalize(newPath);
            if (oldPath.Length == 0 || newPath.Length == 0 || oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
                return;

            for (var i = 0; i < _sessions.Count; i++)
            {
                var reparented = FolderPaths.Reparent(_sessions[i].FolderPath, oldPath, newPath);
                if (reparented is not null)
                    _sessions[i] = _sessions[i] with { FolderPath = reparented };
            }

            _folders = _folders
                .Select(f => FolderPaths.Reparent(f, oldPath, newPath) ?? f)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!_folders.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                _folders.Add(newPath);
            Save();
        }
    }

    /// <summary>
    /// Deletes a folder and everything under it. Returns the removed sessions so the
    /// caller can delete their credentials.
    /// </summary>
    public IReadOnlyList<Session> DeleteFolder(string folderPath)
    {
        lock (_gate)
        {
            folderPath = FolderPaths.Normalize(folderPath);
            if (folderPath.Length == 0)
                return [];

            var removed = _sessions.Where(s => FolderPaths.IsSelfOrDescendant(s.FolderPath, folderPath)).ToList();
            _sessions.RemoveAll(s => FolderPaths.IsSelfOrDescendant(s.FolderPath, folderPath));
            _folders.RemoveAll(f => FolderPaths.IsSelfOrDescendant(f, folderPath));
            Save();
            return removed;
        }
    }

    private void Save()
    {
        var data = new StoreData { Sessions = _sessions, Folders = _folders };
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
    }
}
