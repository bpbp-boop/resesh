namespace Resesh.Core.Ssh;

/// <summary>
/// Builds the shell bootstrap that runs a persistent session inside an "invisible" tmux:
/// private socket (user's own tmux config/server untouched), no status bar, no prefix key,
/// and the outer alternate screen disabled (smcup@/rmcup@) so lines scroll into the client
/// terminal's native scrollback exactly as without tmux. On re-attach the pane history is
/// replayed with capture-pane — ending with the visible screen, which the attach then
/// repaints identically — so scrollback survives disconnects and app restarts seamlessly.
/// The bootstrap is POSIX sh; hosts whose login shell is fish/csh fall back to a plain
/// (non-persistent) shell with an error line.
/// </summary>
public static class TmuxPersistence
{
    private const string Socket = "resesh-app";

    /// <summary>Kept in tmux history so re-attach replay covers long disconnects.</summary>
    private const int HistoryLimit = 50000;

    /// <summary>Deterministic per saved session; clones get their own slot suffix.</summary>
    public static string SessionName(Guid id, int slot)
    {
        var name = "s" + id.ToString("N")[..12];
        return slot == 0 ? name : $"{name}-{slot + 1}";
    }

    /// <summary>
    /// History hygiene: on the tmux path the exec replaces the shell, so history is never
    /// flushed to disk. The remaining exposure is covered in layers — the leading space
    /// (HISTCONTROL=ignorespace/ignoreboth, the Debian/Ubuntu default, and zsh
    /// HIST_IGNORE_SPACE as set by oh-my-zsh); and on the no-tmux fallback, where the shell
    /// lives on, a bash self-scrub deletes the line — only after confirming via the marker
    /// comment that the last history entry really is this line, so it can never eat a real
    /// command when ignorespace already kept it out. Residual gap: zsh with incremental
    /// history but without HIST_IGNORE_SPACE writes the line before the exec.
    /// </summary>
    /// <summary>
    /// smcup@/rmcup@ keep tmux off the alternate screen; indn@ stops multi-line scrolls via
    /// CSI S, which xterm.js discards instead of pushing to scrollback (verified against the
    /// app's bundle) — without it chunks of output would vanish from native scrollback even
    /// with the alternate screen disabled. Plain `set -g` (not -ga): the private server has
    /// no user config to preserve, and overwrite is idempotent across re-asserts.
    /// </summary>
    private const string TerminalOverrides = "set -g terminal-overrides '*:smcup@:rmcup@:indn@'";

    /// <summary>
    /// tmux keeps OSC 0/2 from programs inside the pane to itself and emits a title to the
    /// outer terminal only when set-titles is on — which is off by default, so without this
    /// the app never sees a title on the persistent path at all.
    /// <para>
    /// What it reports matters as much as that it reports: pane_current_command is the
    /// foreground process's comm, so anything run through an interpreter is "node" or
    /// "python3" (codex and claude are both node scripts). The pane title is what the
    /// program calls itself, so prefer it for running programs. Pane titles are sticky,
    /// however, so report pane_current_command when it is an interactive shell. That lets
    /// the app retire a stale agent as soon as control returns to the prompt. tmux seeds the
    /// pane title with the hostname; that default also falls back to the command. Needs
    /// tmux 2.6+ for #{==:}; older servers emit the format literally, which
    /// <c>TabViewModel.ApplyTerminalTitle</c> discards.
    /// </para>
    /// </summary>
    private static readonly string[] InteractiveShells =
        ["sh", "bash", "zsh", "fish", "dash", "ash", "ksh", "mksh", "csh", "tcsh", "nu"];

    private static readonly string TitleReporting =
        "set -g set-titles on \\; "
        + "set -g set-titles-string "
        + $"'{BuildTitleFormat()}'";

    private static string BuildTitleFormat()
    {
        var format = "#{?#{==:#{pane_title},#{host}},#{pane_current_command},#{pane_title}}";
        foreach (var shell in InteractiveShells)
        {
            format = $"#{{?#{{==:#{{pane_current_command}},{shell}}},#{{pane_current_command}},{format}}}";
        }
        return format;
    }

