namespace Resesh.Core.Storage;

/// <summary>Coordinates workspace writes and reports recoverable failures to its caller.</summary>
public sealed class WorkspaceCoordinator(
    WorkspaceStore store,
    Func<WorkspaceLayout> capture,
    Action changed,
    Action<string, string> report)
{
    public bool SaveAs(string name) => Execute("Workspace was not saved", () => store.SaveAs(name, capture()));
    public bool Rename(Guid id, string name) => Execute("Workspace was not renamed", () => store.Rename(id, name));
    public bool Update(Guid id) => Execute("Workspace was not updated", () => store.Update(id, capture()));
    public bool Delete(Guid id) => Execute("Workspace was not deleted", () => store.Delete(id));
    public bool Reorder(IReadOnlyList<Guid> ids) => Execute("Workspace order was not saved", () => store.Reorder(ids));
    public bool SaveLastLayout() => Execute("The last layout was not saved", () => store.SaveLastLayout(capture()), refresh: false);

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
