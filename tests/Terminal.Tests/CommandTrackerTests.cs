using Resesh.Terminal;
using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

/// <summary>The ghostty surface's port of addon-ruler.js command marks, over a fake buffer.</summary>
public class CommandTrackerTests
{
    private sealed class Harness
    {
        public readonly FakeBuffer Buffer = new();
        public readonly CommandTracker Tracker;
        public readonly List<(TimeSpan Delay, Action Action)> Timers = [];
        public readonly List<(string Text, bool Exact)> Running = [];
        public readonly List<TerminalCommandExecution> Executions = [];
        public readonly List<TerminalCommandRecord> Records = [];
        public readonly List<(string Context, string? Platform)> Contexts = [];
        public readonly List<string> Marked = [];
        public long Now = 1_000_000;

        public Harness()
        {
            Tracker = new CommandTracker(Buffer, () => Now)
            {
                Schedule = (delay, action) => Timers.Add((delay, action)),
            };
            Tracker.RunningCommand += (text, exact) => Running.Add((text, exact));
            Tracker.CommandExecution += e => Executions.Add(e);
            Tracker.CommandRecorded += r => Records.Add(r);
            Tracker.PromptContext += (c, p) => Contexts.Add((c, p));
            Tracker.CommandMarked += c => Marked.Add(c);
        }

        public ICommandMarker MarkerAtCursor() => Buffer.CreateMarker(Buffer.CursorLine)!;

        public void Semantic(int kind, int? exit = null, string command = "", int promptKind = 0) =>
            Tracker.OnSemanticPrompt(kind, promptKind, exit, MarkerAtCursor(), Buffer.CursorX, command);

        public void RunTimers()
        {
            while (Timers.Count > 0)
            {
                var (_, action) = Timers[0];
                Timers.RemoveAt(0);
                action();
            }
        }
    }

    [Fact]
    public void Osc133LifecycleMarksCommandReportsExecutionAndRecordsHistory()
    {
        var h = new Harness();
        h.Tracker.SetExecutionReporting(true);
        h.Tracker.SetHistoryCapture(true);
        h.Buffer.Add("user@host:~$ ls -la");
        h.Semantic(1);                       // A
        h.Buffer.CursorX = 13;
        h.Semantic(2);                       // B at the input start
        h.Buffer.Add("total 8");
        h.Buffer.Add("drwx------ 2 user user 4096 .");
        h.Semantic(3);                       // C
        Assert.Equal(("ls -la", true), h.Running[^1]);
        Assert.Single(h.Executions);
        Assert.Equal(new TerminalCommandExecution(1, "ls -la", false, null), h.Executions[0]);

        h.Buffer.Add("user@host:~$ ");
        h.Semantic(4, exit: 2);              // D;2
        Assert.Equal(new TerminalCommandExecution(1, "ls -la", true, 2), h.Executions[^1]);
        Assert.Equal(("", true), h.Running[^1]);

        var mark = Assert.Single(h.Tracker.Commands());
        Assert.Equal(0, mark.Line);
        Assert.Equal(2, mark.Exit);
        Assert.True(mark.Exact);
        Assert.Equal("ls -la", mark.Text);
        Assert.Equal(["ls -la"], h.Marked);

        var record = Assert.Single(h.Records);
        Assert.Equal("ls -la", record.CommandLine);
        Assert.Equal(2, record.ExitCode);
        Assert.True(record.Exact);
        Assert.Equal("total 8\ndrwx------ 2 user user 4096 .", record.Output);
        Assert.Equal("~", record.Directory);
    }

    [Fact]
    public void CommandLineFromShellWinsOverScreenText()
    {
        var h = new Harness();
        h.Buffer.Add("❯ git status");
        h.Semantic(1);
        h.Buffer.CursorX = 2;
        h.Semantic(2);
        h.Semantic(3, command: "git status --short");
        Assert.Equal(("git status --short", true), h.Running[^1]);
        Assert.Equal("git status --short", h.Tracker.Commands()[0].Text);
    }