    public static string BootstrapCommand(Guid id, int slot, string? newShellCommand = null, string? fallbackCommand = null)
    {
        var name = SessionName(id, slot);
        var tmux = $"tmux -L {Socket}";
        var exec = fallbackCommand is null ? "exec " : "";
        return
            // Wipe screen and scrollback first — removes this echoed line and the MOTD.
            " printf '\\033[2J\\033[3J\\033[H'; "
            + "if command -v tmux >/dev/null 2>&1; then "
            + $"if {tmux} has-session -t ={name} 2>/dev/null; then "
            // Replay full pane history including the visible screen; the attach that follows
            // repaints the same visible screen in place, so there is no gap and no duplicate.
            // The overrides are re-asserted before attach so servers created by an older app
            // version (or with stale options) pick up the current value — the client's tty
            // capabilities are built at attach time.
            + $"{tmux} capture-pane -e -p -t ={name} -S -; "
            + $"{exec}{tmux} {TerminalOverrides} \\; {TitleReporting} \\; attach-session -t ={name}; "
            + "else "
            + $"{exec}{tmux} -f /dev/null start-server \\; "
            + $"set -g history-limit {HistoryLimit} \\; "
            + "set -g status off \\; "
            + "set -g prefix None \\; "
            + "set -s escape-time 25 \\; "
            + $"{TerminalOverrides} \\; "
            + $"{TitleReporting} \\; "
            + (newShellCommand is not null ? "set -g allow-passthrough on \\; " : "")
            + $"new-session -s {name}"
            + (newShellCommand is not null ? " " + ShellIntegration.RemoteShellIntegration.QuotePosix(newShellCommand) : "")
            + "; "
            + "fi; "
            + "else "
            + "if type history >/dev/null 2>&1; then "
            + "case \"$(history 1)\" in *resesh-tmux-bootstrap*) "
            + "history -d \"$(history 1 | awk '{print $1;exit}')\" >/dev/null 2>&1;; esac; fi; "
            + "printf '\\n[resesh] tmux not found on this host - continuing without persistence.\\n\\n'; "
            + (fallbackCommand is not null ? "false; " : "")
            + "fi"
            + (fallbackCommand is not null
                ? "; resesh_result=$?; if test \"$resesh_result\" -eq 0; then exit 0; fi; "
                  + "printf '\\n[resesh] Persistent startup failed; opening a non-persistent shell.\\n'; exec " + fallbackCommand
                : "")
            + " # resesh-tmux-bootstrap";
    }

    public static string KillCommand(Guid id, int slot) =>
        $"tmux -L {Socket} kill-session -t ={SessionName(id, slot)}";

    /// <summary>Explicit resume never recreates a shell that ended after discovery.</summary>
    public static string ResumeCommand(Guid id, int slot)
    {
        var target = $"={SessionName(id, slot)}";
        return " printf '\\033[2J\\033[3J\\033[H'; "
            + $"tmux -L {Socket} capture-pane -e -p -t {target} -S -; "
            + $"exec tmux -L {Socket} {TerminalOverrides} \\; {TitleReporting} \\; attach-session -t {target}";
    }

    public static bool IsServerAbsent(SshCommandResult result) => !result.Success &&
        (result.Error.TrimStart().StartsWith("no server running on ", StringComparison.Ordinal)
         || (result.Error.TrimStart().StartsWith("error connecting to ", StringComparison.Ordinal)
             && result.Error.Contains("(No such file or directory)", StringComparison.Ordinal)));

    /// <summary>Only the active pane in the active window represents a session.</summary>
    public static string ManagementCommand() =>
        $"tmux -L {Socket} list-panes -a -F '#{{session_name}}|#{{window_active}}#{{pane_active}}|#{{session_attached}}|#{{session_created}}|#{{pane_current_command}}|#{{pane_current_path}}'";

