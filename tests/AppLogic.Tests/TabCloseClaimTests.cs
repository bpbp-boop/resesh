using Resesh.App.ViewModels;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

/// <summary>The bookkeeping that lets a close wait on a dialog without acting on a tab that
/// closed, moved to another window, or is already being closed by another request.</summary>
public sealed class TabCloseClaimTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("resesh-close-tests-").FullName;

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
            ReportError = _ => { },
        });

    [Fact]
    public void SecondCloseOfTheSameTabIsRefusedUntilTheFirstEnds()
    {
        var window = Window();
        var tab = window.Connect(new Session { Id = Guid.NewGuid() });

        Assert.True(window.TryBeginClose(tab));
        Assert.True(window.IsClosing(tab));
        Assert.False(window.TryBeginClose(tab));

        window.EndClose(tab);
        Assert.False(window.IsClosing(tab));
        Assert.True(window.TryBeginClose(tab));
    }

    [Fact]
    public void TabClosedDuringTheDialogIsNoLongerOwned()
    {
        var window = Window();
        var tab = window.Connect(new Session { Id = Guid.NewGuid() });
        Assert.True(window.TryBeginClose(tab));

        window.CloseTab(tab);

        Assert.False(window.Owns(tab));
        Assert.Null(window.TryGroupOf(tab));
        Assert.Throws<InvalidOperationException>(() => window.GroupOf(tab));
        window.EndClose(tab);
    }

    [Fact]
    public void TabMovedToAnotherWindowDuringTheDialogIsNoLongerOwned()
    {
        var source = Window();
        var target = Window();
        var tab = source.Connect(new Session { Id = Guid.NewGuid() });
        Assert.True(source.TryBeginClose(tab));

        source.DetachTab(tab);
        target.AttachTab(tab, target.FocusedGroup, 0);

        Assert.False(source.Owns(tab));
        Assert.Same(target.FocusedGroup, target.TryGroupOf(tab));
        // The other window's close is not blocked by the abandoned claim.
        Assert.True(target.TryBeginClose(tab));
    }

    [Fact]
    public void TabsTheWindowDoesNotOwnCannotBeClaimed()
    {
        var window = Window();
        var other = Window().Connect(new Session { Id = Guid.NewGuid() });
        Assert.False(window.TryBeginClose(other));
        Assert.False(window.IsClosing(other));
    }

    [Fact]
    public void ClosingTheWindowLeavesNoOwnedTabs()
    {
        var window = Window();
        var tab = window.Connect(new Session { Id = Guid.NewGuid() });
        window.CloseAllTabs();
        Assert.False(window.Owns(tab));
    }

    private sealed class Credentials : ICredentialService
    {
        public string? Read(Guid sessionId) => null;
        public void Write(Guid sessionId, string secret) { }
        public void Delete(Guid sessionId) { }
    }
}