    [Fact]
    public void SecondaryPromptStartDoesNotMoveThePromptLine()
    {
        var h = new Harness();
        h.Buffer.Add("user@host:~$ for i in 1 2");
        h.Semantic(1);
        h.Buffer.CursorX = 13;
        h.Semantic(2);
        h.Buffer.Add("> do echo $i; done");
        h.Semantic(1, promptKind: 3);        // PS2 continuation
        h.Semantic(3);
        Assert.Equal(0, h.Tracker.Commands()[0].Line);
    }

    [Fact]
    public void ShellWithoutOutputStartStillMarksOnCommandEnd()
    {
        var h = new Harness();
        h.Buffer.Add("[root@server ~]# uptime");
        h.Semantic(1);
        h.Buffer.Add(" 10:00 up 3 days");
        h.Semantic(4, exit: 0);
        var mark = Assert.Single(h.Tracker.Commands());
        Assert.Equal(0, mark.Exit);
        Assert.Equal("uptime", mark.Text);
    }

    [Fact]
    public void EnterOnPromptShapedLineBecomesDiscoveredMark()
    {
        var h = new Harness();
        h.Buffer.Add("admin@router:~$ show version");
        h.Tracker.NotifyEnter();
        Assert.Equal(("show version", false), h.Running[^1]); // reported before the echo settles
        Assert.Empty(h.Tracker.Commands());
        h.RunTimers();
        var mark = Assert.Single(h.Tracker.Commands());
        Assert.False(mark.Exact);
        Assert.Null(mark.Exit);
        Assert.Equal("show version", mark.Text);
    }

    [Fact]
    public void DiscoveryDropsAnAnchorThatMovedToAnotherCommand()
    {
        // A full-screen program's redraw moved the anchored line before the echo settled:
        // the line now shows an old command, which must not become this Enter's mark.
        var h = new Harness();
        h.Buffer.Add("root@rct-keep:~# ls -l");
        h.Buffer.Add("total 24");
        var line = h.Buffer.Add("root@rct-keep:/srv/rct-keep# codex");
        h.Tracker.NotifyEnter();
        h.Buffer.Lines[line] = ("root@rct-keep:~# ls -l", false);
        h.RunTimers();
        Assert.Empty(h.Tracker.Commands());
    }

    [Fact]
    public void DiscoveryAcceptsAnEchoThatGrewAfterEnter()
    {
        var h = new Harness();
        var line = h.Buffer.Add("admin@router:~$ cod");
        h.Tracker.NotifyEnter();
        h.Buffer.Lines[line] = ("admin@router:~$ codex", false);
        h.RunTimers();
        Assert.Equal("codex", Assert.Single(h.Tracker.Commands()).Text);
    }

    [Fact]
    public void DiscoveryWaitsForLateEchoAndRetriesOnce()
    {
        var h = new Harness();
        var line = h.Buffer.Add("user@host:~$ ");
        h.Tracker.NotifyEnter();
        Assert.Empty(h.Running);
        Assert.Single(h.Timers);
        var settle = h.Timers[0];
        h.Timers.Clear();
        settle.Action();                      // still no echo: one retry
        Assert.Equal(TimeSpan.FromMilliseconds(900), h.Timers[0].Delay);
        h.Buffer.Lines[line] = ("user@host:~$ sleep 5", false);
        h.RunTimers();
        Assert.Equal(("sleep 5", false), h.Running[^1]);
        Assert.Single(h.Tracker.Commands());
    }

    [Fact]
    public void TitleAfterEnterDropsDiscoveredRunningCommand()
    {
        var h = new Harness();
        var line = h.Buffer.Add("user@host:~$ ");
        h.Tracker.NotifyEnter();
        h.Tracker.NoteTitleChanged();        // the prompt's own title: the command already ended
        h.Buffer.Lines[line] = ("user@host:~$ true", false);
        h.RunTimers();
        Assert.Empty(h.Running);
        Assert.Single(h.Tracker.Commands()); // the mark is still made
    }

