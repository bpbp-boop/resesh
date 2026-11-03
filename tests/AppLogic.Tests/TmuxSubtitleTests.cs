using Resesh.App.ViewModels;
using Resesh.Core.Models;
using Resesh.Core.Sftp;

namespace Resesh.AppLogic.Tests;

public sealed class TmuxSubtitleTests
{
    /// <summary>A tab whose connection is attached to persistent shell 0, or a plain one.</summary>
    private static TabViewModel Tab(bool persistent = true)
    {
        var tab = new TabViewModel(
            new Session { Name = "test", Host = "server", Persistent = persistent },
            new ViewModelEnvironment
            {
                CurrentTheme = () => "dark", ResolveTheme = theme => theme,
                ShowAgentIcons = () => true, IsSessionVisible = _ => true,
                ApplySessionSettings = _ => { }, ReportError = _ => { },
            });
        tab.MarkConnected(remoteSessionSlot: persistent ? 0 : null);
        return tab;
    }

    [Fact]
    public void ShellTitleDisplaysCurrentDirectoryAndStillEndsStaleCommand()
    {
        var tab = Tab();
        tab.ApplyPromptContext("/old");
        tab.ApplyRunningCommand("claude");
        tab.ApplyReportedWorkingDirectory("/tmp");
        Assert.Equal("claude", tab.Subtitle); // cwd cannot end a running command
        tab.ApplyTerminalTitle("bash");
        Assert.Null(tab.RunningCommand);
        Assert.Equal("bash", tab.TerminalTitle); // retain agent retirement evidence
        Assert.Equal("/tmp", tab.Subtitle);
        tab.ApplyReportedWorkingDirectory("/etc");
        Assert.Equal("/etc", tab.Subtitle); // no new title or prompt regex needed
    }

    [Fact]
    public void ReportedDirectoryNeverOverridesRunningCommandOrProgramTitle()
    {
        var tab = Tab();
        tab.ApplyTerminalTitle("bash");
        tab.ApplyRunningCommand("sleep 20");
        tab.ApplyReportedWorkingDirectory("/tmp");
        Assert.Equal("sleep", tab.Subtitle);
        tab.ApplyRunningCommand("");
        tab.ApplyTerminalTitle("Claude Code");
        Assert.Equal("Claude Code", tab.Subtitle);
    }

    [Fact]
    public void ShellWithoutOscFallsBackToPromptContextAndReconnectClearsOldCwd()
    {
        var tab = Tab();
        tab.MarkConnected(remoteSessionSlot: 0);
        tab.ApplyPromptContext("/from-prompt");
        tab.ApplyTerminalTitle("bash");
        Assert.Equal("/from-prompt", tab.Subtitle);
        tab.ApplyReportedWorkingDirectory("/tmp");
        tab.MarkDisconnected();
        tab.ApplyTerminalTitle("bash");
        Assert.Equal("bash", tab.Subtitle);
    }

    [Fact]
    public void CurrentPromptUsesTildeWithoutGuessingTheRemoteHomeDirectory()
    {
        var tab = Tab();
        tab.ApplyTerminalTitle("bash");
        tab.ApplyReportedWorkingDirectory("/custom/homes/boden/etc");
        tab.ApplyPromptContext("~/etc");
        Assert.Equal("~/etc", tab.Subtitle);
        tab.ApplyReportedWorkingDirectory("/custom/homes/boden/etc");
        Assert.Equal("~/etc", tab.Subtitle);

        tab.ApplyReportedWorkingDirectory("/etc");
        Assert.Equal("/etc", tab.Subtitle); // never reuse the previous ~/etc label
        tab.ApplyPromptContext("/etc");
        Assert.Equal("/etc", tab.Subtitle);

        tab.ApplyReportedWorkingDirectory("/custom/homes/boden");
        tab.ApplyPromptContext("~");
        Assert.Equal("~", tab.Subtitle);
    }

