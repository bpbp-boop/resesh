using System.Text;
using Sessions.Core.Sftp;

namespace Sessions.Core.Ssh;

public enum RemoteWorkingDirectoryProbeStatus
{
    Path,
    NotAtShell,
    Unavailable,
}

public sealed record RemoteWorkingDirectoryProbeResult(
    RemoteWorkingDirectoryProbeStatus Status,
    string? Path = null,
    string? Process = null);

/// <summary>
/// Finds the foreground process for this SSH connection's interactive PTY on Linux.
/// At a prompt that process is the active shell, whose /proc cwd is the exact directory.
/// The command runs on a separate SSH channel and never writes to the terminal.
/// </summary>
public static class RemoteWorkingDirectoryProbe
{
    private const string Marker = "sessions-cwd-v1:";

    public const string Command =
        "p=$PPID; " +
        "for c in $(cat /proc/$p/task/$p/children 2>/dev/null); do " +
        "[ \"$c\" = \"$$\" ] && continue; " +
        "t=$(readlink /proc/$c/fd/0 2>/dev/null) || continue; " +
        "case \"$t\" in /dev/pts/*|/dev/tty*) " +
        "s=$(cat /proc/$c/stat 2>/dev/null) || continue; " +
        "s=${s#*) }; set -- $s; fg=$6; " +
        "[ \"$fg\" -gt 0 ] 2>/dev/null || continue; " +
        "comm=$(cat /proc/$fg/comm 2>/dev/null) || continue; " +
        "case \"$comm\" in " +
        "sh|bash|dash|ash|zsh|fish|ksh|ksh93|mksh|csh|tcsh|pwsh|powershell|nu|elvish|xonsh|busybox) " +
        "cwd=$(readlink /proc/$fg/cwd 2>/dev/null) || { printf '" + Marker + "unavailable\\n'; exit; }; " +
        "if command -v base64 >/dev/null 2>&1; then " +
        "printf '" + Marker + "path:'; printf '%s' \"$cwd\" | base64 | tr -d '\\n'; printf '\\n'; " +
        "else printf '" + Marker + "unavailable\\n'; fi;; " +
        "*) printf '" + Marker + "not-shell:%s\\n' \"$comm\";; esac; exit;; esac; done; " +
        "printf '" + Marker + "unavailable\\n'";

    public static RemoteWorkingDirectoryProbeResult Parse(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return new(RemoteWorkingDirectoryProbeStatus.Unavailable);

        var markerIndex = output.LastIndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return new(RemoteWorkingDirectoryProbeStatus.Unavailable);

        var reply = output[(markerIndex + Marker.Length)..].Split(['\r', '\n'], 2)[0];
        if (reply.StartsWith("path:", StringComparison.Ordinal))
        {
            try
            {
                var bytes = Convert.FromBase64String(reply[5..]);
                var path = new UTF8Encoding(false, true).GetString(bytes);
                if (path.StartsWith('/') && !path.Any(character => character < ' ' || character == '\u007f'))
                    return new(RemoteWorkingDirectoryProbeStatus.Path, RemotePath.Normalize(path));
            }
            catch (FormatException)
            {
            }
            catch (DecoderFallbackException)
            {
            }
            return new(RemoteWorkingDirectoryProbeStatus.Unavailable);
        }

        if (reply.StartsWith("not-shell:", StringComparison.Ordinal))
        {
            var process = reply[10..].Trim();
            if (process.Length > 64 || process.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not '+'))
                process = "";
            return new(RemoteWorkingDirectoryProbeStatus.NotAtShell, Process: process.Length == 0 ? null : process);
        }

        return new(RemoteWorkingDirectoryProbeStatus.Unavailable);
    }
}