    [Fact]
    public void OutputThatLooksLikePromptIsNotMarkedWithoutEnter()
    {
        var h = new Harness();
        h.Buffer.Add("root@db:/# echo looks like a prompt");
        h.Tracker.OnOutputParsed();
        h.RunTimers();
        Assert.Empty(h.Tracker.Commands());
    }

    [Fact]
    public void Osc3008AttachesExitToDiscoveredMark()
    {
        var h = new Harness();
        h.Buffer.Add("user@host:~$ false");
        h.Tracker.NotifyEnter();
        h.Tracker.OnOsc3008("start=cmd\\x3b1;type=command;cwd=/home");
        h.RunTimers();
        h.Tracker.OnOsc3008("end=cmd\\x3b1;exit=failure;status=1");
        Assert.Equal(1, h.Tracker.Commands()[0].Exit);
    }

    [Fact]
    public void ShellIntegrationDisablesDiscovery()
    {
        var h = new Harness();
        h.Buffer.Add("user@host:~$ ");
        h.Semantic(1);
        h.Buffer.Lines[0] = ("user@host:~$ make", false);
        h.Tracker.NotifyEnter();
        h.RunTimers();
        Assert.Empty(h.Tracker.Commands());
        Assert.True(h.Tracker.ShellIntegrationSeen);
    }

    [Fact]
    public void CommandOutputIsTranscriptUpToNextMark()
    {
        var h = new Harness();
        h.Buffer.Add("user@host:~$ cat notes.txt");
        h.Tracker.NotifyEnter();
        h.RunTimers();
        h.Buffer.Add("first line that is long and");
        h.Buffer.Add(" wraps here", wrapped: true);
        h.Buffer.Add("second");
        h.Buffer.Add("user@host:~$ ");           // empty Enter at a prompt
        h.Buffer.Add("user@host:~$ ls");
        h.Tracker.NotifyEnter();
        h.RunTimers();
        h.Buffer.Add("a.txt");
        h.Buffer.Add("user@host:~$ ");           // live idle prompt

        Assert.Equal("user@host:~$ cat notes.txt\nfirst line that is long and wraps here\nsecond",
            h.Tracker.CommandOutput(0));
        Assert.Equal("user@host:~$ ls\na.txt", h.Tracker.CommandOutput(5));
    }

    [Fact]
    public void CommandWithNoOutputHasNoTranscript()
    {
        var h = new Harness();
        h.Buffer.Add("user@host:~$ true");
        h.Tracker.NotifyEnter();
        h.RunTimers();
        h.Buffer.Add("user@host:~$ ");
        Assert.Equal("", h.Tracker.CommandOutput(0));
    }

    [Fact]
    public void DiscoveredHistoryEndsAtIdlePrompt()
    {
        var h = new Harness();
        h.Tracker.SetHistoryCapture(true);
        h.Buffer.Add("PS C:\\work> dir");
        h.Tracker.NotifyEnter();
        h.Timers[0].Action();                // commit the mark (settle timer)
        h.Timers.RemoveAt(0);
        h.Buffer.Add("file.txt");
        h.Buffer.Add("PS C:\\work> ");
        h.Tracker.OnOutputParsed();
        h.RunTimers();                       // idle check sees the prompt
        var record = Assert.Single(h.Records);
        Assert.Equal("dir", record.CommandLine);
        Assert.False(record.Exact);
        Assert.Equal("file.txt", record.Output);
        Assert.Equal("C:\\work", record.Directory);
    }

    [Fact]
    public void TrimmedMarkIsRemovedAndRecordedAsLost()
    {
        var h = new Harness();
        h.Tracker.SetHistoryCapture(true);
        h.Buffer.Add("user@host:~$ yes");
        h.Semantic(1);
        h.Buffer.CursorX = 13;
        h.Semantic(2);
        h.Semantic(3);
        for (var i = 0; i < 5; i++)
            h.Buffer.Add("y");
        h.Buffer.Trimmed = 3;                // the prompt line left the scrollback
        h.Tracker.OnOutputParsed();
        Assert.Empty(h.Tracker.Commands());
        var record = Assert.Single(h.Records);
        Assert.True(record.OutputLost);
        Assert.Equal("yes", record.CommandLine);
    }

