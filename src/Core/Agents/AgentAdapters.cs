namespace Sessions.Core.Agents;

/// <summary>An adapter snippet the user can install on a target to upgrade a tab from
/// guessed identity to reported lifecycle events.</summary>
public sealed record AgentAdapterSnippet(string Title, string Target, string Description, string Text);

/// <summary>
/// Text for the opt-in adapters. Sessions never installs these itself: the tab menu shows
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
        "values are percent-encoded; label is optional and shown only in the tab tooltip";

    public static IReadOnlyList<AgentAdapterSnippet> All =>
    [
        ClaudeCodePosix(),
        ClaudeCodeWindows(),
        ShellFunction(),
    ];

    /// <summary>Claude Code hooks on a POSIX host (Linux/macOS/WSL). Writes straight to the
    /// tty so the sequence reaches the terminal instead of the hook's captured stdout.</summary>
    public static AgentAdapterSnippet ClaudeCodePosix() => new(
        "Claude Code — Linux / macOS",
        "~/.claude/settings.json on the remote host",
        "Reports working / needs-approval / complete / exit from Claude Code's own hooks.",
        """
        {
          "hooks": {
            "SessionStart":     [{ "hooks": [{ "type": "command", "command": "printf '\\033]7377;agent;id=claude;state=working\\007' > /dev/tty" }] }],
            "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "printf '\\033]7377;agent;id=claude;state=working\\007' > /dev/tty" }] }],
            "Notification":     [{ "hooks": [{ "type": "command", "command": "printf '\\033]7377;agent;id=claude;state=needs-approval\\007' > /dev/tty" }] }],
            "Stop":             [{ "hooks": [{ "type": "command", "command": "printf '\\033]7377;agent;id=claude;state=complete\\007' > /dev/tty" }] }],
            "SessionEnd":       [{ "hooks": [{ "type": "command", "command": "printf '\\033]7377;agent;id=claude;state=exit\\007' > /dev/tty" }] }]
          }
        }
        """);

    /// <summary>Claude Code hooks on Windows. A hook process inherits the tab's
    /// pseudoconsole, so writing to the console reaches the terminal directly.</summary>
    public static AgentAdapterSnippet ClaudeCodeWindows() => new(
        "Claude Code — Windows",
        "%USERPROFILE%\\.claude\\settings.json",
        "Same events as the POSIX adapter, written to the tab's pseudoconsole.",
        """
        {
          "hooks": {
            "SessionStart":     [{ "hooks": [{ "type": "command", "command": "powershell -NoProfile -Command \"[Console]::Write([char]27+']7377;agent;id=claude;state=working'+[char]7)\"" }] }],
            "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "powershell -NoProfile -Command \"[Console]::Write([char]27+']7377;agent;id=claude;state=working'+[char]7)\"" }] }],
            "Notification":     [{ "hooks": [{ "type": "command", "command": "powershell -NoProfile -Command \"[Console]::Write([char]27+']7377;agent;id=claude;state=needs-approval'+[char]7)\"" }] }],
            "Stop":             [{ "hooks": [{ "type": "command", "command": "powershell -NoProfile -Command \"[Console]::Write([char]27+']7377;agent;id=claude;state=complete'+[char]7)\"" }] }],
            "SessionEnd":       [{ "hooks": [{ "type": "command", "command": "powershell -NoProfile -Command \"[Console]::Write([char]27+']7377;agent;id=claude;state=exit'+[char]7)\"" }] }]
          }
        }
        """);

    /// <summary>A shell helper for agents with no hook system: wrap the run, and report
    /// its start, end and exit status by hand.</summary>
    public static AgentAdapterSnippet ShellFunction() => new(
        "Any agent — shell wrapper",
        "~/.bashrc or ~/.zshrc on the remote host",
        "Reports working on launch and complete/failed on exit for an agent you wrap yourself.",
        """
        # Sessions agent status: sessions_agent <id> <state> [label]
        sessions_agent() { printf '\033]7377;agent;id=%s;state=%s;label=%s\007' "$1" "$2" "${3:-}" > /dev/tty; }

        # Example wrapper — `agentrun claude` reports the run's lifecycle to the tab.
        agentrun() {
          local id="$1"; shift
          sessions_agent "$id" working
          "$id" "$@"; local rc=$?
          [ $rc -eq 0 ] && sessions_agent "$id" complete || sessions_agent "$id" failed
          sessions_agent "$id" exit
          return $rc
        }
        """);
}
