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
    public IReadOnlyList<WorkspaceTabReference> Tabs { get; set; } = [];
    public int ActiveTabIndex { get; init; }
}

/// <summary>The group arrangement shared by saved workspaces and the clean-exit layout.</summary>
public sealed record WorkspaceLayout
{
    public IReadOnlyList<WorkspaceGroup> Groups { get; set; } = [];
    public WorkspaceLayoutNode? Layout { get; init; }
}

/// <summary>A leaf group or recursive equal-size split in a saved workspace.</summary>
public sealed record WorkspaceLayoutNode
{
    public int GroupIndex { get; set; } = -1;
    public SplitOrientation? Orientation { get; init; }
    public IReadOnlyList<WorkspaceLayoutNode> Children { get; set; } = [];
    /// <summary>Arranged width or height of each child pane, in DIPs.</summary>
    public IReadOnlyList<double> Sizes { get; set; } = [];
}

/// <summary>A named, stable saved layout.</summary>
public sealed record Workspace
{
    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    public IReadOnlyList<WorkspaceGroup> Groups { get; set; } = [];
    public WorkspaceLayoutNode? Layout { get; init; }

    /// <summary>A <see cref="WorkspaceAppearance.Icons"/> key drawn on the workspace's rail
    /// button. Null, or a key the catalog does not know, draws the name's initials instead.</summary>
    public string? Icon { get; set; }

    /// <summary>Letters the rail button shows in place of the ones taken from the name. Null
    /// takes them from the name.</summary>
    public string? Initials { get; set; }

    /// <summary>Draws the initials as a small label under the icon, so workspaces that share
    /// an icon still tell apart. Without an icon the initials are drawn anyway.</summary>
    public bool LabelIcon { get; set; }
}