    [Fact]
    public void BookmarkTogglesOnCursorLine()
    {
        var h = new Harness();
        h.Buffer.Add("one");
        h.Buffer.Add("two");
        Assert.True(h.Tracker.ToggleBookmark());
        Assert.Equal([1], h.Tracker.BookmarkLines());
        Assert.False(h.Tracker.ToggleBookmark());
        Assert.Empty(h.Tracker.BookmarkLines());
    }

    [Fact]
    public void JumpTargetsNearestMarkAroundViewportCenter()
    {
        var h = new Harness();
        foreach (var line in new[] { 2, 20, 40 })
        {
            while (h.Buffer.Length < line)
                h.Buffer.Add("out");
            h.Buffer.Add($"user@host:~$ cmd{line}");
            h.Tracker.NotifyEnter();
            h.RunTimers();
        }
        h.Buffer.ViewportTop = 15;           // center = 20
        Assert.Equal(40, h.Tracker.JumpTarget(1));
        Assert.Equal(2, h.Tracker.JumpTarget(-1));
    }

    [Theory]
    [InlineData("PS C:\\Users\\Boden> ", "C:\\Users\\Boden", null)]
    [InlineData("C:\\Windows\\System32>", "C:\\Windows\\System32", null)]
    [InlineData("[root@server /var/log]#", "/var/log", null)]
    [InlineData("user@host ~/work $", "~/work", null)]
    [InlineData("edge1(config-if)#", "configure · interface", null)]
    [InlineData("RP/0/RSP0/CPU0:xr1(config)#", "RP/0/RSP0/CPU0 · configure", "cisco")]
    public void PromptContextLabels(string prompt, string label, string? platform)
    {
        var h = new Harness();
        h.Buffer.Add(prompt);
        Assert.Equal((label, platform), h.Tracker.ReportPromptContext(force: true));
    }

    [Fact]
    public void BareHashPromptNeedsCiscoEvidence()
    {
        var h = new Harness();
        h.Buffer.Add("server#");
        Assert.Null(h.Tracker.ReportPromptContext(force: true));
        h.Tracker.SetPromptPlatform("cisco");
        Assert.Equal(("privileged EXEC", "cisco"), h.Tracker.ReportPromptContext(force: true));
    }

    [Fact]
    public void JunosOperationalNeedsBannerOrPlatform()
    {
        var h = new Harness();
        h.Buffer.Add("--- JUNOS 22.4R1 Kernel 64-bit");
        h.Buffer.Add("admin@mx1>");
        Assert.Equal(("operational", "juniper"), h.Tracker.ReportPromptContext(force: true));
    }

    [Fact]
    public void PromptContextIsReportedOncePerLine()
    {
        var h = new Harness();
        h.Buffer.Add("user@host ~ $");
        h.Tracker.OnOutputParsed();
        h.Tracker.OnOutputParsed();
        Assert.Single(h.Contexts);
        h.Buffer.Add("user@host ~ $");     // same context, new line: a command ended
        h.Tracker.OnOutputParsed();
        Assert.Equal(2, h.Contexts.Count);
    }

    [Theory]
    [InlineData("start=abc;type=command", "start", "abc", "command", null, null)]
    [InlineData("end=a\\x3bb;exit=success", "end", "a;b", null, "success", null)]
    [InlineData("end=x;status=137", "end", "x", null, null, 137)]
    public void ParsesOsc3008(string data, string action, string id, string? type, string? exit, int? status)
    {
        var parsed = CommandTracker.ParseOsc3008(data);
        Assert.Equal(new CommandTracker.Osc3008(action, id, type, exit, status), parsed);
    }

    [Theory]
    [InlineData("start=")]
    [InlineData("begin=x")]
    [InlineData("start=bad\\escape")]
    public void RejectsMalformedOsc3008(string data) => Assert.Null(CommandTracker.ParseOsc3008(data));
}