    public static IReadOnlyList<TmuxSessionInfo> ParseManagedSessions(string output, Guid id)
    {
        var sessions = new Dictionary<int, TmuxSessionInfo>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('|', 6);
            if (parts is not [var name, "11", var attached, var created, var command, var path]
                || !TryParseSlot(name, SessionName(id, 0), out var slot)
                || name != SessionName(id, slot)
                || !int.TryParse(attached, out var clients) || clients < 0)
                continue;

            DateTimeOffset? started = null;
            if (long.TryParse(created, out var seconds) && seconds >= 0 && seconds <= 253402300799)
                started = DateTimeOffset.FromUnixTimeSeconds(seconds);
            sessions[slot] = new TmuxSessionInfo(slot, name, path, clients, started, command);
        }
        return sessions.Values.OrderBy(session => session.Slot).ToList();
    }

    /// <summary>Lists the active pane, attached-client count, and current path for every
    /// session on the app's private tmux server. The path is last because it can contain
    /// the separator character.</summary>
    public static string DiscoveryCommand() =>
        $"tmux -L {Socket} list-panes -a -F '#{{session_name}}|#{{pane_active}}|#{{session_attached}}|#{{pane_current_path}}'";

    /// <summary>Reads the tmux sessions that belong to one saved Sessions profile.</summary>
    public static IReadOnlyList<TmuxSessionInfo> ParseSessions(string output, Guid id)
    {
        var primaryName = SessionName(id, 0);
        var sessions = new Dictionary<int, TmuxSessionInfo>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('|', 4);
            if (parts is not [var name, "1", var attachedText, var path]
                || !TryParseSlot(name, primaryName, out var slot)
                || !int.TryParse(attachedText, out var attachedClients)
                || attachedClients < 0)
            {
                continue;
            }

            sessions[slot] = new TmuxSessionInfo(slot, name, path, attachedClients);
        }

        return sessions.Values.OrderBy(session => session.Slot).ToList();
    }

    /// <summary>Returns the lowest slot that is not present remotely or in another app tab.</summary>
    public static int NextAvailableSlot(IEnumerable<int> unavailableSlots)
    {
        var unavailable = unavailableSlots.ToHashSet();
        var slot = 0;
        while (unavailable.Contains(slot))
            slot++;
        return slot;
    }

    private static bool TryParseSlot(string name, string primaryName, out int slot)
    {
        if (name == primaryName)
        {
            slot = 0;
            return true;
        }

        var prefix = primaryName + "-";
        if (name.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(prefix.Length), out var suffix)
            && suffix >= 2)
        {
            slot = suffix - 1;
            return true;
        }

        slot = 0;
        return false;
    }

    /// <summary>
    /// Lists every pane's session/active/cwd (for "open file pane at current folder").
    /// Deliberately queries the whole server and matches client-side in
    /// <see cref="ParseCurrentPath"/> — the exec channel has no attached tmux client, and
    /// avoiding server-side target resolution is one less thing to go wrong remotely.
    /// </summary>
    public static string CurrentPathCommand() =>
        $"tmux -L {Socket} list-panes -a -F '#{{session_name}} #{{pane_active}} #{{pane_current_path}}'";

    /// <summary>The active pane's cwd for this session/slot, or null when absent from the
    /// <see cref="CurrentPathCommand"/> output. Paths may contain spaces; session names
    /// (hex-derived) cannot.</summary>
    public static string? ParseCurrentPath(string output, Guid id, int slot)
    {
        var name = SessionName(id, slot);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 3);
            if (parts is [var session, "1", var path] && session == name && path.StartsWith('/'))
                return path;
        }
        return null;
    }
}

/// <summary>One persistent shell found on the app's private tmux server.</summary>
public sealed record TmuxSessionInfo(int Slot, string Name, string CurrentPath, int AttachedClients,
    DateTimeOffset? CreatedAt = null, string CurrentCommand = "");
