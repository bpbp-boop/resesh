namespace Resesh.App.ViewModels;

/// <summary>The rules a window's workspaces keep. A window shows exactly one workspace and
/// parks the others it holds, keyed by id with Home under <see cref="Home"/>. The shown one
/// is never also parked: a window that both showed and parked a workspace would lay out the
/// same groups twice, and parking over an entry orphans the groups it held, still connected
/// but reachable from no button.</summary>
internal static class WorkspaceParking
{
    /// <summary>Home's key: the unsaved tabs have no workspace id.</summary>
    public static readonly Guid Home = Guid.Empty;

    /// <summary>Whether the shown workspace is kept out of the parked ones.</summary>
    public static bool IsConsistent<TParked>(Guid shown, IReadOnlyDictionary<Guid, TParked> parked) =>
        !parked.ContainsKey(shown);

    /// <summary>Whether the shown tabs can become a new workspace, leaving an empty Home
    /// parked behind: only while Home is shown, so the shown tabs are Home's, and so there is
    /// no parked Home the new empty one would replace.</summary>
    public static bool CanAdoptShownAsNew<TParked>(Guid shown, IReadOnlyDictionary<Guid, TParked> parked) =>
        shown == Home && !parked.ContainsKey(Home);

    /// <summary>Whether the rail's + button turns the shown tabs into the new workspace
    /// rather than starting an empty one: Home is shown and holds a session (app pages such
    /// as Settings don't count). Decided when the name is entered, not when the button is
    /// pressed, since the window may have switched workspace while the prompt was open.</summary>
    public static bool TakesShownTabs<TParked>(
        Guid shown, IReadOnlyDictionary<Guid, TParked> parked, IEnumerable<TabViewModel> shownTabs) =>
        CanAdoptShownAsNew(shown, parked) && shownTabs.Any(tab => !tab.IsAppPage);

    /// <summary>Whether a workspace's tabs can be added to the shown layout. Adding opens a
    /// new connection for each of its tabs, so a workspace a window already holds, this one
    /// or another, is never added: its connections would be open twice.</summary>
    public static bool CanAddToShownLayout(bool openHere, bool openElsewhere) => !openHere && !openElsewhere;

    /// <summary>Whether "Update from current layout" is offered for a workspace: only while
    /// this window holds it, shown or parked, since what it saves is that workspace's own
    /// live layout. A workspace open nowhere here has none to save.</summary>
    public static bool CanUpdateFromLiveLayout(bool openHere) => openHere;
}
