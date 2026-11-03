namespace Resesh.Core.Storage;

/// <summary>Coordinates workspace writes and reports recoverable failures to its caller.
/// A write that succeeds is announced by <see cref="WorkspaceStore.Changed"/>.</summary>
/// <param name="liveLayout">A workspace's own layout as it stands where it is open, or null
/// when no window has it open. Keyed by id, so an update can only ever capture the workspace
/// it updates, never whichever one happens to be shown.</param>
/// <param name="restore">Called when a write fails, to put back menus a drag or an edit
/// changed before the write was tried.</param>
public sealed class WorkspaceCoordinator(
    WorkspaceStore store,
    Func<Guid, WorkspaceLayout?> liveLayout,
    Action restore,
    Action<string, string> report)
{

    /// <summary>Saves a new workspace with the layout given, or reports why it could not.</summary>
    public Workspace? Create(string name, WorkspaceLayout layout)
    {
        Workspace? created = null;
        Execute("Workspace was not saved", () => created = store.SaveAs(name, layout));
        return created;
    }
    public bool Rename(Guid id, string name) => Execute("Workspace was not renamed", () => store.Rename(id, name));

    /// <summary>Saves a workspace's live layout as what it opens to. A workspace that is not
    /// open has no live layout, so nothing changes.</summary>
    public bool Update(Guid id) => liveLayout(id) is { } layout && Save(id, layout);

    public bool Delete(Guid id) => Execute("Workspace was not deleted", () => store.Delete(id));
    public bool Reorder(IReadOnlyList<Guid> ids) => Execute("Workspace order was not saved", () => store.Reorder(ids));
    public bool SetAppearance(Guid id, string? icon, string? initials, bool labelIcon) =>
        Execute("Workspace was not changed", () => store.SetAppearance(id, icon, initials, labelIcon));

    /// <summary>Saves a workspace's layout as what it opens to. A workspace deleted meanwhile,
    /// or a layout with no saved sessions in it, keeps what it had.</summary>
    public bool Save(Guid id, WorkspaceLayout layout)
    {
        if (!store.Workspaces.Any(workspace => workspace.Id == id)
            || layout.Groups.All(group => group.Tabs.Count == 0))
            return false;
        return Execute("Workspace was not saved", () => store.Update(id, layout));
    }

    /// <summary>Remembers an open workspace's unsaved layout for the next time it opens,
    /// without changing what it was saved as.</summary>
    public bool KeepDraft(Guid id, WorkspaceLayout layout) =>
        Execute("Unsaved workspace changes were not kept", () => store.SetDraft(id, layout), refresh: false);

    /// <summary>Forgets a workspace's unsaved layout, so it opens as saved.</summary>
    public bool DiscardDraft(Guid id) =>
        Execute("Workspace changes were not discarded", () => store.SetDraft(id, null));

    public bool SaveLastLayout(WorkspaceLayout layout, Guid? activeWorkspaceId) =>
        Execute("The last layout was not saved", () => store.SaveLastLayout(layout, activeWorkspaceId), refresh: false);

    private bool Execute(string failure, Action write, bool refresh = true)
    {
        try { write(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException)
        {
            if (refresh) restore();
            report(failure, exception.Message);
            return false;
        }
        return true;
    }
}
