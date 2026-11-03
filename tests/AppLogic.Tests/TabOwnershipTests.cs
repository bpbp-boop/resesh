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
        Environment());

    private ViewModelEnvironment Environment() => new()
        {
            CurrentTheme = () => "dark",
            ResolveTheme = theme => theme,
            ShowAgentIcons = () => true,
            IsSessionVisible = _ => true,
            ApplySessionSettings = _ => { },
            ReportError = _errors.Add,
        };

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
        window.DetachTab(tabs[closed]);
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
        window.DetachTab(tab);
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
        {
            tabs[0].IsPinned = true;
            tabs[1].IsPinned = true;
        }
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
        tab.MarkConnected();
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

    [Fact]
    public void UnpinningTheFirstTabKeepsPinsTogetherAndPreservesSelection()
    {
        var window = Window();
        var a = window.Connect(new Session());
        var b = window.Connect(new Session());
        var c = window.Connect(new Session());
        a.IsPinned = true;
        b.IsPinned = true;
        var group = window.FocusedGroup;
        group.SelectedTab = a;

        a.IsPinned = false;

        Assert.Equal([b, a, c], group.Tabs);
        Assert.Same(a, group.SelectedTab);
        Assert.True(a.IsActive);
        // Drag and direct insert cannot cross the pin boundary either.
        group.Tabs.Move(2, 0);
        Assert.Same(b, group.Tabs[0]);
        var incoming = new TabViewModel(new Session(), Environment());
        group.Tabs.Insert(0, incoming);
        Assert.Same(b, group.Tabs[0]);
        Assert.Same(incoming, group.Tabs[1]);
    }

    [Fact]
    public void DuplicateAndForeignAttachmentsDoNotChangeOwnershipOrSubscriptions()
    {
        var source = Window();
        var target = Window();
        var tab = source.Connect(new Session());
        var other = new TabGroupViewModel();
        source.Groups.Add(other);

        Assert.Throws<InvalidOperationException>(() => source.AttachTab(tab, other, 0));
        Assert.Throws<InvalidOperationException>(() => target.AttachTab(tab, target.FocusedGroup, 0));
        Assert.Throws<InvalidOperationException>(() => other.Tabs.Add(tab));
        Assert.Throws<ArgumentException>(() => source.AttachTab(
            new TabViewModel(new Session(), Environment()), target.FocusedGroup, 0));
        Assert.Single(source.AllTabs);
        Assert.Empty(target.AllTabs);

        var notifications = 0;
        source.PropertyChanged += (_, e) => { if (e.PropertyName == "StatusText") notifications++; };
        tab.ConnectionSummary = "changed";
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void DirectRemovalAndClearRepairSelectionAndReleaseOwnership()
    {
        var window = Window();
        var a = window.Connect(new Session());
        var b = window.Connect(new Session());
        var group = window.FocusedGroup;
        group.SelectedTab = a;
        Assert.Throws<ArgumentException>(() => group.SelectedTab = new TabViewModel(new Session(), Environment()));

        group.Tabs.Remove(a);
        Assert.Same(b, window.ActiveTab);
        Assert.False(a.IsActive);
        group.Tabs.Clear();
        Assert.Null(window.ActiveTab);
        Assert.False(b.IsActive);
        var otherWindow = Window();
        otherWindow.AttachTab(a, otherWindow.FocusedGroup, 0);
        Assert.Same(a, otherWindow.ActiveTab);
    }

    [Fact]
    public void WorkspaceMovesAndGroupRemovalKeepFocusAndSubscriptionsValid()
    {
        var window = Window();
        var group = window.FocusedGroup;
        var tab = window.Connect(new Session());
        var other = new TabGroupViewModel();
        window.Groups.Add(other);
        Assert.Throws<ArgumentException>(() => window.FocusedGroup = new TabGroupViewModel());
        Assert.Throws<InvalidOperationException>(() => window.BackgroundGroups.Add(group));
        Assert.Throws<InvalidOperationException>(() => Window().Groups.Add(group));

        window.Groups.Remove(group);
        window.BackgroundGroups.Add(group);
        Assert.Same(other, window.FocusedGroup);
        Assert.False(tab.IsGroupFocused);
        Assert.True(tab.IsInBackgroundWorkspace);
        Assert.Throws<ArgumentException>(() => window.FocusedGroup = group);
        window.BackgroundGroups.Remove(group);
        window.Groups.Add(group);
        window.FocusedGroup = group;
        Assert.True(tab.IsGroupFocused);
        Assert.False(tab.IsInBackgroundWorkspace);
        var notifications = 0;
        window.PropertyChanged += (_, e) => { if (e.PropertyName == "StatusText") notifications++; };
        tab.ConnectionSummary = "changed";
        Assert.Equal(1, notifications);
        window.Groups.Clear();
        Assert.Null(window.ActiveTab);
        Assert.Throws<InvalidOperationException>(() => window.FocusedGroup);
        window.Groups.Add(other);
        Assert.Same(other, window.FocusedGroup);
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