/// <summary>
/// JSON-backed workspace store. Candidate state is published only after the temp/swap/.bak
/// write succeeds, and loading falls back to the backup when the primary file is invalid.
/// </summary>
public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = CoreJson.With(new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    });

    private readonly StoreFile _file;
    private readonly object _gate = new();
    private List<Workspace> _workspaces = [];
    private WorkspaceLayout? _lastLayout;
    private Guid? _lastActiveWorkspaceId;
    private Dictionary<Guid, WorkspaceLayout> _drafts = [];
    public string? LoadWarning { get; private set; }

    /// <summary>Whether saves are allowed; see <see cref="StoreLoadState"/>.</summary>
    public StoreLoadState LoadState
    {
        get { lock (_gate) return _file.State; }
    }

    public WorkspaceStore(string path)
    {
        _file = new StoreFile(path, "Workspaces");
    }

    public static string DefaultPath => AppDataPaths.Roaming("workspaces.json");

    public IReadOnlyList<Workspace> Workspaces
    {
        get { lock (_gate) return _workspaces.ToList(); }
    }

    public WorkspaceLayout? LastLayout
    {
        get { lock (_gate) return _lastLayout; }
    }

    /// <summary>The workspace the last window to close was showing, reopened with the last layout.</summary>
    public Guid? LastActiveWorkspaceId
    {
        get { lock (_gate) return _lastActiveWorkspaceId; }
    }

    /// <summary>The unsaved layout a workspace was left with when its window closed, if it
    /// differs from the saved one. Opening the workspace starts from this instead.</summary>
    public WorkspaceLayout? DraftOf(Guid id)
    {
        lock (_gate) return _drafts.GetValueOrDefault(id);
    }

    public bool HasDraft(Guid id)
    {
        lock (_gate) return _drafts.ContainsKey(id);
    }

    /// <summary>Whether a live layout differs from the workspace's saved one in what a save
    /// would keep: tabs, their order and pins, and how panes split. Pane sizes and selected
    /// tabs are left out, so resizing a pane or clicking a tab is not a change. A layout with
    /// no saved sessions open is not a change either: there is nothing to save.</summary>
    public bool HasChanges(Guid id, WorkspaceLayout live)
    {
        lock (_gate) return HasChangesLocked(id, live);
    }

    private bool HasChangesLocked(Guid id, WorkspaceLayout live)
    {
        var index = _workspaces.FindIndex(workspace => workspace.Id == id);
        return index >= 0 && Differs(_workspaces[index], live);
    }

    private static bool Differs(Workspace workspace, WorkspaceLayout live)
    {
        var normalized = NormalizeLayout(live);
        if (normalized.Groups.All(group => group.Tabs.Count == 0))
            return false;
        var saved = NormalizeLayout(new WorkspaceLayout { Groups = workspace.Groups, Layout = workspace.Layout });
        return !SameArrangement(saved, normalized);
    }

    /// <summary>Keeps (or, with no changes, drops) a workspace's unsaved layout. The saved
    /// layout is never touched: only <see cref="Update"/> changes that.</summary>
    public void SetDraft(Guid id, WorkspaceLayout? live)
    {
        lock (_gate)
        {
            if (!_workspaces.Any(workspace => workspace.Id == id))
                return;
            var drafts = new Dictionary<Guid, WorkspaceLayout>(_drafts);
            if (live is not null && HasChangesLocked(id, live))
                drafts[id] = NormalizeLayout(live);
            else if (!drafts.Remove(id))
                return;
            Commit(_workspaces, _lastLayout, _lastActiveWorkspaceId, drafts);
        }
    }

    internal static bool SameArrangement(WorkspaceLayout a, WorkspaceLayout b)
    {
        if (a.Groups.Count != b.Groups.Count)
            return false;
        for (var index = 0; index < a.Groups.Count; index++)
        {
            if (!a.Groups[index].Tabs.SequenceEqual(b.Groups[index].Tabs))
                return false;
        }
        return SameNode(a.Layout, b.Layout);
    }

    private static bool SameNode(WorkspaceLayoutNode? a, WorkspaceLayoutNode? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a.Orientation != b.Orientation || a.Children.Count != b.Children.Count)
            return false;
        if (a.Orientation is null)
            return a.GroupIndex == b.GroupIndex;
        for (var index = 0; index < a.Children.Count; index++)
        {
            if (!SameNode(a.Children[index], b.Children[index]))
                return false;
        }
        return true;
    }

    public void Load()
    {
        lock (_gate)
        {
            var data = _file.Load(path => ParsePayload(File.ReadAllBytes(path))) ?? new WorkspaceStoreData { Workspaces = [] };
            LoadWarning = _file.Warning;
            _workspaces = data.Workspaces!;
            _lastLayout = data.LastLayout;
            _lastActiveWorkspaceId = data.LastActiveWorkspaceId;
            _drafts = (data.Drafts ?? []).ToDictionary(draft => draft.WorkspaceId, draft => draft.Layout);
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
            Commit([.. _workspaces, workspace]);
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
            var updated = _workspaces.ToList();
            updated[index] = updated[index] with { Name = name };
            Commit(updated);
        }
    }

    public void Update(Guid id, WorkspaceLayout layout)
    {
        lock (_gate)
        {
            var index = FindIndex(id);
            var normalized = NormalizeLayout(layout);
            var updated = _workspaces.ToList();
            updated[index] = updated[index] with
            {
                Groups = normalized.Groups,
                Layout = normalized.Layout,
            };
            var drafts = new Dictionary<Guid, WorkspaceLayout>(_drafts);
            drafts.Remove(id);
            Commit(updated, _lastLayout, _lastActiveWorkspaceId, drafts);
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var updated = _workspaces.Where(workspace => workspace.Id != id).ToList();
            var removed = updated.Count != _workspaces.Count;
            if (removed)
                Commit(updated);
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

            Commit(orderedIds.Select(id => byId[id]).ToList());
        }
    }

    public void SetAppearance(Guid id, string? icon, string? initials, bool labelIcon = false)
    {
        lock (_gate)
        {
            var index = FindIndex(id);
            var updated = _workspaces.ToList();
            updated[index] = updated[index] with
            {
                Icon = string.IsNullOrWhiteSpace(icon) ? null : icon,
                Initials = WorkspaceAppearance.NormalizeInitials(initials),
                LabelIcon = labelIcon,
            };
            Commit(updated);
        }
    }

    /// <summary>
    /// Forgets deleted sessions: their tabs leave every saved workspace, unsaved draft and the
    /// last layout, in one write, as a backup import drops sessions it did not bring. Panes
    /// stay where they were, matching a window that still shows the workspace, and a draft
    /// left the same as its saved layout is dropped. Returns whether anything changed.
    /// </summary>
    public bool RemoveSessions(IEnumerable<Guid> sessionIds)
    {
        lock (_gate)
        {
            var removed = sessionIds.ToHashSet();
            var referenced = _workspaces.SelectMany(workspace => workspace.Groups)
                .Concat(_lastLayout?.Groups ?? [])
                .Concat(_drafts.Values.SelectMany(draft => draft.Groups))
                .SelectMany(group => group.Tabs)
                .Select(tab => tab.SessionId)
                .ToHashSet();
            if (!referenced.Overlaps(removed))
                return false;

            referenced.ExceptWith(removed);
            var pruned = RemapReferences(
                new WorkspaceStoreData
                {
                    Workspaces = _workspaces,
                    LastLayout = _lastLayout,
                    LastActiveWorkspaceId = _lastActiveWorkspaceId,
                    Drafts = _drafts.Select(draft => new WorkspaceDraft { WorkspaceId = draft.Key, Layout = draft.Value }).ToList(),
                },
                new Dictionary<Guid, Guid>(),
                referenced);
            var workspaces = pruned.Workspaces!;
            var drafts = (pruned.Drafts ?? [])
                .Where(draft => workspaces.FirstOrDefault(workspace => workspace.Id == draft.WorkspaceId) is { } saved
                    && Differs(saved, draft.Layout))
                .ToDictionary(draft => draft.WorkspaceId, draft => draft.Layout);
            Commit(workspaces, pruned.LastLayout, _lastActiveWorkspaceId, drafts);
            return true;
        }
    }

    /// <summary>Saves the unsaved (Home) layout and the workspace shown beside it, if any.</summary>
    public void SaveLastLayout(WorkspaceLayout layout, Guid? activeWorkspaceId = null)
    {
        lock (_gate)
        {
            Commit(_workspaces, NormalizeLayout(layout), activeWorkspaceId);
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

    private void Commit(List<Workspace> workspaces) => Commit(workspaces, _lastLayout, _lastActiveWorkspaceId);

    private void Commit(List<Workspace> workspaces, WorkspaceLayout? lastLayout, Guid? lastActiveWorkspaceId) =>
        Commit(workspaces, lastLayout, lastActiveWorkspaceId, _drafts);

    private void Commit(
        List<Workspace> workspaces,
        WorkspaceLayout? lastLayout,
        Guid? lastActiveWorkspaceId,
        Dictionary<Guid, WorkspaceLayout> drafts)
    {
        // A deleted workspace cannot be reopened at startup, and its draft goes with it.
        if (lastActiveWorkspaceId is { } id && !workspaces.Any(workspace => workspace.Id == id))
            lastActiveWorkspaceId = null;
        drafts = drafts
            .Where(draft => workspaces.Any(workspace => workspace.Id == draft.Key))
            .ToDictionary(draft => draft.Key, draft => draft.Value);
        _file.Write(Serialize(workspaces, lastLayout, lastActiveWorkspaceId, drafts));
        _workspaces = workspaces;
        _lastLayout = lastLayout;
        _lastActiveWorkspaceId = lastActiveWorkspaceId;
        _drafts = drafts;
    }

    private static string Serialize(
        List<Workspace> workspaces,
        WorkspaceLayout? lastLayout,
        Guid? lastActiveWorkspaceId,
        Dictionary<Guid, WorkspaceLayout> drafts) =>
        JsonSerializer.Serialize(new WorkspaceStoreData
        {
            Workspaces = workspaces,
            LastLayout = lastLayout,
            LastActiveWorkspaceId = lastActiveWorkspaceId,
            Drafts = drafts.Count == 0
                ? null
                : drafts.Select(draft => new WorkspaceDraft { WorkspaceId = draft.Key, Layout = draft.Value }).ToList(),
        }, JsonOptions.Info<WorkspaceStoreData>());

    /// <summary>What the store holds, in its file format, for a backup; null when it holds
    /// nothing. Taken from memory like every other store's backup data, never copied from
    /// disk: a damaged file, or a store running from its .bak, must not end up in the archive.
    /// An empty store exports nothing, so importing that backup leaves workspaces alone
    /// rather than deleting them.</summary>
    internal byte[]? ExportBackup()
    {
        lock (_gate)
        {
            _file.EnsureLoaded();
            if (_workspaces.Count == 0 && _lastLayout is null && _drafts.Count == 0)
                return null;
            return System.Text.Encoding.UTF8.GetBytes(
                Serialize(_workspaces, _lastLayout, _lastActiveWorkspaceId, _drafts));
        }
    }

    internal static WorkspaceStoreData ParsePayload(byte[] payload)
    {
        try
        {
            var data = JsonSerializer.Deserialize(payload, JsonOptions.Info<WorkspaceStoreData>())
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
            LastActiveWorkspaceId = source.LastActiveWorkspaceId,
            Drafts = source.Drafts?.Select(draft => draft with { Layout = RemapLayout(draft.Layout) }).ToList(),
        };
    }

    /// <summary>Replaces every workspace, the last layout and drafts with a backup's. It saves
    /// like any other change: never over a file that could not be read, and published only
    /// once written, so the running app sees the import without reloading the file.</summary>
    internal void ReplaceWithImport(WorkspaceStoreData data)
    {
        var normalized = NormalizeData(data);
        lock (_gate)
        {
            Commit(
                normalized.Workspaces!,
                normalized.LastLayout,
                normalized.LastActiveWorkspaceId,
                (normalized.Drafts ?? []).ToDictionary(draft => draft.WorkspaceId, draft => draft.Layout));
        }
    }

    /// <summary>Throws unless a save may replace the file now; lets an import refuse before it
    /// changes anything else.</summary>
    internal void EnsureWritable()
    {
        lock (_gate)
            _file.EnsureWritable();
    }

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
            LastActiveWorkspaceId = data.LastActiveWorkspaceId is { } active && ids.Contains(active) ? active : null,
            // A draft for a workspace that is gone, or a second one for the same workspace, is dropped.
            Drafts = data.Drafts?
                .Where(draft => draft is not null && ids.Contains(draft.WorkspaceId) && draft.Layout is not null)
                .GroupBy(draft => draft.WorkspaceId)
                .Select(group => group.First() with { Layout = NormalizeLayout(group.First().Layout) })
                .ToList(),
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
        if (node.Sizes is null)
            throw new InvalidDataException("A workspace split node has no pane sizes collection.");

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
            if (node.Sizes.Count != 0)
                throw new InvalidDataException("A workspace leaf cannot contain pane sizes.");
            return new WorkspaceLayoutNode { GroupIndex = node.GroupIndex };
        }

        if (node.GroupIndex != -1
            || !Enum.IsDefined(node.Orientation.Value)
            || node.Children.Count < 2
            || (node.Sizes.Count != 0 && node.Sizes.Count != node.Children.Count)
            || node.Sizes.Any(size => !double.IsFinite(size) || size <= 0))
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
            Sizes = node.Sizes.ToList(),
        };
    }

}

internal sealed record WorkspaceStoreData
{
    public List<Workspace>? Workspaces { get; init; }
    public WorkspaceLayout? LastLayout { get; init; }
    public Guid? LastActiveWorkspaceId { get; init; }
    public List<WorkspaceDraft>? Drafts { get; init; }
}

/// <summary>A workspace's unsaved layout, kept across restarts until it is saved or reverted.</summary>
internal sealed record WorkspaceDraft
{
    public Guid WorkspaceId { get; set; }
    public WorkspaceLayout Layout { get; set; } = new();
}
