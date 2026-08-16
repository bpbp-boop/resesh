namespace Sessions.Core.Agents;

/// <summary>
/// The guessing half of agent awareness: map a command line, a terminal title, or a
/// process name to an agent identity. Everything here is a heuristic and is treated as
/// such — identity only, never attention state, and always outranked by an adapter's
/// structured event or the user's own choice.
/// </summary>
public static class AgentDetection
{
    // Executable/package name -> identity. Keys are compared after stripping directories,
    // extensions and the usual launcher noise.
    private static readonly Dictionary<string, string> CommandNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "claude",
        ["claude-code"] = "claude",
        ["claudecode"] = "claude",
        ["@anthropic-ai/claude-code"] = "claude",
        ["codex"] = "codex",
        ["@openai/codex"] = "codex",
        ["gemini"] = "gemini",
        ["gemini-cli"] = "gemini",
        ["@google/gemini-cli"] = "gemini",
        ["pi"] = "pi",
        ["oh-my-pi"] = "pi",
        ["ohmypi"] = "pi",
        ["grok"] = "grok",
        ["grok-build"] = "grok",
        // Recognized as agents, not named individually.
        ["aider"] = AgentIdentities.Generic,
        ["goose"] = AgentIdentities.Generic,
        ["opencode"] = AgentIdentities.Generic,
        ["cursor-agent"] = AgentIdentities.Generic,
        ["amp"] = AgentIdentities.Generic,
        ["crush"] = AgentIdentities.Generic,
    };

    // Wrappers that run something else; the real command is the next token.
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "doas", "env", "command", "exec", "nohup", "time", "nice", "stdbuf", "winpty",
        "npx", "bunx", "pnpx", "uvx", "dlx", "wsl", "wsl.exe",
    };

    private static readonly string[] Extensions = [".exe", ".cmd", ".bat", ".ps1", ".sh", ".js", ".mjs"];

    /// <summary>
    /// The agent started by a command line, or null when it starts no agent we know.
    /// Null is a real answer for the caller ("this is a plain shell command"), which is
    /// why an empty or unparseable line returns null too.
    /// </summary>
    public static string? FromCommand(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var tokens = commandLine.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length && i < 8; i++)
        {
            var token = tokens[i].Trim('"', '\'');
            if (token.Length == 0)
                continue;
            // Leading environment assignments (FOO=bar cmd …).
            if (token.Contains('=') && !token.StartsWith('-'))
                continue;
            // Flags belonging to a wrapper we already skipped (sudo -E, npx --yes …).
            if (token.StartsWith('-'))
                continue;

            var name = Normalize(token);
            if (Wrappers.Contains(name))
                continue;
            return CommandNames.GetValueOrDefault(name);
        }
        return null;
    }

    /// <summary>The agent named by a terminal title, or null. Conservative on purpose:
    /// path-shaped tokens are skipped so a directory called "claude" in a title-reporting
    /// shell prompt is not mistaken for the agent running in it.</summary>
    public static string? FromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        foreach (var raw in title.Split((char[])[' ', '\t', ':', ',', '|', '(', ')', '[', ']'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            // "~/src/claude", "C:\tools\gemini" — a location, not a running program.
            if (raw.IndexOfAny(['/', '\\', '~']) >= 0)
                continue;
            var token = raw.Trim('"', '\'', '.', '*', '✳', '●', '·');
            if (token.Length == 0)
                continue;
            if (CommandNames.TryGetValue(Normalize(token), out var key))
                return key;
        }
        return null;
    }

    /// <summary>The agent identified by a process name (local tabs enumerate the processes
    /// inside their own job object, which is the strongest local signal we have).</summary>
    public static string? FromProcessName(string? processName) =>
        string.IsNullOrWhiteSpace(processName)
            ? null
            : CommandNames.GetValueOrDefault(Normalize(processName));

    /// <summary>The first agent among a set of process names (job-object membership).</summary>
    public static string? FromProcessNames(IEnumerable<string>? processNames)
    {
        if (processNames is null)
            return null;
        string? generic = null;
        foreach (var name in processNames)
        {
            var key = FromProcessName(name);
            if (key is null)
                continue;
            if (key != AgentIdentities.Generic)
                return key; // a named agent beats "some agent"
            generic = key;
        }
        return generic;
    }

    /// <summary>Strips directories, quotes and executable extensions: "C:\bin\claude.exe" → "claude".
    /// Scoped package names ("@anthropic-ai/claude-code") keep their slash on purpose.</summary>
    private static string Normalize(string token)
    {
        var value = token.Trim().Trim('"', '\'');
        if (!value.StartsWith('@'))
        {
            var slash = value.LastIndexOfAny(['/', '\\']);
            if (slash >= 0)
                value = value[(slash + 1)..];
        }
        foreach (var extension in Extensions)
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^extension.Length];
                break;
            }
        }
        return value;
    }
}
