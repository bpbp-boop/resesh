using System.Text.Json;
using Resesh.Core.Agents;

namespace Resesh.Core.Tests;

public class AgentAdaptersTests
{
    [Fact]
    public void CodexAdapterUsesOfficialLifecycleEventsForPreciseStates()
    {
        var adapter = AgentAdapters.Codex();
        using var document = JsonDocument.Parse(adapter.Text);
        var hooks = document.RootElement.GetProperty("hooks");

        AssertState(hooks, "SessionStart", "idle");
        AssertState(hooks, "UserPromptSubmit", "working");
        AssertState(hooks, "PermissionRequest", "needs-approval");
        AssertState(hooks, "PostToolUse", "working");
        AssertState(hooks, "Stop", "complete");
        AssertState(hooks, "SessionEnd", "exit");
    }

    [Fact]
    public void CodexAdapterReportsOnlyAndNeverMakesAnApprovalDecision()
    {
        var adapter = AgentAdapters.Codex();

        Assert.Contains("/dev/tty", adapter.Text, StringComparison.Ordinal);
        Assert.Contains("CONOUT$", adapter.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("permissionDecision", adapter.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"behavior\"", adapter.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_input", adapter.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodexAdapterIsTheFirstRecommendedAdapter()
    {
        Assert.StartsWith("Codex", AgentAdapters.All[0].Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClaudeHooksReturnAllowlistedTerminalSequenceJson(bool windows)
    {
        var adapter = windows ? AgentAdapters.ClaudeCodeWindows() : AgentAdapters.ClaudeCodePosix();
        using var settings = JsonDocument.Parse(adapter.Text);
        var hooks = settings.RootElement.GetProperty("hooks");
        var eventStates = new (string Event, string State)[]
        {
            ("SessionStart", "working"),
            ("UserPromptSubmit", "working"),
            ("Notification", "needs-approval"),
            ("Stop", "complete"),
            ("SessionEnd", "exit"),
        };
        Assert.Equal(eventStates.Length, hooks.EnumerateObject().Count());
        foreach (var (eventName, state) in eventStates)
        {
            var handler = hooks.GetProperty(eventName)[0].GetProperty("hooks")[0];
            Assert.Equal("command", handler.GetProperty("type").GetString());
            var command = handler.GetProperty("command").GetString()!;
            var prefix = windows ? "echo '" : "printf '%s\\n' '";
            if (!windows)
            {
                // Inside tmux the hook first leaves the state in the pane option. That
                // part prints nothing, so the JSON stays the hook's only output.
                var tmux = command[..command.IndexOf(prefix, StringComparison.Ordinal)];
                Assert.StartsWith("[ $TMUX_PANE ] && tmux set -p ", tmux, StringComparison.Ordinal);
                Assert.EndsWith(" >/dev/null 2>&1; ", tmux, StringComparison.Ordinal);
                Assert.DoesNotContain("'", tmux, StringComparison.Ordinal);
                command = command[tmux.Length..];
            }
            Assert.StartsWith(prefix, command, StringComparison.Ordinal);
            Assert.EndsWith("'", command, StringComparison.Ordinal);
            var json = command[prefix.Length..^1];
            // Both shells preserve a single-quoted argument verbatim. In particular,
            // printf's %s never interprets the JSON's backslash escapes as control bytes.
            Assert.DoesNotContain("'", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\x1b", command, StringComparison.Ordinal);
            Assert.DoesNotContain("\a", command, StringComparison.Ordinal);
            Assert.Contains("\\u001b", json, StringComparison.Ordinal);
            Assert.Contains("\\u0007", json, StringComparison.Ordinal);
            using var output = JsonDocument.Parse(json);
            Assert.Single(output.RootElement.EnumerateObject());
            var sequence = output.RootElement.GetProperty("terminalSequence").GetString();
            Assert.Equal($"\x1b]777;notify;resesh-agent;id=claude;state={state}\a", sequence);
            var parsed = AgentOsc.Parse(777, sequence![6..^1]);
            Assert.NotNull(parsed);
            Assert.Equal(AgentSource.Structured, parsed.Source);
            Assert.Equal("claude", parsed.Key);
            Assert.Equal(state == "exit", parsed.Ended);
        }
        Assert.DoesNotContain("/dev/tty", adapter.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("CONOUT$", adapter.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("EncodedCommand", adapter.Text, StringComparison.Ordinal);
        Assert.Contains("desktop notification", adapter.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("working", AgentAttention.Working)]
    [InlineData("needs-approval", AgentAttention.NeedsApproval)]
    [InlineData("needs-answer", AgentAttention.NeedsAnswer)]
    [InlineData("complete", AgentAttention.Complete)]
    [InlineData("failed", AgentAttention.Failed)]
    [InlineData("idle", AgentAttention.Idle)]
    public void TmuxReportsCarryTheSameStatesAsTheSequences(string state, AgentAttention expected)
    {
        var report = AgentAdapters.TmuxReport("claude", state);
        var status = ProgramStatus.ParseReport(report + ":n=4242");
        Assert.NotNull(status);
        Assert.Equal("claude", status.App);
        Assert.Equal(expected, status.Attention);
        Assert.Null(AgentAdapters.TmuxReport("claude", "exit"));
    }

    [Fact]
    public void PosixAdaptersUseThePaneOptionInsideTmux()
    {
        foreach (var adapter in new[] { AgentAdapters.Codex(), AgentAdapters.ClaudeCodePosix(), AgentAdapters.ShellFunction() })
        {
            Assert.Contains("TMUX_PANE", adapter.Text, StringComparison.Ordinal);
            Assert.Contains("tmux set -p ", adapter.Text, StringComparison.Ordinal);
            Assert.Contains("tmux set -p -u ", adapter.Text, StringComparison.Ordinal); // exit unsets it
            Assert.Contains(TmuxStatus.Option, adapter.Text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("tmux", AgentAdapters.ClaudeCodeWindows().Text, StringComparison.Ordinal);
    }

    private static void AssertState(JsonElement hooks, string eventName, string state)
    {
        var handler = hooks.GetProperty(eventName)[0].GetProperty("hooks")[0];
        var posix = handler.GetProperty("command").GetString();
        var windows = handler.GetProperty("commandWindows").GetString();

        Assert.Contains($"id=codex;state={state}", posix, StringComparison.Ordinal);
        Assert.Contains($"id=codex;state={state}", windows, StringComparison.Ordinal);
        Assert.Equal(3, handler.GetProperty("timeout").GetInt32());
    }
}
