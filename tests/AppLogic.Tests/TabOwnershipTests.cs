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
