using Resesh.App.ViewModels;
using Resesh.Core.Models;

namespace Resesh.AppLogic.Tests;

public sealed class WorkspaceParkingTests
{
    private static readonly Guid Saved = Guid.NewGuid();

    private static readonly ViewModelEnvironment Environment = new()
    {
        CurrentTheme = () => "dark",
        ResolveTheme = theme => theme,
        ShowAgentIcons = () => true,
        IsSessionVisible = _ => true,
        ApplySessionSettings = _ => { },
        ReportError = _ => { },
    };

    private static Dictionary<Guid, string> Parked(params Guid[] ids) => ids.ToDictionary(id => id, id => id.ToString());

    private static TabViewModel Session() => new(new Session { Id = Guid.NewGuid() }, Environment);

    [Fact]
    public void ShownWorkspaceMustNotAlsoBeParked()
    {
        Assert.True(WorkspaceParking.IsConsistent(WorkspaceParking.Home, Parked(Saved)));
        Assert.True(WorkspaceParking.IsConsistent(Saved, Parked(WorkspaceParking.Home)));
        Assert.False(WorkspaceParking.IsConsistent(Saved, Parked(WorkspaceParking.Home, Saved)));
    }

    [Fact]
    public void HomeTabsBecomeANewWorkspaceOnlyWhileHomeIsShown()
    {
        Assert.True(WorkspaceParking.CanAdoptShownAsNew(WorkspaceParking.Home, Parked(Saved)));
        // Another window showed a saved workspace here while the name prompt was open: the
        // shown tabs are that workspace's, and Home is parked with its own tabs.
        Assert.False(WorkspaceParking.CanAdoptShownAsNew(Saved, Parked(WorkspaceParking.Home)));
        // A parked Home would be replaced by the new empty one, stranding its tabs.
        Assert.False(WorkspaceParking.CanAdoptShownAsNew(WorkspaceParking.Home, Parked(WorkspaceParking.Home)));
    }

    [Fact]
    public void NewWorkspaceTakesTheShownTabsOnlyWhenHomeHoldsASession()
    {
        var home = WorkspaceParking.Home;
        Assert.True(WorkspaceParking.TakesShownTabs(home, Parked(), [Session()]));
        Assert.False(WorkspaceParking.TakesShownTabs(home, Parked(), []));
        Assert.False(WorkspaceParking.TakesShownTabs(home, Parked(),
            [TabViewModel.CreateAppPage(AppPage.Settings, Environment)]));
        Assert.False(WorkspaceParking.TakesShownTabs(Saved, Parked(home), [Session()]));
    }
}
