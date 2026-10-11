using System.Text.Encodings.Web;
using System.Text.Json;

namespace Resesh.Core.Agents;

/// <summary>An adapter snippet the user can install on a target to upgrade a tab from
/// guessed identity to reported lifecycle events.</summary>
public sealed record AgentAdapterSnippet(string Title, string Target, string Description, string Text);

/// <summary>
/// Text for the opt-in adapters. resesh never installs these itself: the tab menu shows
/// the exact text, the user copies it to the target they choose, and removing it is
/// deleting the lines again. Nothing here can send input to a session — an adapter's only
/// power is to emit one escape sequence describing what the agent is doing.
/// </summary>
public static class AgentAdapters
{
    /// <summary>The escape sequence itself, documented once for anyone writing their own
    /// adapter: <c>ESC ] 7377 ; agent ; id=… ; state=… ; label=… BEL</c>.</summary>
    public const string SequenceReference =
        "ESC ] 7377 ; agent ; id=<agent> ; state=<state> ; label=<text> BEL\n" +
        "states: working | needs-approval | needs-answer | complete | failed | idle | exit\n" +
        "values are percent-encoded; label is optional and shown only in the tab tooltip\n\n" +
        "Claude Code hooks return stdout JSON with terminalSequence containing:\n" +
        "ESC ] 777 ; notify ; resesh-agent ; id=<agent> ; state=<state> ; label=<text> BEL\n" +
        "The exact title resesh-agent is reserved for structured status. Other terminals may show it as a desktop notification.";

    public static IReadOnlyList<AgentAdapterSnippet> All =>
    [
        Codex(),
        ClaudeCodePosix(),
        ClaudeCodeWindows(),
        ShellFunction(),
    ];

    /// <summary>Codex lifecycle hooks. One hooks.json works on Windows and POSIX targets:
    /// Codex selects commandWindows on Windows and command everywhere else. Commands drain
    /// the hook JSON from stdin, write only to the controlling terminal, and return no hook
    /// output, so they can report state without changing an approval or agent decision.</summary>
    public static AgentAdapterSnippet Codex() => new(
        "Codex",
        "~/.codex/hooks.json · Windows, Linux, macOS",
        "Adds exact lifecycle states from Codex hooks. After you add the file, review and trust it with /hooks.",
        CodexHooksJson());

    private static string CodexHooksJson()
    {
        var eventStates = new (string Event, string State)[]
        {
            ("SessionStart", "idle"),
            ("UserPromptSubmit", "working"),
            ("PermissionRequest", "needs-approval"),
            ("PostToolUse", "working"),
            ("Stop", "complete"),
            ("SessionEnd", "exit"),
        };

        var hooks = new Dictionary<string, object>();
        foreach (var (eventName, state) in eventStates)
        {
            hooks[eventName] = new[]
            {
                new
                {
                    hooks = new[]
                    {
                        new
                        {
                            type = "command",
                            command = CodexPosixCommand(state),
                            commandWindows = CodexWindowsCommand(state),
                            timeout = 3,
                        },
                    },
                },
            };
        }

        return JsonSerializer.Serialize(
            new
            {
                description = "resesh tab status for Codex. Reports state only; never approves or sends input.",
                hooks,
            },
            // Relaxed escaping so the quotes inside the commands stay readable (\" instead
            // of ") — this text is copied into a config file the user should review.
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static string CodexPosixCommand(string state) =>
        $"sh -c \"cat >/dev/null; printf '\\033]7377;agent;id=codex;state={state}\\007' > /dev/tty\"";

    private static string CodexWindowsCommand(string state) =>
        $"powershell -NoProfile -NonInteractive -Command \"$input | Out-Null; "
        + "$s=[IO.File]::OpenWrite('\\\\.\\CONOUT$'); "
        + $"$b=[Text.Encoding]::UTF8.GetBytes([char]27+']7377;agent;id=codex;state={state}'+[char]7); "
        + "$s.Write($b,0,$b.Length); $s.Dispose()\"";

    /// <summary>Claude Code emits terminalSequence from hook stdout; hooks themselves
    /// have no controlling terminal. printf %s preserves the JSON's Unicode escapes.</summary>
    public static AgentAdapterSnippet ClaudeCodePosix() => new(
        "Claude Code",
        "~/.claude/settings.json · Linux, macOS, WSL",
        "Reports working, approval, finished, and exit states via Claude Code's terminalSequence JSON output. " +
        "Interactive sessions only. In another terminal, the reserved resesh-agent title may appear as a desktop notification.",
        ClaudeCodeHooksJson(windows: false));

    /// <summary>The single-quoted echo argument preserves JSON escapes in both Git Bash
    /// and PowerShell, without a console device or a nested PowerShell command.</summary>
    public static AgentAdapterSnippet ClaudeCodeWindows() => new(
        "Claude Code",
        "%USERPROFILE%\\.claude\\settings.json · Windows",
        "Reports the same lifecycle states via Claude Code's terminalSequence JSON output (Git Bash or PowerShell). " +
        "Interactive sessions only. In another terminal, the reserved resesh-agent title may appear as a desktop notification.",
        ClaudeCodeHooksJson(windows: true));

    private static string ClaudeCodeHooksJson(bool windows)
    {
        var eventStates = new (string Event, string State)[]
        {
            ("SessionStart", "working"),
            ("UserPromptSubmit", "working"),
            ("Notification", "needs-approval"),
            ("Stop", "complete"),
            ("SessionEnd", "exit"),
        };
        var hooks = new Dictionary<string, object>();
        foreach (var (eventName, state) in eventStates)
        {
            // The payload is constant and never inspects tool input or returns decisions.
            // Ignoring stdin is harmless; no input field is needed to report the event.
            // Literal JSON escapes keep control bytes off the shell command line.
            var json = "{\"terminalSequence\":\"\\u001b]777;notify;" +
                AgentOsc.StructuredNotifyTitle + ";id=claude;state=" + state + "\\u0007\"}";
            var command = windows ? $"echo '{json}'" : $"printf '%s\\n' '{json}'";
            hooks[eventName] = new[]
            {
                new { hooks = new[] { new { type = "command", command } } },
            };
        }
        return JsonSerializer.Serialize(new { hooks },
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>A shell helper for agents with no hook system: wrap the run, and report
    /// its start, end and exit status by hand.</summary>
    public static AgentAdapterSnippet ShellFunction() => new(
        "Shell wrapper",
        "~/.bashrc or ~/.zshrc · Any command-line agent",
        "Reports working, finished, and failed states for an agent command that you wrap.",
        """
        # resesh agent status: resesh_agent <id> <state> [label]
        resesh_agent() { printf '\033]7377;agent;id=%s;state=%s;label=%s\007' "$1" "$2" "${3:-}" > /dev/tty; }

        # Example wrapper — `agentrun claude` reports the run's lifecycle to the tab.
        agentrun() {
          local id="$1"; shift
          resesh_agent "$id" working
          "$id" "$@"; local rc=$?
          [ $rc -eq 0 ] && resesh_agent "$id" complete || resesh_agent "$id" failed
          resesh_agent "$id" exit
          return $rc
        }
        """);
}
