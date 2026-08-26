using System.Text.Json;
using Resesh.Core.Layout;

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
    public WorkspaceLayoutNode? Layout { get; init; }
}

/// <summary>A leaf group or recursive equal-size split in a saved workspace.</summary>
public sealed record WorkspaceLayoutNode
{
    public int GroupIndex { get; init; } = -1;
    public SplitOrientation? Orientation { get; init; }
    public IReadOnlyList<WorkspaceLayoutNode> Children { get; init; } = [];
}

/// <summary>A named, stable saved layout.</summary>
public sealed record Workspace
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<WorkspaceGroup> Groups { get; init; } = [];
    public WorkspaceLayoutNode? Layout { get; init; }
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
                Layout = normalized.Layout,
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
            var normalized = NormalizeLayout(layout);
            _workspaces[index] = _workspaces[index] with
            {
                Groups = normalized.Groups,
                Layout = normalized.Layout,
            };
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

    public void Reorder(IReadOnlyList<Guid> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        lock (_gate)
        {
            if (orderedIds.Count != _workspaces.Count || orderedIds.Distinct().Count() != orderedIds.Count)
                throw new ArgumentException("Workspace order must contain every workspace exactly once.", nameof(orderedIds));

            var byId = _workspaces.ToDictionary(workspace => workspace.Id);
            if (orderedIds.Any(id => !byId.ContainsKey(id)))
                throw new ArgumentException("Workspace order contains an unknown workspace.", nameof(orderedIds));
            if (_workspaces.Select(workspace => workspace.Id).SequenceEqual(orderedIds))
                return;

            _workspaces = orderedIds.Select(id => byId[id]).ToList();
            Save();
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
            Layout = layout.Layout,
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
            Workspaces = source.Workspaces!.Select(workspace =>
            {
                var remapped = RemapLayout(new WorkspaceLayout
                {
                    Groups = workspace.Groups,
                    Layout = workspace.Layout,
                });
                return workspace with
                {
                    Groups = remapped.Groups,
                    Layout = remapped.Layout,
                };
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
            var layout = NormalizeLayout(new WorkspaceLayout
            {
                Groups = workspace.Groups,
                Layout = workspace.Layout,
            });
            workspaces.Add(workspace with { Groups = layout.Groups, Layout = layout.Layout });
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

        var groups = layout.Groups.Select(group =>
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
        }).ToList();

        var normalizedLayout = layout.Layout ?? CreateLegacyLayout(groups.Count);
        if (normalizedLayout is not null)
        {
            var groupIndices = new List<int>();
            normalizedLayout = NormalizeLayoutNode(normalizedLayout, groups.Count, groupIndices);
            if (!groupIndices.SequenceEqual(Enumerable.Range(0, groups.Count)))
                throw new InvalidDataException("A workspace split layout does not contain every group in order.");
        }
        else if (groups.Count != 0)
        {
            throw new InvalidDataException("A workspace split layout is missing.");
        }

        return new WorkspaceLayout
        {
            Groups = groups,
            Layout = normalizedLayout,
        };
    }

    private static WorkspaceLayoutNode? CreateLegacyLayout(int groupCount)
    {
        if (groupCount == 0)
            return null;
        if (groupCount == 1)
            return new WorkspaceLayoutNode { GroupIndex = 0 };
        return new WorkspaceLayoutNode
        {
            Orientation = SplitOrientation.Columns,
            Children = Enumerable.Range(0, groupCount)
                .Select(index => new WorkspaceLayoutNode { GroupIndex = index })
                .ToList(),
        };
    }

    private static WorkspaceLayoutNode NormalizeLayoutNode(
        WorkspaceLayoutNode node,
        int groupCount,
        List<int> groupIndices)
    {
        if (node.Children is null)
            throw new InvalidDataException("A workspace split node has no children collection.");

        if (node.Orientation is null)
        {
            if (node.Children.Count != 0
                || node.GroupIndex < 0
                || node.GroupIndex >= groupCount
                || groupIndices.Contains(node.GroupIndex))
            {
                throw new InvalidDataException("A workspace split layout contains an invalid group leaf.");
            }
            groupIndices.Add(node.GroupIndex);
            return new WorkspaceLayoutNode { GroupIndex = node.GroupIndex };
        }

        if (node.GroupIndex != -1
            || !Enum.IsDefined(node.Orientation.Value)
            || node.Children.Count < 2)
        {
            throw new InvalidDataException("A workspace split layout contains an invalid branch.");
        }

        return new WorkspaceLayoutNode
        {
            Orientation = node.Orientation,
            Children = node.Children
                .Select(child => child is null
                    ? throw new InvalidDataException("A workspace split layout contains a null node.")
                    : NormalizeLayoutNode(child, groupCount, groupIndices))
                .ToList(),
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
