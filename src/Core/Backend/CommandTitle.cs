using System.Text.RegularExpressions;

namespace Sessions.Core.Backend;

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
        return name.Length switch { 0 => null, > 48 => name[..48], _ => name };
    }
}
