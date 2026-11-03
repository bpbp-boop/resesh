namespace Resesh.App.ViewModels;

/// <summary>What closing one tab asks first.</summary>
public enum TabCloseKind
{
    /// <summary>Close at once: an app page, or confirmation is off for an unpinned tab.</summary>
    Now,
    Confirm,
    /// <summary>One dialog both unpins and closes.</summary>
    ConfirmPinned,
    /// <summary>The tab is attached to a persistent shell: the dialog offers to end it too.</summary>
    ConfirmPersistent,
}

/// <summary>The confirmation a tab close shows, decided before any dialog is built.</summary>
public sealed record TabClosePlan(TabCloseKind Kind, string Title = "", string Message = "", string PrimaryText = "");

// Bookkeeping for tab closes and lock prompts that wait on the user or the network.
public sealed partial class MainViewModel
{
    private readonly HashSet<TabViewModel> _closingTabs = [];

    /// <summary>The group holding the tab, or null once the tab has been closed or moved to
    /// another window. Asynchronous close flows ask this after every await rather than
    /// <see cref="GroupOf"/>, which throws for a tab the window no longer owns.</summary>
    public TabGroupViewModel? TryGroupOf(TabViewModel tab) =>
        Groups.Concat(BackgroundGroups).FirstOrDefault(g => g.Tabs.Contains(tab));

    /// <summary>Whether the window still holds the tab, in any workspace.</summary>
    public bool Owns(TabViewModel tab) => TryGroupOf(tab) is not null;

    /// <summary>Whether a close of the tab is waiting on a confirmation or a remote shell.</summary>
    public bool IsClosing(TabViewModel tab) => _closingTabs.Contains(tab);

    /// <summary>Claims the tab for a close that may wait. False when the window doesn't own
    /// it or another close of it is already in flight, so a second Ctrl+W, middle-click or
    /// bulk close during the first one's dialog doesn't stack another dialog for the same
    /// tab. Every successful claim must be released with <see cref="EndClose"/>.</summary>
    public bool TryBeginClose(TabViewModel tab) => Owns(tab) && _closingTabs.Add(tab);

    /// <summary>Releases a claim from <see cref="TryBeginClose"/>, whether the tab closed or not.</summary>
    public void EndClose(TabViewModel tab) => _closingTabs.Remove(tab);

    /// <summary>What closing the tab asks first. The window's close pathway shows the dialog
    /// this describes, then removes the tab with <see cref="DetachTab"/>.</summary>
    public TabClosePlan PlanClose(TabViewModel tab, bool confirmActiveSessions)
    {
        if (tab.IsAppPage || (!tab.IsPinned && !confirmActiveSessions))
            return new(TabCloseKind.Now);

        if (tab.IsAttachedToRemoteSession && HasTerminal(tab))
        {
            return tab.IsPinned
                ? new(TabCloseKind.ConfirmPersistent, "Tab Is Pinned",
                    $"\"{tab.Header}\" is pinned. Closing the tab also unpins it.", "Unpin and Close")
                : new(TabCloseKind.ConfirmPersistent, "Close Tab", $"Close \"{tab.Header}\"?", "Close Tab");
        }

        var detail = tab.State != TabConnectionState.Connected
            ? ""
            : tab.IsLocal
                ? " The process is still running — closing stops it and everything it started."
                : tab.IsAttachedToRemoteSession
                    ? " The remote session keeps running (persistent) — connecting again resumes it."
                    : " The session is still connected.";
        return tab.IsPinned
            ? new(TabCloseKind.ConfirmPinned, "Tab Is Pinned", $"\"{tab.Header}\" is pinned.{detail}", "Unpin and Close")
            : new(TabCloseKind.Confirm, "Close Tab", $"Close \"{tab.Header}\"?{detail}", "Close");
    }

    /// <summary>The question a bulk close asks ("Close 3 other tab(s)? 1 of them is still
    /// connected.").</summary>
    public static string CloseManyMessage(IReadOnlyCollection<TabViewModel> tabs, string description)
    {
        var connected = tabs.Count(t => t.State == TabConnectionState.Connected);
        return connected > 0
            ? $"Close {tabs.Count} {description}? {connected} of them {(connected == 1 ? "is" : "are")} still connected."
            : $"Close {tabs.Count} {description}?";
    }

    private readonly HashSet<TabViewModel> _lockPromptTabs = [];

    /// <summary>Whether a lock or unlock prompt for the tab is open or queued.</summary>
    public bool IsLockPromptOpen(TabViewModel tab) => _lockPromptTabs.Contains(tab);

    /// <summary>Claims the tab for a lock or unlock prompt. False when the window doesn't own
    /// it or a prompt for it is already waiting, so a second "Lock Session…" can't queue a
    /// second password that silently replaces the first, and a second unlock click can't
    /// count a stale attempt. Release every successful claim with <see cref="EndLockPrompt"/>.</summary>
    public bool TryBeginLockPrompt(TabViewModel tab) => Owns(tab) && _lockPromptTabs.Add(tab);

    /// <summary>Releases a claim from <see cref="TryBeginLockPrompt"/>.</summary>
    public void EndLockPrompt(TabViewModel tab) => _lockPromptTabs.Remove(tab);
}
