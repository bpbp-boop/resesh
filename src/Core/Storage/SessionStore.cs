using System.Text.Json;
using System.Text.Json.Serialization;
using Resesh.Core.Models;

namespace Resesh.Core.Storage;

/// <summary>
/// JSON-file-backed store for sessions and explicitly-created (possibly empty) folders.
/// Writes are atomic: serialize candidate state to a temp file, then swap it in, rotating
/// the previous file to ".bak". In-memory changes are published only after that succeeds.
/// Load falls back to the .bak if the main file is missing or corrupt.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = CoreJson.With(new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { CamelCaseEnumConverter.Instance },
    });

    private readonly StoreFile _file;
    private readonly object _gate = new();

    private List<Session> _sessions = [];
    private List<string> _folders = [];
    private List<string> _localFolders = [];
    private List<FolderSettings> _folderSettings = [];

    private List<string> FolderListOf(SessionKind kind) =>
        kind == SessionKind.Local ? _localFolders : _folders;

    public SessionStore(string path)
    {
        _file = new StoreFile(path, "Sessions");
    }

    public static string DefaultPath =>
        AppDataPaths.Roaming("sessions.json");

    /// <summary>Set by <see cref="Load"/> when the file was recovered or unreadable.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Whether saves are allowed; see <see cref="StoreLoadState"/>.</summary>
    public StoreLoadState LoadState
    {
        get { lock (_gate) return _file.State; }
    }

    /// <summary>
    /// Raised after every successful save, whatever wrote it, so copies of sessions held
    /// elsewhere (tabs, the tree, other windows) can re-read them. Raised on the writing
    /// thread while the store is still locked: handlers see exactly the saved state and may
    /// read the store, but must not wait on another thread that uses it.
    /// </summary>
    public event Action? Changed;

    public IReadOnlyList<Session> Sessions
    {
        get { lock (_gate) return _sessions.Select(SessionSnapshot.Copy).ToList(); }
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
            try
            {
                var data = _file.Load(Read) ?? new StoreData();
                LoadWarning = _file.Warning;
                _sessions = data.Sessions ?? [];
                _folders = data.Folders ?? [];
                _localFolders = data.LocalFolders ?? [];
                _folderSettings = (data.FolderSettings ?? [])
                    .Select(f => f with { Path = FolderPaths.Normalize(f.Path) })
                    .Where(f => f.IdentityId is not null)
                    .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // Older builds attached identities to folders that existed only through their
                // sessions; once those moved away the identity stayed, unseen, and logged in any
                // folder later made at that path. Keep such folders so the identity shows.
                _folders = WithSettingFolders(_folders, _folderSettings);
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

    /// <summary>The folders of one kind that were created (or are kept by a folder setting),
    /// without those that exist only because a session is in them. Backups carry these, so a
    /// restore does not make folders that come and go with their sessions permanent.</summary>
    public IReadOnlyList<string> ExplicitFoldersOf(SessionKind kind)
    {
        lock (_gate)
            return FolderListOf(kind).ToList();
    }

    /// <summary><paramref name="folders"/> plus the folder of every non-root setting. A folder
    /// with settings is always explicit: it must not vanish when its last session moves out
    /// while its identity lives on, to be inherited by whatever is made there next.</summary>
    private static List<string> WithSettingFolders(List<string> folders, IEnumerable<FolderSettings> settings) =>
        UnionFolders(folders, settings.Select(setting => setting.Path));

    // ---- Folder settings (remote tree only; local profiles have no logins) ----

    /// <summary>Settings saved on remote-tree folders. The empty path is the tree root.</summary>
    public IReadOnlyList<FolderSettings> FolderSettings
    {
        get { lock (_gate) return _folderSettings.Select(setting => setting with { }).ToList(); }
    }

    /// <summary>The identity attached directly to this folder (not inherited), or null.</summary>
    public Guid? FolderIdentity(string folderPath)
    {
        folderPath = FolderPaths.Normalize(folderPath);
        lock (_gate)
            return _folderSettings.FirstOrDefault(f => f.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase))?.IdentityId;
    }

    /// <summary>Attaches an identity to a remote-tree folder, or detaches it with null.
    /// Attaching makes the folder explicit, so it outlives the sessions in it.</summary>
    public void SetFolderIdentity(string folderPath, Guid? identityId)
    {
        folderPath = FolderPaths.Normalize(folderPath);
        lock (_gate)
        {
            var index = _folderSettings.FindIndex(f => f.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && _folderSettings[index].IdentityId == identityId)
                return;
            var folderSettings = _folderSettings.ToList();
            if (index >= 0)
                folderSettings.RemoveAt(index);
            if (identityId is not null)
                folderSettings.Add(new FolderSettings { Path = folderPath, IdentityId = identityId });
            Save(folders: WithSettingFolders(_folders, folderSettings), folderSettings: folderSettings);
        }
    }

    /// <summary>Adds imported folder settings for folders that have none; local settings win.</summary>
    public void MergeFolderSettings(IEnumerable<FolderSettings> settings)
    {
        lock (_gate)
        {
            var folderSettings = MergedFolderSettings(_folderSettings, settings);
            if (!ReferenceEquals(folderSettings, _folderSettings))
                Save(folders: WithSettingFolders(_folders, folderSettings), folderSettings: folderSettings);
        }
    }

    /// <summary><paramref name="destination"/> plus each incoming setting for a folder that has
    /// none; the same list when nothing is added.</summary>
    private static List<FolderSettings> MergedFolderSettings(List<FolderSettings> destination, IEnumerable<FolderSettings> incoming)
    {
        List<FolderSettings>? candidate = null;
        foreach (var setting in incoming)
        {
            var path = FolderPaths.Normalize(setting.Path);
            if (setting.IdentityId is null
                || (candidate ?? destination).Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                continue;
            candidate ??= destination.ToList();
            candidate.Add(setting with { Path = path });
        }
        return candidate ?? destination;
    }

    /// <summary>Detaches an identity everywhere: folders drop it, sessions that chose it
    /// inherit again. Returns the number of folders and sessions changed.</summary>
    public int DetachIdentity(Guid identityId)
    {
        lock (_gate)
        {
            List<FolderSettings>? folderSettings = null;
            var changed = 0;
            if (_folderSettings.Any(f => f.IdentityId == identityId))
            {
                folderSettings = _folderSettings.ToList();
                changed = folderSettings.RemoveAll(f => f.IdentityId == identityId);
            }
            List<Session>? sessions = null;
            for (var i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].IdentityId != identityId)
                    continue;
                sessions ??= _sessions.ToList();
                sessions[i] = _sessions[i] with { IdentityId = null, InheritLogin = true };
                changed++;
            }
            if (changed > 0)
                Save(sessions: sessions, folderSettings: folderSettings);
            return changed;
        }
    }

    public Session? Find(Guid id)
    {
        lock (_gate)
            return _sessions.FirstOrDefault(s => s.Id == id) is { } session ? SessionSnapshot.Copy(session) : null;
    }

    public void Add(Session session)
    {
        lock (_gate)
        {
            if (_sessions.Any(s => s.Id == session.Id))
                throw new InvalidOperationException($"Session {session.Id} already exists.");
            var sessions = _sessions.ToList();
            sessions.Add(session);
            Save(sessions: sessions);
        }
    }

    public void Update(Session session)
    {
        lock (_gate)
        {
            var index = _sessions.FindIndex(s => s.Id == session.Id);
            if (index < 0)
                throw new InvalidOperationException($"Session {session.Id} not found.");
            var sessions = _sessions.ToList();
            sessions[index] = session;
            Save(sessions: sessions);
        }
    }

    /// <summary>Removes the session. Caller is responsible for deleting its credential.</summary>
    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            if (!_sessions.Any(s => s.Id == id))
                return false;
            var sessions = _sessions.ToList();
            sessions.RemoveAll(s => s.Id == id);
            Save(sessions: sessions);
            return true;
        }
    }

    // ---- Batches: one atomic write however many sessions change ----

    public void AddMany(IEnumerable<Session> added)
    {
        lock (_gate)
        {
            var sessions = _sessions.ToList();
            foreach (var session in added)
            {
                if (sessions.Any(s => s.Id == session.Id))
                    throw new InvalidOperationException($"Session {session.Id} already exists.");
                sessions.Add(session);
            }
            if (sessions.Count != _sessions.Count)
                Save(sessions: sessions);
        }
    }

    /// <summary>Replaces saved sessions. Nothing is written when any of them is missing.</summary>
    public void UpdateMany(IEnumerable<Session> updated)
    {
        lock (_gate)
        {
            List<Session>? sessions = null;
            foreach (var session in updated)
            {
                sessions ??= _sessions.ToList();
                var index = sessions.FindIndex(s => s.Id == session.Id);
                if (index < 0)
                    throw new InvalidOperationException($"Session {session.Id} not found.");
                sessions[index] = session;
            }
            if (sessions is not null)
                Save(sessions: sessions);
        }
    }

    /// <summary>Removes the sessions and returns the ones that existed, so the caller can
    /// delete their credentials.</summary>
    public IReadOnlyList<Session> RemoveMany(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            var remove = ids.ToHashSet();
            var removed = _sessions.Where(s => remove.Contains(s.Id)).ToList();
            if (removed.Count > 0)
                Save(sessions: _sessions.Where(s => !remove.Contains(s.Id)).ToList());
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
            List<Session>? sessions = null;
            for (var index = 0; index < _sessions.Count && remaining.Count > 0; index++)
            {
                var session = _sessions[index];
                if (!remaining.Remove(session.Id)
                    || session.FolderPath.Equals(folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sessions ??= _sessions.ToList();
                sessions[index] = session with { FolderPath = folderPath };
            }

            if (sessions is not null)
                Save(sessions: sessions);
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
                var candidate = folders.ToList();
                candidate.Add(folderPath);
                if (kind == SessionKind.Local)
                    Save(localFolders: candidate);
                else
                    Save(folders: candidate);
            }
        }
    }

    /// <summary>
    /// Adds or replaces a prepared set of imported sessions and unions its explicit folders
    /// and folder settings (for folders that have none here) into this store. The caller
    /// resolves conflicts and final ids before this method runs. The complete merge is saved
    /// in one atomic write, so sessions never land without the folder logins they inherit.
    /// </summary>
    public void ApplyImport(
        IEnumerable<Session> sessions,
        IEnumerable<string> folders,
        IEnumerable<string> localFolders,
        IEnumerable<FolderSettings>? folderSettings = null)
    {
        lock (_gate)
        {
            List<Session>? candidateSessions = null;
            foreach (var session in sessions)
            {
                candidateSessions ??= _sessions.ToList();
                var index = candidateSessions.FindIndex(s => s.Id == session.Id);
                if (index >= 0)
                    candidateSessions[index] = session;
                else
                    candidateSessions.Add(session);
            }

            var candidateFolderSettings = MergedFolderSettings(_folderSettings, folderSettings ?? []);
            var candidateFolders = WithSettingFolders(UnionFolders(_folders, folders), candidateFolderSettings);
            var candidateLocalFolders = UnionFolders(_localFolders, localFolders);
            Save(candidateSessions, candidateFolders, candidateLocalFolders, candidateFolderSettings);
        }
    }

    private static List<string> UnionFolders(List<string> destination, IEnumerable<string> source)
    {
        List<string>? candidate = null;
        foreach (var folder in source.Select(FolderPaths.Normalize).Where(f => f.Length > 0))
        {
            if ((candidate ?? destination).Contains(folder, StringComparer.OrdinalIgnoreCase))
                continue;
            candidate ??= destination.ToList();
            candidate.Add(folder);
        }
        return candidate ?? destination;
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

            List<Session>? sessions = null;
            for (var i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].FolderScope != kind)
                    continue;
                var reparented = FolderPaths.Reparent(_sessions[i].FolderPath, oldPath, newPath);
                if (reparented is not null)
                {
                    sessions ??= _sessions.ToList();
                    sessions[i] = _sessions[i] with { FolderPath = reparented };
                }
            }

            var folders = FolderListOf(kind);
            var renamed = folders
                .Select(f => FolderPaths.Reparent(f, oldPath, newPath) ?? f)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!renamed.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                renamed.Add(newPath);
            if (kind == SessionKind.Local)
                Save(sessions: sessions, localFolders: renamed);
            else
            {
                var folderSettings = ReparentFolderSettings(_folderSettings, oldPath, newPath);
                Save(sessions: sessions, folders: renamed, folderSettings: folderSettings);
            }
        }
    }

    /// <summary>Moves folder settings with their folder. When the destination already has
    /// its own setting, the destination keeps it: sessions already there keep their login.</summary>
    private static List<FolderSettings> ReparentFolderSettings(List<FolderSettings> folderSettings, string oldPath, string newPath)
    {
        var moved = folderSettings
            .Select(f => (Setting: f, Path: FolderPaths.Reparent(f.Path, oldPath, newPath)))
            .Where(m => m.Path is not null)
            .ToList();
        if (moved.Count == 0)
            return folderSettings;
        var candidate = folderSettings.ToList();
        candidate.RemoveAll(f => moved.Any(m => ReferenceEquals(m.Setting, f)));
        foreach (var (setting, path) in moved)
        {
            if (!candidate.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                candidate.Add(setting with { Path = path! });
        }
        return candidate;
    }

    /// <summary>The sessions <see cref="DeleteFolder"/> would remove with this folder: every
    /// session in its tree (telnet and serial sessions share the SSH tree) under it.</summary>
    public IReadOnlyList<Session> SessionsInFolder(string folderPath, SessionKind kind = SessionKind.Ssh)
    {
        folderPath = FolderPaths.Normalize(folderPath);
        if (folderPath.Length == 0)
            return [];
        lock (_gate)
            return _sessions.Where(s => IsInFolder(s, folderPath, kind)).Select(SessionSnapshot.Copy).ToList();
    }

    private static bool IsInFolder(Session session, string folderPath, SessionKind kind) =>
        session.FolderScope == kind && FolderPaths.IsSelfOrDescendant(session.FolderPath, folderPath);

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

            var removed = _sessions.Where(s => IsInFolder(s, folderPath, kind)).ToList();
            var sessions = _sessions.Where(s => !IsInFolder(s, folderPath, kind)).ToList();
            var folders = FolderListOf(kind)
                .Where(f => !FolderPaths.IsSelfOrDescendant(f, folderPath))
                .ToList();
            if (kind == SessionKind.Local)
                Save(sessions: sessions, localFolders: folders);
            else
            {
                var folderSettings = _folderSettings
                    .Where(f => f.Path.Length == 0 || !FolderPaths.IsSelfOrDescendant(f.Path, folderPath))
                    .ToList();
                Save(sessions: sessions, folders: folders, folderSettings: folderSettings);
            }
            return removed;
        }
    }

    /// <summary>
    /// Deletes folders (each with everything under it) and sessions in one write, so a save
    /// that fails deletes none of them. Returns the removed sessions so the caller can delete
    /// their credentials.
    /// </summary>
    public IReadOnlyList<Session> DeleteMany(IEnumerable<(string Path, SessionKind Kind)> folders, IEnumerable<Guid> sessionIds)
    {
        lock (_gate)
        {
            var targets = folders
                .Select(folder => (Path: FolderPaths.Normalize(folder.Path), folder.Kind))
                .Where(folder => folder.Path.Length > 0)
                .ToList();
            var ids = sessionIds.ToHashSet();
            bool Removes(Session session) =>
                ids.Contains(session.Id) || targets.Any(folder => IsInFolder(session, folder.Path, folder.Kind));
            bool FolderGoes(string path, SessionKind kind) =>
                targets.Any(folder => folder.Kind == kind && FolderPaths.IsSelfOrDescendant(path, folder.Path));

            var removed = _sessions.Where(Removes).ToList();
            if (removed.Count == 0 && targets.Count == 0)
                return [];
            Save(
                sessions: _sessions.Where(session => !Removes(session)).ToList(),
                folders: _folders.Where(path => !FolderGoes(path, SessionKind.Ssh)).ToList(),
                localFolders: _localFolders.Where(path => !FolderGoes(path, SessionKind.Local)).ToList(),
                folderSettings: _folderSettings
                    .Where(setting => setting.Path.Length == 0 || !FolderGoes(setting.Path, SessionKind.Ssh))
                    .ToList());
            return removed;
        }
    }

    private void Save(
        List<Session>? sessions = null,
        List<string>? folders = null,
        List<string>? localFolders = null,
        List<FolderSettings>? folderSettings = null)
    {
        sessions ??= _sessions;
        folders ??= _folders;
        localFolders ??= _localFolders;
        folderSettings ??= _folderSettings;
        Validate(sessions, folders, localFolders, folderSettings);
        sessions = sessions.Select(SessionSnapshot.Copy).ToList();
        folderSettings = folderSettings.Select(setting => setting with { }).ToList();
        var data = new StoreData
        {
            Sessions = sessions,
            Folders = folders,
            LocalFolders = localFolders.Count > 0 ? localFolders : null,
            FolderSettings = folderSettings.Count > 0 ? folderSettings : null,
        };
        var json = JsonSerializer.Serialize(data, JsonOptions.Info<StoreData>());

        _file.Write(json);
        _sessions = sessions;
        _folders = folders;
        _localFolders = localFolders;
        _folderSettings = folderSettings;
        Changed?.Invoke();
    }

    private static StoreData? Read(string path)
    {
        var data = JsonSerializer.Deserialize(File.ReadAllText(path), JsonOptions.Info<StoreData>());
        Validate(data?.Sessions, data?.Folders, data?.LocalFolders, data?.FolderSettings);
        return data;
    }

    /// <summary>The one check for stored sessions and folders, used by the loader (so a bad
    /// file falls back to .bak) and by backup reading: no null entries or required text.</summary>
    internal static void Validate(IEnumerable<Session?>? sessions, IEnumerable<string?>? folders,
        IEnumerable<string?>? localFolders, IEnumerable<FolderSettings?>? folderSettings)
    {
        if (sessions?.Any(session => session is null || session.Name is null
                || session.Host is null || session.Username is null || session.FolderPath is null
                || session.Notes is null || session.TerminalType is null
                || session.Local is { } local && (local.Executable is null || local.StartingDirectory is null
                    || local.Arguments is null || local.Arguments.Any(argument => argument is null)
                    || local.Environment?.Any(pair => pair.Value is null) == true)
                || session.Proxy is { } proxy && (proxy.Host is null || proxy.Username is null)
                || session.Tunnels?.Any(tunnel => tunnel is null || tunnel.BindAddress is null || tunnel.DestinationHost is null) == true) == true
            || folders?.Any(folder => folder is null) == true
            || localFolders?.Any(folder => folder is null) == true
            || folderSettings?.Any(setting => setting is null || setting.Path is null) == true)
        {
            throw new InvalidDataException("The sessions data contains an invalid session, folder or folder setting.");
        }
        if (sessions is not null)
        {
            var ids = new HashSet<Guid>();
            if (sessions.Any(session => !ids.Add(session!.Id)))
                throw new InvalidDataException("The sessions data contains duplicate session IDs.");
        }
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
        public List<Session>? Sessions { get; set; }
        public List<string>? Folders { get; set; }

        /// <summary>Folders under the virtual Local root (separate namespace from Folders).</summary>
        public List<string>? LocalFolders { get; set; }

        /// <summary>Remote-tree folder settings, keyed by path. Older builds ignore it.</summary>
        public List<FolderSettings>? FolderSettings { get; set; }
    }
}
