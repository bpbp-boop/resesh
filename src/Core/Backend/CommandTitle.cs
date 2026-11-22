using System.Text.RegularExpressions;

namespace Resesh.Core.Backend;

/// <summary>
/// Turns a captured shell command line into the short program name a tab subtitle can
/// show ("sudo tail -f /var/log/syslog" -> "tail"), mirroring the look of tmux's
/// pane_current_command on hosts where the real process table is out of reach.
/// </summary>
public static class CommandTitle
{
    /// <summary>VAR=value prefixes run something else; skip them.</summary>
    private static readonly Regex Assignment = new("^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.Compiled);

    /// <summary>Wrappers whose operand is what the user thinks is running.</summary>
    private static readonly HashSet<string> Wrappers =
        new(StringComparer.Ordinal) { "sudo", "doas", "env", "nohup", "time", "exec", "command" };

    private static readonly char[] Operators = { ';', '|', '&', '<', '>', '(', ')' };
    private static readonly char[] Separators = { '/', '\\' };

    /// <summary>
    /// ConPTY can publish the launched executable path as its initial window title. It is
    /// transport noise, not a program-selected title, so callers can fall back to the
    /// profile's starting directory until the first prompt arrives.
    /// </summary>
    public static bool IsLocalExecutableTitle(string? title, string? executable)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(executable))
            return false;

        var expanded = Environment.ExpandEnvironmentVariables(executable.Trim());
        return string.Equals(
            title.Trim().Trim('"'),
            expanded.Trim().Trim('"'),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Null when nothing legible runs (blank, a bare VAR=value, a subshell).</summary>
    public static string? ProgramName(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;
        var tokens = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var i = 0;
        while (i < tokens.Length && Assignment.IsMatch(tokens[i]))
            i++;
        // A wrapper followed by an option keeps its own name: knowing which options take
        // arguments ("sudo -u alice htop") is a rabbit hole a guessed title can't justify.
        while (i + 1 < tokens.Length && Wrappers.Contains(tokens[i]) && !tokens[i + 1].StartsWith('-'))
        {
            i++;
            while (i < tokens.Length && Assignment.IsMatch(tokens[i]))
                i++;
        }
        if (i >= tokens.Length)
            return null;

        var name = tokens[i].Trim('"', '\'');
        var cut = name.IndexOfAny(Operators);
        if (cut == 0)
            return null; // "(cd x; make)" — a subshell has no honest single name
        if (cut > 0)
            name = name[..cut]; // "htop;ls" — the first shell operator ends the name
        var sep = name.LastIndexOfAny(Separators);
        if (sep >= 0)
            name = name[(sep + 1)..]; // "/usr/bin/python3" and "\vim" (alias bypass) alike
        if (cut < 0 && IsSsh(name))
            name = SshDestination(tokens.AsSpan(i + 1)) is { } destination ? $"ssh {destination}" : "ssh";
        return name.Length switch { 0 => null, > 48 => name[..48], _ => name };
    }

    private static bool IsSsh(string name) =>
        name.Equals("ssh", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>OpenSSH options that take an argument, per ssh(1).</summary>
    private const string SshOptionsWithArgument = "BbcDEeFIiJLlmOoPpQRSWw";

    /// <summary>
    /// The host an ssh command line connects to, as typed ("alice@web1"), so tabs that each
    /// ssh somewhere else don't all read "ssh". Null when no destination can be found.
    /// </summary>
    private static string? SshDestination(ReadOnlySpan<string> args)
    {
        string? user = null;
        string? destination = null;
        for (var i = 0; i < args.Length && destination is null; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                if (i + 1 < args.Length)
                    destination = args[i + 1];
                break;
            }
            if (arg.Length < 2 || arg[0] != '-')
            {
                destination = arg;
                break;
            }
            // Flags bundle ("-tt", "-vp22"); the first that takes an argument consumes the
            // rest of the token, or the next token when nothing is left.
            for (var c = 1; c < arg.Length; c++)
            {
                if (!SshOptionsWithArgument.Contains(arg[c]))
                    continue;
                var value = c + 1 < arg.Length ? arg[(c + 1)..] : i + 1 < args.Length ? args[++i] : null;
                if (arg[c] == 'l')
                    user = value?.Trim('"', '\'');
                break;
            }
        }
        if (destination is null)
            return null;

        destination = destination.Trim('"', '\'');
        var cut = destination.IndexOfAny(Operators);
        if (cut >= 0)
            destination = destination[..cut];
        if (destination.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            // ssh://[user@]host[:port] — the port is noise next to the host name.
            destination = destination["ssh://".Length..].TrimEnd('/');
            var port = destination.LastIndexOf(':');
            if (port > destination.LastIndexOf(']'))
                destination = destination[..port];
        }
        if (destination.Length == 0)
            return null;
        return user is { Length: > 0 } && !destination.Contains('@') ? $"{user}@{destination}" : destination;
    }
}
