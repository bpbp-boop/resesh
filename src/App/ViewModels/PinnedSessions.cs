using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>Which pinned sessions reopen at launch. A pinned tab of a saved workspace belongs
/// to that workspace, which keeps it pinned and reopens it when the workspace opens; only
/// Home's pinned tabs reopen on their own. Listing both would connect a workspace's pinned
/// session twice, once in Home at launch and again when the workspace opens.</summary>
internal static class PinnedSessions
{
    /// <summary>The sessions Home's pinned tabs hold, in tab order.</summary>
    public static IReadOnlyList<Guid> Of(IEnumerable<TabViewModel> homeTabs) =>
        homeTabs.Where(tab => tab.IsPinned && !tab.IsPlayback && !tab.IsAppPage)
            .Select(tab => tab.Session.Id)
            .Distinct()
            .ToList();

    /// <summary>The list to save now that the pinned tabs may have changed, or null to keep the
    /// saved one: when nothing changed, or when no window is left. A window closing while
    /// others stay open takes its pinned tabs with it; the last one closing is the app
    /// quitting, and its pinned tabs are what reopens next time.</summary>
    public static IReadOnlyList<Guid>? ToSave(IReadOnlyList<Guid> saved, IEnumerable<TabViewModel> homeTabs, int openWindows)
    {
        if (openWindows == 0)
            return null;
        var current = Of(homeTabs);
        return current.SequenceEqual(saved) ? null : current;
    }

    /// <summary>The saved pinned sessions to reopen at launch, less those a saved workspace
    /// pins: lists kept before workspace pins stopped being listed name them, and they would
    /// open in Home and again in their workspace.</summary>
    public static IReadOnlyList<Guid> ToRestore(IReadOnlyList<Guid> saved, IEnumerable<Workspace> workspaces)
    {
        var workspacePins = workspaces
            .SelectMany(workspace => workspace.Groups)
            .SelectMany(group => group.Tabs)
            .Where(tab => tab.Pinned)
            .Select(tab => tab.SessionId)
            .ToHashSet();
        return saved.Where(id => !workspacePins.Contains(id)).Distinct().ToList();
    }
}
