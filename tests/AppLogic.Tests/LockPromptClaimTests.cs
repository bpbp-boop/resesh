using Resesh.App.ViewModels;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

/// <summary>One lock or unlock prompt per tab, so a second queued lock can't replace the
/// password the first one set.</summary>
public sealed class LockPromptClaimTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("resesh-lock-tests-").FullName;

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
    public void SecondPromptForTheSameTabIsRefusedUntilTheFirstEnds()
    {
        var window = Window();
        var tab = window.Connect(new Session { Id = Guid.NewGuid() });

        Assert.True(window.TryBeginLockPrompt(tab));
        Assert.True(window.IsLockPromptOpen(tab));
        Assert.False(window.TryBeginLockPrompt(tab));

        window.EndLockPrompt(tab);
        Assert.False(window.IsLockPromptOpen(tab));
        Assert.True(window.TryBeginLockPrompt(tab));
    }

    [Fact]
    public void PromptsAndClosesClaimSeparately()
    {
        var window = Window();
        var tab = window.Connect(new Session { Id = Guid.NewGuid() });
        Assert.True(window.TryBeginLockPrompt(tab));
        // Closing a tab whose lock prompt is open is still allowed.
        Assert.True(window.TryBeginClose(tab));
    }

    [Fact]
    public void TabMovedAwayCanBePromptedInItsNewWindow()
    {
        var source = Window();
        var target = Window();
        var tab = source.Connect(new Session { Id = Guid.NewGuid() });
        Assert.True(source.TryBeginLockPrompt(tab));

        source.DetachTab(tab);
        target.AttachTab(tab, target.FocusedGroup, 0);

        Assert.False(source.Owns(tab));
        Assert.True(target.TryBeginLockPrompt(tab));
        Assert.False(Window().TryBeginLockPrompt(tab));
    }

    [Fact]
    public void SecondLockReplacesThePasswordSoOnlyOnePromptMayRun()
    {
        // The reason for the claim: TabViewModel.Lock does not refuse a locked tab.
        var tab = Window().Connect(new Session { Id = Guid.NewGuid() });
        tab.Lock("first");
        tab.Lock("second");
        Assert.False(tab.TryUnlock("first"));
        Assert.True(tab.TryUnlock("second"));
    }

    private sealed class Credentials : ICredentialService
    {
        public string? Read(Guid sessionId) => null;
        public void Write(Guid sessionId, string secret) { }
        public void Delete(Guid sessionId) { }
    }
}
