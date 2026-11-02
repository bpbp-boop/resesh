using Resesh.App.ViewModels;
using Resesh.Core.Models;

namespace Resesh.AppLogic.Tests;

/// <summary>Moves a test tab into a state through the same transitions the terminal view uses.</summary>
internal static class TabStates
{
    public static TabViewModel MoveTo(this TabViewModel tab, TabConnectionState state, int? exitCode = null)
    {
        switch (state)
        {
            case TabConnectionState.Connecting: tab.BeginConnecting(); break;
            case TabConnectionState.Connected: tab.MarkConnected(); break;
            case TabConnectionState.Disconnected: tab.MarkDisconnected(); break;
            case TabConnectionState.Exited: tab.MarkExited(exitCode); break;
            case TabConnectionState.Playback: tab.StartPlayback("demo.cast"); break;
            default: throw new ArgumentOutOfRangeException(nameof(state));
        }
        Assert.Equal(state, tab.State);
        return tab;
    }
}

public sealed class TabStateTests
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

    private static TabViewModel Local() =>
        new(new Session { Name = "shell", Kind = SessionKind.Local, Local = new LocalTarget() }, Environment);

    [Fact]
    public void NewSessionTabStartsConnectingAndWalksTheLifecycle()
    {
        var tab = Local();
        Assert.Equal(TabConnectionState.Connecting, tab.State);
        Assert.False(tab.IsInert);

        Assert.True(tab.MarkConnected());
        Assert.Equal("running", tab.StateText);
        Assert.True(tab.MarkExited(3));
        Assert.Equal("exited (code 3)", tab.StateText);
        Assert.True(tab.BeginConnecting());
        Assert.Null(tab.ExitCode);
        Assert.True(tab.MarkDisconnected());
        Assert.Equal("failed", tab.StateText);
    }

    [Fact]
    public void ExitCodeChangesInTheSameStepAsTheState()
    {
        var tab = Local().MoveTo(TabConnectionState.Connected);
        var seen = new List<string>();
        tab.PropertyChanged += (_, args) =>
        {
            // Whatever a listener reads on any notification, state and code already agree.
            if (args.PropertyName is nameof(TabViewModel.State) or nameof(TabViewModel.ExitCode) or nameof(TabViewModel.StateText))
                seen.Add($"{args.PropertyName}:{tab.State}:{tab.ExitCode}:{tab.StateText}");
        };

        tab.MarkExited(7);

        Assert.All(seen, entry => Assert.EndsWith(":Exited:7:exited (code 7)", entry));
        Assert.Single(seen, entry => entry.StartsWith("State:"));
        Assert.Single(seen, entry => entry.StartsWith("ExitCode:"));

        seen.Clear();
        tab.MarkConnected();
        Assert.All(seen, entry => Assert.EndsWith(":Connected::running", entry));
    }

    [Fact]
    public void ExitedAgainWithANewCodeUpdatesTheText()
    {
        var tab = Local().MoveTo(TabConnectionState.Exited, 1);
        var changes = new List<string?>();
        tab.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        tab.MarkExited(null);

        Assert.Equal("exited", tab.StateText);
        Assert.DoesNotContain(nameof(TabViewModel.State), changes);
        Assert.Contains(nameof(TabViewModel.ExitCode), changes);
        Assert.Contains(nameof(TabViewModel.StateText), changes);
    }

    [Fact]
    public void RepeatingTheSameStateRaisesNothing()
    {
        var tab = Local().MoveTo(TabConnectionState.Disconnected);
        var changes = new List<string?>();
        tab.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        Assert.True(tab.MarkDisconnected());

        Assert.Empty(changes);
    }

    [Fact]
    public void PlaybackSetsPathAndStateTogetherAndNeverConnects()
    {
        var tab = Local();
        tab.StartPlayback(@"C:\casts\demo.cast");

        Assert.True(tab.IsPlayback);
        Assert.True(tab.IsInert);
        Assert.Equal(TabConnectionState.Playback, tab.State);
        Assert.Equal("demo.cast", tab.Subtitle);
        Assert.Equal("recording playback", tab.StateText);

        Assert.False(tab.BeginConnecting());
        Assert.False(tab.MarkConnected());
        Assert.False(tab.MarkDisconnected());
        Assert.False(tab.MarkExited(0));
        Assert.Equal(TabConnectionState.Playback, tab.State);
        Assert.Throws<InvalidOperationException>(() => tab.StartPlayback("other.cast"));
    }

    [Fact]
    public void OnlyANewTabCanBecomePlayback()
    {
        var tab = Local().MoveTo(TabConnectionState.Connected);

        Assert.Throws<InvalidOperationException>(() => tab.StartPlayback("demo.cast"));
        Assert.False(tab.IsPlayback);
        Assert.Equal(TabConnectionState.Connected, tab.State);
    }

    [Fact]
    public void AppPageIsItsOwnFinalStateAndIsNotPlayback()
    {
        var settings = TabViewModel.CreateAppPage(AppPage.Settings, Environment);

        Assert.Equal(TabConnectionState.Page, settings.State);
        Assert.True(settings.IsInert);
        Assert.False(settings.IsPlayback);
        Assert.Equal("changes apply immediately", settings.StateText);

        Assert.False(settings.BeginConnecting());
        Assert.False(settings.MarkDisconnected());
        Assert.False(settings.MarkExited(1));
        Assert.Equal(TabConnectionState.Page, settings.State);
        Assert.Null(settings.ExitCode);
        Assert.Throws<InvalidOperationException>(() => settings.StartPlayback("demo.cast"));
    }

    [Fact]
    public void LeavingConnectedClearsTheLastProgramTitle()
    {
        var tab = Local().MoveTo(TabConnectionState.Connected);
        tab.ApplyRunningCommand("htop");
        Assert.NotNull(tab.RunningCommand);

        tab.MarkExited(0);

        Assert.Null(tab.RunningCommand);
    }
}
