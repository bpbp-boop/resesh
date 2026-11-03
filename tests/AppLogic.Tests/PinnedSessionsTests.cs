using Resesh.App.ViewModels;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

public sealed class PinnedSessionsTests
{
    private static readonly ViewModelEnvironment Environment = new()
    {
        CurrentTheme = () => "dark",
        ResolveTheme = theme => theme,
        ShowAgentIcons = () => true,
        IsSessionVisible = _ => true,
        ApplySessionSettings = _ => { },
        ReportError = _ => { },
    };

    private static TabViewModel Tab(Guid session, bool pinned) =>
        new(new Session { Id = session }, Environment) { IsPinned = pinned };

    [Fact]
    public void OnlyHomesPinnedSessionsAreListedOnceEachInTabOrder()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), unpinned = Guid.NewGuid();
        var page = TabViewModel.CreateAppPage(AppPage.Settings, Environment);
        page.IsPinned = true;

        var ids = PinnedSessions.Of([Tab(second, true), Tab(unpinned, false), Tab(first, true), Tab(second, true), page]);

        Assert.Equal([second, first], ids);
    }

    [Fact]
    public void ASaveIsSkippedWhenNothingChanged()
    {
        var id = Guid.NewGuid();

        Assert.Null(PinnedSessions.ToSave([id], [Tab(id, true)], openWindows: 1));
        Assert.Equal([id], PinnedSessions.ToSave([], [Tab(id, true)], openWindows: 1));
    }

    [Fact]
    public void ClosingAWindowDropsItsPinsButTheLastWindowKeepsThem()
    {
        Guid stays = Guid.NewGuid(), closes = Guid.NewGuid();
        IReadOnlyList<Guid> saved = [stays, closes];

        // Another window is still open: only its Home tabs are left to count.
        Assert.Equal([stays], PinnedSessions.ToSave(saved, [Tab(stays, true)], openWindows: 1));
        // The last window closed: the app is quitting, and its pins reopen next time.
        Assert.Null(PinnedSessions.ToSave(saved, [], openWindows: 0));
    }

    [Fact]
    public void ASessionAWorkspacePinsIsNotRestoredInHome()
    {
        Guid home = Guid.NewGuid(), workspacePin = Guid.NewGuid(), workspaceTab = Guid.NewGuid();
        var lab = new Workspace
        {
            Id = Guid.NewGuid(),
            Groups =
            [
                new WorkspaceGroup
                {
                    Tabs =
                    [
                        new WorkspaceTabReference { SessionId = workspacePin, Pinned = true },
                        new WorkspaceTabReference { SessionId = workspaceTab },
                    ],
                },
            ],
        };

        Assert.Equal([home, workspaceTab], PinnedSessions.ToRestore([home, workspacePin, workspaceTab], [lab]));
    }
}
