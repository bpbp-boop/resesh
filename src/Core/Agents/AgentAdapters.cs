using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        "The exact title resesh-agent is reserved for structured status. Other terminals may show it as a desktop notification.\n\n" +
        "Inside a persistent (tmux) session, set a pane option instead (tmux 3.0 or later):\n" +
        "tmux set -p -t \"$TMUX_PANE\" @resesh_status state=<state>:app=<agent>\n" +
        "states: working | blocked (with kind=permission or kind=question) | done | error | idle; unset the option on exit";

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

        var hooks = new JsonObject();
        foreach (var (eventName, state) in eventStates)
        {
            hooks[eventName] = HookGroup(new JsonObject
            {
                ["type"] = "command",
                ["command"] = CodexPosixCommand(state),
                ["commandWindows"] = CodexWindowsCommand(state),
                ["timeout"] = 3,
            });
        }

        return Snippet(new JsonObject
        {
            ["description"] = "resesh tab status for Codex. Reports state only; never approves or sends input.",
            ["hooks"] = hooks,
        });
    }

    private static string CodexPosixCommand(string state) =>
        $"sh -c \"cat >/dev/null; if [ $TMUX_PANE ]; then {TmuxCommand("codex", state)}; "
        + $"else printf '\\033]7377;agent;id=codex;state={state}\\007' > /dev/tty; fi\"";

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
        "For Claude Code before 2.1.295; later versions report their own status. " +
        "Reports working, approval, finished, and exit states via Claude Code's terminalSequence JSON output. " +
        "Interactive sessions only. In another terminal, the reserved resesh-agent title may appear as a desktop notification.",
        ClaudeCodeHooksJson(windows: false));

    /// <summary>The single-quoted echo argument preserves JSON escapes in both Git Bash
    /// and PowerShell, without a console device or a nested PowerShell command.</summary>
    public static AgentAdapterSnippet ClaudeCodeWindows() => new(
        "Claude Code",
        "%USERPROFILE%\\.claude\\settings.json · Windows",
        "For Claude Code before 2.1.295; later versions report their own status. " +
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
        var hooks = new JsonObject();
        foreach (var (eventName, state) in eventStates)
        {
            // The payload is constant and never inspects tool input or returns decisions.
            // Ignoring stdin is harmless; no input field is needed to report the event.
            // Literal JSON escapes keep control bytes off the shell command line.
            var json = "{\"terminalSequence\":\"\\u001b]777;notify;" +
                AgentOsc.StructuredNotifyTitle + ";id=claude;state=" + state + "\\u0007\"}";
            // Inside tmux the POSIX hook also leaves the state in the pane option; the
            // JSON is still the hook's only output.
            var command = windows
                ? $"echo '{json}'"
                : $"[ $TMUX_PANE ] && {TmuxCommand("claude", state)}; printf '%s\\n' '{json}'";
            hooks[eventName] = HookGroup(new JsonObject { ["type"] = "command", ["command"] = command });
        }
        return Snippet(new JsonObject { ["hooks"] = hooks });
    }

    /// <summary>[{ "hooks": [hook] }], the shape both agents' hook settings use per event.</summary>
    private static JsonArray HookGroup(JsonObject hook) => new(new JsonObject { ["hooks"] = new JsonArray(hook) });

    // Relaxed escaping so the quotes inside the commands stay readable (\" instead of
    // ") — this text is copied into a config file the user should review.
    private static readonly JsonSerializerOptions SnippetOptions =
        new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Snippet(JsonObject root) => root.ToJsonString(SnippetOptions);

    // ---- persistent (tmux) sessions ----
    //
    // tmux discards the sequences above, so inside tmux an adapter stores an OSC 7501
    // report body in the pane option the app's title format carries out (TmuxStatus).
    // TMUX_PANE is set for every process in a pane and holds no spaces, so it is tested
    // and passed unquoted: the commands sit inside quoted JSON strings already.

    /// <summary>The report for one of the adapter states, or null for "exit".</summary>
    internal static string? TmuxReport(string agent, string state) => state switch
    {
        "exit" => null,
        "working" => $"state=working:app={agent}",
        "needs-approval" => $"state=blocked:kind=permission:app={agent}",
        "needs-answer" => $"state=blocked:kind=question:app={agent}",
        "complete" => $"state=done:app={agent}",
        "failed" => $"state=error:app={agent}",
        _ => $"state=idle:app={agent}",
    };

    /// <summary>Sets or (on exit) unsets the pane option. The shell's pid makes each
    /// report distinct, so tmux sends the title again when a state repeats.</summary>
    private static string TmuxCommand(string agent, string state) =>
        (TmuxReport(agent, state) is { } report
            ? $"tmux set -p -t $TMUX_PANE {TmuxStatus.Option} {report}:n=$$"
            : $"tmux set -p -u -t $TMUX_PANE {TmuxStatus.Option}")
        + " >/dev/null 2>&1";

    /// <summary>A shell helper for agents with no hook system: wrap the run, and report
    /// its start, end and exit status by hand.</summary>
    public static AgentAdapterSnippet ShellFunction() => new(
        "Shell wrapper",
        "~/.bashrc or ~/.zshrc · Any command-line agent",
        "Reports working, finished, and failed states for an agent command that you wrap.",
        """
        # resesh agent status: resesh_agent <id> <state> [label]
        # Inside tmux the state goes into a pane option that resesh reads through the title.
        resesh_agent() {
          if [ -z "$TMUX_PANE" ]; then
            printf '\033]7377;agent;id=%s;state=%s;label=%s\007' "$1" "$2" "${3:-}" > /dev/tty
            return
          fi
          case "$2" in
            exit) tmux set -p -u -t "$TMUX_PANE" @resesh_status 2>/dev/null; return ;;
            working) set -- "$1" state=working ;;
            needs-approval) set -- "$1" state=blocked:kind=permission ;;
            needs-answer) set -- "$1" state=blocked:kind=question ;;
            complete) set -- "$1" state=done ;;
            failed) set -- "$1" state=error ;;
            *) set -- "$1" state=idle ;;
          esac
          tmux set -p -t "$TMUX_PANE" @resesh_status "$2:app=$1" 2>/dev/null
        }

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
