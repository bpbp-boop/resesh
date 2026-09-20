using Resesh.App.ViewModels;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

public sealed class TabOwnershipTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("resesh-tab-tests-").FullName;
    private readonly List<Exception> _errors = [];

    public void Dispose() => Directory.Delete(_directory, true);

    private MainViewModel Window() => new(
        new SessionStore(Path.Combine(_directory, Guid.NewGuid() + ".json")), new Credentials(),
        new ViewModelEnvironment
        {
            CurrentTheme = () => "dark",
            ResolveTheme = theme => theme,
            ShowAgentIcons = () => true,
            IsSessionVisible = _ => true,
            ApplySessionSettings = _ => { },
            ReportError = _errors.Add,
        });

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 1, 2)]
    [InlineData(3, 3, 2)]
    [InlineData(0, 2, 2)]
    [InlineData(3, 1, 1)]
    public void ClosingSelectsNeighbourOrPreservesInactiveSelection(int closed, int selected, int expected)
    {
        var window = Window();
        var tabs = Enumerable.Range(0, 4).Select(_ => window.Connect(new Session { Id = Guid.NewGuid() })).ToArray();
        var group = window.FocusedGroup;
        group.SelectedTab = tabs[selected];
        // Model the native control selecting another item during its removal handler.
        group.Tabs.CollectionChanged += (_, _) => group.SelectedTab = group.Tabs.LastOrDefault();
        window.CloseTab(tabs[closed]);
        Assert.Same(tabs[expected], group.SelectedTab);
        Assert.True(tabs[expected].IsActive);
        Assert.False(tabs[closed].IsActive);
        Assert.Single(group.Tabs, tab => tab.IsActive);
    }

    [Fact]
    public void ClosingOnlyTabClearsSelection()
    {
        var window = Window();
        var tab = window.Connect(new Session { Id = Guid.NewGuid() });
        window.CloseTab(tab);
        Assert.Null(window.FocusedGroup.SelectedTab);
        Assert.False(tab.IsActive);
    }

    [Theory]
    [InlineData(0, false, 1)]
    [InlineData(1, false, 2)]
    [InlineData(3, false, 4)]
    [InlineData(0, true, 2)]
    [InlineData(1, true, 2)]
    public void CloneOpensBesideSourceAfterPinnedPrefix(int sourceIndex, bool pinFirstTwo, int expectedIndex)
    {
        var window = Window();
        var group = window.FocusedGroup;
        var tabs = Enumerable.Range(0, 4).Select(_ => window.Connect(new Session { Id = Guid.NewGuid(), Persistent = true })).ToArray();
        if (pinFirstTwo)
            tabs[0].IsPinned = tabs[1].IsPinned = true;
        var otherGroup = new TabGroupViewModel();
        window.Groups.Add(otherGroup);
        window.FocusedGroup = otherGroup;
        var source = tabs[sourceIndex];

        var clone = window.Connect(source.Session, insertAfter: source);

        Assert.Same(clone, group.Tabs[expectedIndex]);
        Assert.Same(clone, group.SelectedTab);
        Assert.True(clone.IsActive);
        Assert.False(clone.IsPinned);
        Assert.Same(source.Session, clone.Session);
        Assert.NotEqual(source.TmuxSlot, clone.TmuxSlot);
        Assert.Equal(tabs, group.Tabs.Where(tab => tab != clone));
        Assert.Empty(otherGroup.Tabs);
    }

    [Fact]
    public void TransferPreservesLiveViewStateAndMovesNotificationOwnership()
    {
        var source = Window();
        var target = Window();
        var tab = source.Connect(new Session { Id = Guid.NewGuid(), Name = "live", Host = "host" });
        var view = new View();
        tab.View = view;
        tab.IsPinned = true;
        tab.State = TabConnectionState.Connected;
        tab.ApplyRunningCommand("long running command");

        var runningCommand = tab.RunningCommand;
        source.DetachTab(tab);
        target.AttachTab(tab, target.FocusedGroup, 0);
        Assert.Empty(source.AllTabs);
        Assert.Same(tab, Assert.Single(target.AllTabs));
        Assert.Same(view, tab.View);
        Assert.True(tab.IsPinned);
        Assert.Equal(TabConnectionState.Connected, tab.State);
        Assert.Equal(runningCommand, tab.RunningCommand);
        Assert.Equal(0, view.Disposals);

        var sourceNotifications = 0;
        var targetNotifications = 0;
        source.PropertyChanged += (_, e) => { if (e.PropertyName == "StatusText") sourceNotifications++; };
        target.PropertyChanged += (_, e) => { if (e.PropertyName == "StatusText") targetNotifications++; };
        tab.ConnectionSummary = "updated";
        Assert.Equal(0, sourceNotifications);
        Assert.Equal(1, targetNotifications);

        source.CloseAllTabs();
        Assert.Equal(0, view.Disposals);
        target.CloseAllTabs();
        Assert.Equal(1, view.Disposals);
    }

    [Fact]
    public void ClosingAllTabsContinuesAcrossGroupsAfterOneViewFails()
    {
        var window = Window();
        var first = window.Connect(new Session { Id = Guid.NewGuid() });
        var failed = new View { Fail = true };
        first.View = failed;
        var group = new TabGroupViewModel();
        window.Groups.Add(group);
        var second = window.Connect(new Session { Id = Guid.NewGuid() }, group);
        var successful = new View();
        second.View = successful;
        window.CloseAllTabs();
        Assert.Equal(1, failed.Disposals);
        Assert.Equal(1, successful.Disposals);
        Assert.Single(_errors);
        Assert.Empty(window.AllTabs);
        Assert.All(window.Groups, item => Assert.Null(item.SelectedTab));
        window.CloseAllTabs();
        Assert.Equal(1, successful.Disposals);
    }

    [Fact]
    public void RepeatedTransfersDoNotDuplicateSubscriptions()
    {
        var first = Window();
        var second = Window();
        var tab = first.Connect(new Session { Id = Guid.NewGuid() });
        for (var index = 0; index < 5; index++)
        {
            first.DetachTab(tab);
            second.AttachTab(tab, second.FocusedGroup, 0);
            second.DetachTab(tab);
            first.AttachTab(tab, first.FocusedGroup, 0);
        }
        var notifications = 0;
        first.PropertyChanged += (_, e) => { if (e.PropertyName == "StatusText") notifications++; };
        tab.ConnectionSummary = "new";
        Assert.Equal(1, notifications);
    }

    private sealed class View : IDisposable
    {
        public int Disposals;
        public bool Fail;
        public void Dispose()
        {
            Disposals++;
            if (Fail) throw new IOException("Test cleanup failure");
        }
    }

    private sealed class Credentials : ICredentialService
    {
        public string? Read(Guid sessionId) => null;
        public void Write(Guid sessionId, string secret) { }
        public void Delete(Guid sessionId) { }
    }
}