    [Fact]
    public void ExactCommandSurvivesLateTmuxShellAndInterpreterTitlesUntilCompletion()
    {
        var tab = Tab();
        tab.MarkConnected(remoteSessionSlot: 0);
        tab.ApplyReportedWorkingDirectory("/home/user/etc");
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
        tab.MarkDisconnected();
        tab.ApplyRunningCommand("sleep 1"); // fresh connection using discovery
        tab.ApplyTerminalTitle("bash");
        Assert.Null(tab.RunningCommand);
    }

    [Fact]
    public void AgentTitleReplacesCommandNameWithoutItsSpinner()
    {
        var tab = Tab();
        tab.MarkConnected(remoteSessionSlot: 0);
        tab.ApplyTerminalTitle("bash");
        tab.ApplyRunningCommand("claude --resume", exact: true);
        Assert.Equal("claude", tab.Subtitle);
        tab.ApplyTerminalTitle("✳ Claude Code");
        Assert.Equal("Claude Code", tab.Subtitle);
        tab.ApplyTerminalTitle("⠐ Fix login bug");
        Assert.Equal("Fix login bug", tab.Subtitle);
        tab.ApplyTerminalTitle("✳ Fix login bug");
        Assert.Equal("Fix login bug", tab.Subtitle);
        tab.ApplyRunningCommand("", exact: true);
        tab.ApplyTerminalTitle("bash");
        tab.ApplyReportedWorkingDirectory("/srv");
        Assert.Equal("/srv", tab.Subtitle);
    }

    [Fact]
    public void StickyTmuxTitleFromEarlierProgramDoesNotReplaceNewCommand()
    {
        var tab = Tab();
        tab.MarkConnected(remoteSessionSlot: 0);
        tab.ApplyRunningCommand("claude", exact: true);
        tab.ApplyTerminalTitle("✳ Fix login bug");
        tab.ApplyRunningCommand("", exact: true);
        tab.ApplyTerminalTitle("bash");

        tab.ApplyRunningCommand("vim notes.txt", exact: true);
        tab.ApplyTerminalTitle("⠂ Fix login bug"); // tmux's pane title outlived claude
        Assert.Equal("vim", tab.Subtitle);
        tab.ApplyTerminalTitle("python3"); // a bare process name is tmux's fallback
        Assert.Equal("vim", tab.Subtitle);
        tab.ApplyTerminalTitle("notes.txt - VIM");
        Assert.Equal("notes.txt - VIM", tab.Subtitle);
    }

    [Fact]
    public void PlainSshTitleDropsAgentSpinner()
    {
        var tab = Tab(persistent: false);
        tab.ApplyTerminalTitle("✳ Fix login bug");
        Assert.Equal("Fix login bug", tab.Subtitle);
    }

    [Fact]
    public void PlainSshTitlePriorityDoesNotChange()
    {
        var tab = Tab(persistent: false);
        tab.ApplyTerminalTitle("bash");
        tab.ApplyReportedWorkingDirectory("/tmp");
        Assert.Equal("bash", tab.Subtitle);
    }

    [Fact]
    public void TmuxRulesFollowTheAttachedConnectionNotTheSetting()
    {
        // Persistence switched on after a plain connect: the shell is still a plain one.
        var plain = Tab(persistent: false);
        plain.Session = plain.Session with { Persistent = true };
        plain.ApplyTerminalTitle("bash");
        plain.ApplyReportedWorkingDirectory("/tmp");
        Assert.Equal("bash", plain.Subtitle);

        // Switched off while attached: the tmux shell is still there.
        var attached = Tab();
        attached.Session = attached.Session with { Persistent = false };
        attached.ApplyTerminalTitle("bash");
        attached.ApplyReportedWorkingDirectory("/srv");
        Assert.Equal("/srv", attached.Subtitle);
    }

    [Fact]
    public void WithdrawnReportKeepsThePromptThatContradictedIt()
    {
        var tab = Tab();
        tab.ApplyTerminalTitle("bash");
        tab.ApplyReportedWorkingDirectory("/home/user");
        tab.ApplyPromptContext("/opt");
        tab.ApplyReportedWorkingDirectory(null); // the tracker found the report stale
        Assert.Equal("/opt", tab.Subtitle);
    }
}
