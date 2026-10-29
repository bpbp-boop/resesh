namespace Resesh.Core.Storage;

/// <summary>Coordinates workspace writes and reports recoverable failures to its caller.</summary>
public sealed class WorkspaceCoordinator(
    WorkspaceStore store,
    Func<WorkspaceLayout> capture,
    Action changed,
    Action<string, string> report)
{
    public bool SaveAs(string name) => Create(name, capture()) is not null;

    /// <summary>Saves a new workspace with the layout given, or reports why it could not.</summary>
    public Workspace? Create(string name, WorkspaceLayout layout)
    {
        Workspace? created = null;
        Execute("Workspace was not saved", () => created = store.SaveAs(name, layout));
        return created;
    }
    public bool Rename(Guid id, string name) => Execute("Workspace was not renamed", () => store.Rename(id, name));
    public bool Update(Guid id) => Execute("Workspace was not updated", () => store.Update(id, capture()));
    public bool Delete(Guid id) => Execute("Workspace was not deleted", () => store.Delete(id));
    public bool Reorder(IReadOnlyList<Guid> ids) => Execute("Workspace order was not saved", () => store.Reorder(ids));
    public bool SetAppearance(Guid id, string? icon, string? initials) =>
        Execute("Workspace was not changed", () => store.SetAppearance(id, icon, initials));

    /// <summary>Keeps an open workspace's saved layout in step with what it shows. A workspace
    /// deleted meanwhile, or one left with no saved sessions, keeps what it had.</summary>
    public bool AutoSave(Guid id, WorkspaceLayout layout)
    {
        if (!store.Workspaces.Any(workspace => workspace.Id == id)
            || layout.Groups.All(group => group.Tabs.Count == 0))
            return false;
        return Execute("Workspace was not saved", () => store.Update(id, layout), refresh: false);
    }

    public bool SaveLastLayout(WorkspaceLayout layout, Guid? activeWorkspaceId) =>
        Execute("The last layout was not saved", () => store.SaveLastLayout(layout, activeWorkspaceId), refresh: false);

    private bool Execute(string failure, Action write, bool refresh = true)
    {
        try { write(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException)
        {
            if (refresh) changed(); // Restore menus after an optimistic drag or edit.
            report(failure, exception.Message);
            return false;
        }
        if (refresh) changed();
        return true;
    }
}
