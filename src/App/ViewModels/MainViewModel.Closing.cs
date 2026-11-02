namespace Resesh.App.ViewModels;

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
