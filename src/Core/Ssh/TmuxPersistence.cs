namespace Sessions.Core.Ssh;

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
    private const string Socket = "sessions-app";

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

    public static string BootstrapCommand(Guid id, int slot)
    {
        var name = SessionName(id, slot);
        var tmux = $"tmux -L {Socket}";
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
            + $"exec {tmux} {TerminalOverrides} \\; attach-session -t ={name}; "
            + "else "
            + $"exec {tmux} -f /dev/null start-server \\; "
            + $"set -g history-limit {HistoryLimit} \\; "
            + "set -g status off \\; "
            + "set -g prefix None \\; "
            + "set -s escape-time 25 \\; "
            + $"{TerminalOverrides} \\; "
            + $"new-session -s {name}; "
            + "fi; "
            + "else "
            + "if type history >/dev/null 2>&1; then "
            + "case \"$(history 1)\" in *sessions-tmux-bootstrap*) "
            + "history -d \"$(history 1 | awk '{print $1;exit}')\" >/dev/null 2>&1;; esac; fi; "
            + "printf '\\n[Sessions] tmux not found on this host - continuing without persistence.\\n\\n'; "
            + "fi # sessions-tmux-bootstrap";
    }

    public static string KillCommand(Guid id, int slot) =>
        $"tmux -L {Socket} kill-session -t ={SessionName(id, slot)}";

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
public sealed record TmuxSessionInfo(int Slot, string Name, string CurrentPath, int AttachedClients);
