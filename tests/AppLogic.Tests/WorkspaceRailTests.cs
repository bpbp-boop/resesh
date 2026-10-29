using Resesh.App.ViewModels;
using Resesh.Core.Agents;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

public sealed class WorkspaceRailTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("resesh-rail-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    private static readonly ViewModelEnvironment Environment = new()
    {
        CurrentTheme = () => "dark",
        ResolveTheme = theme => theme,
        ShowAgentIcons = () => true,
        IsSessionVisible = _ => true,
        ApplySessionSettings = _ => { },
        ReportError = _ => { },
    };

    private MainViewModel Window() => new(
        new SessionStore(Path.Combine(_directory, Guid.NewGuid() + ".json")), new Credentials(), Environment);

    private static TabViewModel Tab(TabConnectionState state = TabConnectionState.Connected) =>
        new(new Session { Id = Guid.NewGuid(), Name = "edge" }, Environment) { State = state };

    [Fact]
    public void Summarize_ReportsTheMostUrgentAlert()
    {
        var quiet = Tab();
        var busy = Tab();
        busy.NotifyOutputActivity();
        var dropped = Tab(TabConnectionState.Disconnected);
        var waiting = Tab();
        waiting.Agent = new AgentSnapshot("claude", AgentAttention.NeedsApproval, null, AgentSource.Structured);

        Assert.Equal(WorkspaceAlert.None, WorkspaceAlerts.Summarize([quiet]));
        Assert.Equal(WorkspaceAlert.Activity, WorkspaceAlerts.Summarize([quiet, busy]));
        Assert.Equal(WorkspaceAlert.Disconnected, WorkspaceAlerts.Summarize([busy, dropped]));
        Assert.Equal(WorkspaceAlert.Attention, WorkspaceAlerts.Summarize([dropped, waiting, busy]));
    }

    [Fact]
    public void Summarize_IgnoresAppPagesAndPlaybackThatAreNeverConnected()
    {
        var settings = TabViewModel.CreateAppPage(AppPage.Settings, Environment);
        settings.State = TabConnectionState.Disconnected;
        var playback = Tab(TabConnectionState.Disconnected);
        playback.PlaybackPath = "x.cast";

        Assert.Equal(WorkspaceAlert.None, WorkspaceAlerts.Summarize([settings, playback]));
    }

    [Fact]
    public void BackgroundTab_CollectsUnseenOutputUntilItsWorkspaceIsShown()
    {
        var tab = Tab();
        tab.IsActive = true;
        tab.NotifyOutputActivity();
        Assert.False(tab.HasUnseenOutput);

        tab.IsInBackgroundWorkspace = true;
        Assert.False(tab.IsOnScreen);
        tab.NotifyOutputActivity();
        Assert.True(tab.HasUnseenOutput);

        tab.IsInBackgroundWorkspace = false;
        Assert.True(tab.IsOnScreen);
        Assert.False(tab.HasUnseenOutput);
    }

    [Fact]
    public void BackgroundGroups_BelongToTheWindowButAreNotShown()
    {
        var window = Window();
        var shown = window.Connect(new Session { Id = Guid.NewGuid(), Name = "shown" });
        var parked = new TabGroupViewModel();
        var hidden = new TabViewModel(new Session { Id = Guid.NewGuid(), Name = "hidden" }, Environment);
        parked.Tabs.Add(hidden);
        window.BackgroundGroups.Add(parked);

        Assert.Equal([shown, hidden], window.AllTabs);
        Assert.Equal([shown], window.ShownTabs);
        Assert.Same(parked, window.GroupOf(hidden));
        Assert.True(window.IsBackgroundGroup(parked));
        Assert.False(window.IsBackgroundGroup(window.FocusedGroup));

        window.CloseAllTabs();
        Assert.Empty(window.AllTabs);
    }

    [Fact]
    public void TabChanged_ReportsPropertiesOfTabsInTheWindow()
    {
        var window = Window();
        var tab = window.Connect(new Session { Id = Guid.NewGuid() });
        var changes = new List<string?>();
        window.TabChanged += (changed, property) =>
        {
            Assert.Same(tab, changed);
            changes.Add(property);
        };

        tab.State = TabConnectionState.Disconnected;

        Assert.Contains(nameof(TabViewModel.State), changes);
    }

    [Fact]
    public void RailItem_ShowsStateMarkerAndAlertOnlyInTheBackground()
    {
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Prod incident", Icon = "warning" };
        var item = WorkspaceRailItemViewModel.For(workspace);

        Assert.Equal("PI", item.Monogram);
        Assert.Equal(WorkspaceAppearance.Find("warning")!.Glyph, item.Glyph);
        Assert.True(item.HasIcon);
        Assert.Equal(0, item.MarkerHeight);
        Assert.True(item.TileOpacity < 1);

        item.SetState(isOpen: true, isActive: false, isOpenElsewhere: false, WorkspaceAlert.Attention, position: 2);
        Assert.Equal(8, item.MarkerHeight);
        Assert.True(item.HasAlert);
        Assert.Equal("Prod incident — open, an agent needs you (Ctrl+Shift+2)", item.Tooltip);
        Assert.Equal("Open in the background", item.StatusText);
        Assert.Equal("An agent needs you", item.AlertText);
        Assert.Equal("Ctrl+Shift+2", item.ShortcutText);

        item.SetState(isOpen: true, isActive: true, isOpenElsewhere: false, WorkspaceAlert.Attention, position: 2);
        Assert.Equal(20, item.MarkerHeight);
        Assert.False(item.HasAlert);
        Assert.Equal(1.0, item.TileOpacity);

        item.SetState(isOpen: false, isActive: false, isOpenElsewhere: true, WorkspaceAlert.None, position: 12);
        Assert.True(item.IsOpenElsewhere);
        Assert.Equal("Prod incident — open in another window", item.Tooltip);
        Assert.Equal("Open in another window", item.StatusText);
        Assert.Equal("", item.AlertText);
        Assert.Equal("", item.ShortcutText);
    }

    [Fact]
    public void RailItem_UpdateFollowsRenameAndCustomisation()
    {
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Lab" };
        var item = WorkspaceRailItemViewModel.For(workspace);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Update(workspace with { Name = "Core network", Initials = "cor" });
        Assert.Equal("cor", item.Monogram);

        item.Update(workspace with { Name = "Core network", Icon = "server" });

        Assert.Equal("Core network", item.Name);
        Assert.Equal("CN", item.Monogram);
        Assert.True(item.HasIcon);
        Assert.Contains(nameof(WorkspaceRailItemViewModel.Monogram), changed);
        Assert.Contains(nameof(WorkspaceRailItemViewModel.Glyph), changed);
        Assert.Contains(nameof(WorkspaceRailItemViewModel.HasIcon), changed);

        item.Update(workspace with { Icon = "database" });
        Assert.Equal("", item.Glyph);
        Assert.Equal(WorkspaceAppearance.Find("database")!.Path, item.IconPath);
        Assert.True(item.HasIcon);

        // A key this build does not know, such as a session icon, falls back to the initials.
        item.Update(workspace with { Icon = "debian" });
        Assert.Equal("", item.Glyph);
        Assert.Equal("", item.IconPath);
        Assert.False(item.HasIcon);
    }

    [Fact]
    public void RailItem_ShowsUnsavedChangesUnlessAnotherWindowHasIt()
    {
        var item = WorkspaceRailItemViewModel.For(new Workspace { Id = Guid.NewGuid(), Name = "Lab" });

        item.SetState(isOpen: true, isActive: false, isOpenElsewhere: false, WorkspaceAlert.None, position: 2, hasUnsavedChanges: true);
        Assert.True(item.ShowsUnsavedMark);
        Assert.Equal("Open in the background · unsaved changes", item.StatusText);
        Assert.Equal("Lab — open, unsaved changes (Ctrl+Shift+2)", item.Tooltip);

        item.SetState(isOpen: false, isActive: false, isOpenElsewhere: true, WorkspaceAlert.None, position: 2, hasUnsavedChanges: true);
        Assert.True(item.HasUnsavedChanges);
        Assert.False(item.ShowsUnsavedMark);

        var home = WorkspaceRailItemViewModel.CreateHome();
        home.SetState(true, true, false, WorkspaceAlert.None, 1, hasUnsavedChanges: true);
        Assert.False(home.HasUnsavedChanges);
    }

    [Fact]
    public void HomeItem_IsAlwaysOpenAndHasNoWorkspace()
    {
        var home = WorkspaceRailItemViewModel.CreateHome();
        home.SetState(isOpen: false, isActive: false, isOpenElsewhere: false, WorkspaceAlert.Activity, position: 1);

        Assert.True(home.IsHome);
        Assert.Null(home.Workspace);
        Assert.True(home.IsOpen);
        Assert.Equal(WorkspaceAppearance.HomeGlyph, home.Glyph);
        Assert.Equal("Home — tabs outside saved workspaces, new output (Ctrl+Shift+1)", home.Tooltip);
    }

    private sealed class Credentials : ICredentialService
    {
        public string? Read(Guid sessionId) => null;
        public void Write(Guid sessionId, string secret) { }
        public void Delete(Guid sessionId) { }
    }
}
