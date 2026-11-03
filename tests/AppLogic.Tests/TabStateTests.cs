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

    private static TabViewModel Ssh() =>
        new(new Session { Name = "web", Host = "web.example", Username = "admin" }, Environment);

    [Theory]
    [InlineData(TabConnectionState.Disconnected)]
    [InlineData(TabConnectionState.Exited)]
    [InlineData(TabConnectionState.Connecting)]
    public void WhatDescribedAConnectionGoesWithIt(TabConnectionState next)
    {
        var tab = Ssh();
        tab.MarkConnected(summary: "aes256-gcm@openssh.com • SHA256:abc");
        tab.ConnectionDetails = "Cipher: aes256-gcm@openssh.com";
        Assert.Equal("aes256-gcm@openssh.com • SHA256:abc", tab.ConnectionSummary);

        tab.MoveTo(next);

        // No transport has to clear these; a reconnect over telnet must not show SSH's.
        Assert.Equal("", tab.ConnectionSummary);
        Assert.Equal("", tab.ConnectionDetails);
    }

    [Fact]
    public void ConnectingDescribesTheNewConnectionInTheSameStep()
    {
        var tab = Local();
        var summaryWhenConnected = (string?)null;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabViewModel.ConnectionSummary))
                summaryWhenConnected = tab.State == TabConnectionState.Connected ? tab.ConnectionSummary : null;
        };

        tab.MarkConnected(summary: "pid 42");

        Assert.Equal("pid 42", summaryWhenConnected);
    }

    [Theory]
    [InlineData(TabConnectionState.Connecting, false)]
    [InlineData(TabConnectionState.Connected, false)]
    [InlineData(TabConnectionState.Disconnected, true)]
    [InlineData(TabConnectionState.Exited, true)]
    [InlineData(TabConnectionState.Playback, false)]
    public void OnlyAnEndedConnectionIsStopped(TabConnectionState state, bool stopped) =>
        Assert.Equal(stopped, Local().MoveTo(state).IsStopped);

    [Fact]
    public void CommandsActOnlyOnALiveUnlockedConnection()
    {
        var tab = Ssh();
        Assert.False(tab.AcceptsCommands); // still connecting
        tab.MarkConnected();
        Assert.True(tab.AcceptsCommands);
        tab.Lock("secret");
        Assert.False(tab.AcceptsCommands);
        Assert.True(tab.TryUnlock("secret"));
        Assert.True(tab.AcceptsCommands);
        tab.MarkDisconnected();
        Assert.False(tab.AcceptsCommands);
    }
}
