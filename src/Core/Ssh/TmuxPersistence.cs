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
            + $"{tmux} capture-pane -e -p -t ={name} -S -; "
            + $"exec {tmux} attach-session -t ={name}; "
            + "else "
            + $"exec {tmux} -f /dev/null start-server \\; "
            + $"set -g history-limit {HistoryLimit} \\; "
            + "set -g status off \\; "
            + "set -g prefix None \\; "
            + "set -s escape-time 25 \\; "
            + "set -ga terminal-overrides ',*:smcup@:rmcup@' \\; "
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
}
