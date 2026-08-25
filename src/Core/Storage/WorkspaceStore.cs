using System.Text.Json;

namespace Resesh.Core.Storage;

/// <summary>A saved tab reference. Session details remain in <see cref="SessionStore"/>.</summary>
public sealed record WorkspaceTabReference
{
    public Guid SessionId { get; init; }
    public bool Pinned { get; init; }
}

/// <summary>One ordered tab group and its selected tab.</summary>
public sealed record WorkspaceGroup
{
    public IReadOnlyList<WorkspaceTabReference> Tabs { get; init; } = [];
    public int ActiveTabIndex { get; init; }
}

/// <summary>The group arrangement shared by saved workspaces and the clean-exit layout.</summary>
public sealed record WorkspaceLayout
{
    public IReadOnlyList<WorkspaceGroup> Groups { get; init; } = [];
}

/// <summary>A named, stable saved layout.</summary>
public sealed record Workspace
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<WorkspaceGroup> Groups { get; init; } = [];
}

/// <summary>
/// JSON-backed workspace store. Writes use the same temp/swap/.bak scheme as the session
/// store, and loading falls back to the backup when the primary file is invalid.
/// </summary>
public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly string _bakPath;
    private readonly object _gate = new();
    private List<Workspace> _workspaces = [];
    private WorkspaceLayout? _lastLayout;

    public WorkspaceStore(string path)
    {
        _path = path;
        _bakPath = path + ".bak";
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Resesh",
        "workspaces.json");

    public IReadOnlyList<Workspace> Workspaces
    {
        get { lock (_gate) return _workspaces.ToList(); }
    }

    public WorkspaceLayout? LastLayout
    {
        get { lock (_gate) return _lastLayout; }
    }

    public void Load()
    {
        lock (_gate)
        {
            var data = TryRead(_path) ?? TryRead(_bakPath) ?? new WorkspaceStoreData { Workspaces = [] };
            _workspaces = data.Workspaces!;
            _lastLayout = data.LastLayout;
        }
    }

    public Workspace SaveAs(string name, WorkspaceLayout layout)
    {
        lock (_gate)
        {
            name = ValidateName(name);
            if (_workspaces.Any(workspace => workspace.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A workspace named \"{name}\" already exists.");

            var normalized = NormalizeLayout(layout);
            var workspace = new Workspace
            {
                Id = Guid.NewGuid(),
                Name = name,
                Groups = normalized.Groups,
            };
            _workspaces.Add(workspace);
            Save();
            return workspace;
        }
    }

    public void Rename(Guid id, string name)
    {
        lock (_gate)
        {
            name = ValidateName(name);
            if (_workspaces.Any(workspace => workspace.Id != id
                && workspace.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A workspace named \"{name}\" already exists.");
            }

            var index = FindIndex(id);
            _workspaces[index] = _workspaces[index] with { Name = name };
            Save();
        }
    }

    public void Update(Guid id, WorkspaceLayout layout)
    {
        lock (_gate)
        {
            var index = FindIndex(id);
            _workspaces[index] = _workspaces[index] with { Groups = NormalizeLayout(layout).Groups };
            Save();
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var removed = _workspaces.RemoveAll(workspace => workspace.Id == id) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    public void SaveLastLayout(WorkspaceLayout layout)
    {
        lock (_gate)
        {
            _lastLayout = NormalizeLayout(layout);
            Save();
        }
    }

    private int FindIndex(Guid id)
    {
        var index = _workspaces.FindIndex(workspace => workspace.Id == id);
        return index >= 0
            ? index
            : throw new InvalidOperationException($"Workspace {id} was not found.");
    }

    private static string ValidateName(string name)
    {
        name = name?.Trim() ?? "";
        return name.Length > 0
            ? name
            : throw new ArgumentException("Workspace name cannot be empty.", nameof(name));
    }

    private void Save() => WriteAtomic(_path, new WorkspaceStoreData
    {
        Workspaces = _workspaces,
        LastLayout = _lastLayout,
    });

    private static WorkspaceStoreData? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? ParsePayload(File.ReadAllBytes(path)) : null;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return null;
        }
    }

    internal static WorkspaceStoreData ParsePayload(byte[] payload)
    {
        try
        {
            var data = JsonSerializer.Deserialize<WorkspaceStoreData>(payload, JsonOptions)
                ?? throw new InvalidDataException("The workspace payload is empty.");
            return NormalizeData(data);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("The workspace payload is invalid.", exception);
        }
    }

    internal static WorkspaceStoreData RemapReferences(
        WorkspaceStoreData source,
        IReadOnlyDictionary<Guid, Guid> idMap,
        IReadOnlySet<Guid> validSessionIds)
    {
        WorkspaceLayout RemapLayout(WorkspaceLayout layout) => new()
        {
            Groups = layout.Groups.Select(group =>
            {
                var surviving = group.Tabs
                    .Select((tab, originalIndex) => (
                        Tab: tab with { SessionId = idMap.GetValueOrDefault(tab.SessionId, tab.SessionId) },
                        OriginalIndex: originalIndex))
                    .Where(item => validSessionIds.Contains(item.Tab.SessionId))
                    .ToList();
                var activeIndex = surviving.FindIndex(item => item.OriginalIndex == group.ActiveTabIndex);
                if (activeIndex < 0 && surviving.Count > 0)
                {
                    activeIndex = Math.Clamp(
                        surviving.Count(item => item.OriginalIndex < group.ActiveTabIndex),
                        0,
                        surviving.Count - 1);
                }
                return new WorkspaceGroup
                {
                    Tabs = surviving.Select(item => item.Tab).ToList(),
                    ActiveTabIndex = Math.Max(activeIndex, 0),
                };
            }).ToList(),
        };

        return new WorkspaceStoreData
        {
            Workspaces = source.Workspaces!.Select(workspace => workspace with
            {
                Groups = RemapLayout(new WorkspaceLayout { Groups = workspace.Groups }).Groups,
            }).ToList(),
            LastLayout = source.LastLayout is null ? null : RemapLayout(source.LastLayout),
        };
    }

    internal static void WriteImported(string path, WorkspaceStoreData data) =>
        WriteAtomic(path, NormalizeData(data));

    private static WorkspaceStoreData NormalizeData(WorkspaceStoreData data)
    {
        if (data.Workspaces is null)
            throw new InvalidDataException("The workspace payload has no workspaces collection.");

        var ids = new HashSet<Guid>();
        var workspaces = new List<Workspace>(data.Workspaces.Count);
        foreach (var workspace in data.Workspaces)
        {
            if (workspace is null)
                throw new InvalidDataException("The workspace payload contains a null workspace.");
            if (workspace.Id == Guid.Empty || !ids.Add(workspace.Id))
                throw new InvalidDataException("The workspace payload contains an empty or duplicate workspace id.");
            if (string.IsNullOrWhiteSpace(workspace.Name))
                throw new InvalidDataException("The workspace payload contains an unnamed workspace.");
            var layout = NormalizeLayout(new WorkspaceLayout { Groups = workspace.Groups });
            workspaces.Add(workspace with { Groups = layout.Groups });
        }

        return new WorkspaceStoreData
        {
            Workspaces = workspaces,
            LastLayout = data.LastLayout is null ? null : NormalizeLayout(data.LastLayout),
        };
    }

    private static WorkspaceLayout NormalizeLayout(WorkspaceLayout layout)
    {
        if (layout.Groups is null)
            throw new InvalidDataException("A workspace has no groups collection.");

        return new WorkspaceLayout
        {
            Groups = layout.Groups.Select(group =>
            {
                if (group is null)
                    throw new InvalidDataException("A workspace contains a null group.");
                if (group.Tabs is null)
                    throw new InvalidDataException("A workspace group has no tabs collection.");

                var tabs = new List<WorkspaceTabReference>(group.Tabs.Count);
                foreach (var tab in group.Tabs)
                {
                    if (tab is null)
                        throw new InvalidDataException("A workspace group contains a null tab.");
                    if (tab.SessionId == Guid.Empty)
                        throw new InvalidDataException("A workspace contains an empty session id.");
                    tabs.Add(tab);
                }

                if ((tabs.Count == 0 && group.ActiveTabIndex != 0)
                    || (tabs.Count > 0
                        && (group.ActiveTabIndex < 0 || group.ActiveTabIndex >= tabs.Count)))
                {
                    throw new InvalidDataException("A workspace active-tab index is outside its group.");
                }

                return group with { Tabs = tabs };
            }).ToList(),
        };
    }

    private static void WriteAtomic(string path, WorkspaceStoreData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(data, JsonOptions));
        if (File.Exists(path))
            File.Replace(tempPath, path, path + ".bak");
        else
            File.Move(tempPath, path);
    }
}

internal sealed record WorkspaceStoreData
{
    public List<Workspace>? Workspaces { get; init; }
    public WorkspaceLayout? LastLayout { get; init; }
}
