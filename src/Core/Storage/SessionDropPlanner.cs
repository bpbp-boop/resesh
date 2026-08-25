using Resesh.Core.Models;

namespace Resesh.Core.Storage;

public sealed record SessionDropPlan(bool Accepted, IReadOnlyList<Guid> SessionIds);

/// <summary>Converts a completed tree drop into same-scope session moves.</summary>
public static class SessionDropPlanner
{
    public static SessionDropPlan Plan(
        bool dropSucceeded,
        IEnumerable<Session> draggedSessions,
        string targetFolder,
        SessionKind targetKind)
    {
        if (!dropSucceeded)
            return new(false, []);

        targetFolder = FolderPaths.Normalize(targetFolder);
        var sessionIds = draggedSessions
            .Where(session => session.Kind == targetKind
                && !FolderPaths.Normalize(session.FolderPath).Equals(
                    targetFolder,
                    StringComparison.OrdinalIgnoreCase))
            .Select(session => session.Id)
            .Distinct()
            .ToList();
        return new(true, sessionIds);
    }
}
