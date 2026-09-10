using Resesh.App.ViewModels;
using Resesh.Core.Models;
using Resesh.Core.Sftp;

namespace Resesh.AppLogic.Tests;

public sealed class TmuxSubtitleTests
{
    private static TabViewModel Tab(bool persistent = true) => new(
        new Session { Name = "test", Host = "server", Persistent = persistent },
        new ViewModelEnvironment
        {
            CurrentTheme = () => "dark", ResolveTheme = theme => theme,
            ShowAgentIcons = () => true, IsSessionVisible = _ => true,
            ApplySessionSettings = _ => { }, ReportError = _ => { },
        });

    [Fact]
    public void ShellTitleDisplaysCurrentDirectoryAndStillEndsStaleCommand()
    {
        var tab = Tab();
        tab.ApplyPromptContext("/old");
        tab.ApplyRunningCommand("claude");
        tab.ApplyReportedWorkingDirectory(new("server", "/tmp"));
        Assert.Equal("claude", tab.Subtitle); // cwd cannot end a running command
        tab.ApplyTerminalTitle("bash");
        Assert.Null(tab.RunningCommand);
        Assert.Equal("bash", tab.TerminalTitle); // retain agent retirement evidence
        Assert.Equal("/tmp", tab.Subtitle);
        tab.ApplyReportedWorkingDirectory(new("server", "/etc"));
        Assert.Equal("/etc", tab.Subtitle); // no new title or prompt regex needed
    }

    [Fact]
    public void ReportedDirectoryNeverOverridesRunningCommandOrProgramTitle()
    {
        var tab = Tab();
        tab.ApplyTerminalTitle("bash");
        tab.ApplyRunningCommand("sleep 20");
        tab.ApplyReportedWorkingDirectory(new("server", "/tmp"));
        Assert.Equal("sleep", tab.Subtitle);
        tab.ApplyRunningCommand("");
        tab.ApplyTerminalTitle("Claude Code");
        Assert.Equal("Claude Code", tab.Subtitle);
    }

    [Fact]
    public void ShellWithoutOscFallsBackToPromptContextAndReconnectClearsOldCwd()
    {
        var tab = Tab();
        tab.State = TabConnectionState.Connected;
        tab.ApplyPromptContext("/from-prompt");
        tab.ApplyTerminalTitle("bash");
        Assert.Equal("/from-prompt", tab.Subtitle);
        tab.ApplyReportedWorkingDirectory(new("server", "/tmp"));
        tab.State = TabConnectionState.Disconnected;
        tab.ApplyTerminalTitle("bash");
        Assert.Equal("bash", tab.Subtitle);
    }

    [Fact]
    public void CurrentPromptUsesTildeWithoutGuessingTheRemoteHomeDirectory()
    {
        var tab = Tab();
        tab.ApplyTerminalTitle("bash");
        tab.ApplyReportedWorkingDirectory(new("server", "/custom/homes/boden/etc"));
        tab.ApplyPromptContext("~/etc");
        Assert.Equal("~/etc", tab.Subtitle);
        tab.ApplyReportedWorkingDirectory(new("server", "/custom/homes/boden/etc"));
        Assert.Equal("~/etc", tab.Subtitle);

        tab.ApplyReportedWorkingDirectory(new("server", "/etc"));
        Assert.Equal("/etc", tab.Subtitle); // never reuse the previous ~/etc label
        tab.ApplyPromptContext("/etc");
        Assert.Equal("/etc", tab.Subtitle);

        tab.ApplyReportedWorkingDirectory(new("server", "/custom/homes/boden"));
        tab.ApplyPromptContext("~");
        Assert.Equal("~", tab.Subtitle);
    }

    [Fact]
    public void ExactCommandSurvivesLateTmuxShellAndInterpreterTitlesUntilCompletion()
    {
        var tab = Tab();
        tab.State = TabConnectionState.Connected;
        tab.ApplyReportedWorkingDirectory(new("server", "/home/user/etc"));
        tab.ApplyPromptContext("~/etc");
        tab.ApplyRunningCommand("ansible-playbook site.yml", exact: true);
        tab.ApplyTerminalTitle("bash"); // delayed tmux poll from before command start
        tab.ApplyTerminalTitle("python3");
        Assert.Equal("ansible-playbook", tab.Subtitle);
        tab.ApplyPromptContext("~/etc"); // a redraw is not exact completion
        Assert.Equal("ansible-playbook", tab.Subtitle);
        tab.ApplyRunningCommand("", exact: true);
        Assert.Equal("~/etc", tab.Subtitle);

        tab.ApplyRunningCommand("ansible-playbook next.yml", exact: true);
        tab.State = TabConnectionState.Disconnected;
        tab.ApplyRunningCommand("sleep 1"); // fresh connection using discovery
        tab.ApplyTerminalTitle("bash");
        Assert.Null(tab.RunningCommand);
    }

    [Fact]
    public void PlainSshTitlePriorityDoesNotChange()
    {
        var tab = Tab(persistent: false);
        tab.ApplyTerminalTitle("bash");
        tab.ApplyReportedWorkingDirectory(new("server", "/tmp"));
        Assert.Equal("bash", tab.Subtitle);
    }
}
