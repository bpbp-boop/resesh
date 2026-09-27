using System.Text.Json.Serialization;
using Resesh.Core.Models;

namespace Resesh.Core.History;

/// <summary>
/// One finished shell command as the terminal saw it: the command line, where it ran, how it
/// ended, and the text it printed. Entries are local-only and never leave this computer.
/// </summary>
public sealed record CommandHistoryEntry
{
    /// <summary>Longest output kept per command. Longer output keeps its first part and sets
    /// <see cref="OutputTruncated"/>.</summary>
    public const int MaxOutputLength = 64 * 1024;

    /// <summary>Longest command line kept.</summary>
    public const int MaxCommandLength = 4096;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The saved session the tab was opened from; null for ad-hoc quick connects.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SessionId { get; init; }

    /// <summary>The session's display name when the command ran.</summary>
    public string SessionName { get; init; } = "";

    public SessionKind Kind { get; init; } = SessionKind.Ssh;

    /// <summary>"user@host" for remote sessions, the shell executable for local profiles.</summary>
    public string Target { get; init; } = "";

    /// <summary>The shell's working directory as last reported before the command, if known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkingDirectory { get; init; }

    public string Command { get; init; } = "";

    /// <summary>Null when the shell did not report a result (commands found without shell integration).</summary>
    public int? ExitCode { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EndedAt { get; init; }

    public string Output { get; init; } = "";

    /// <summary>The output was longer than <see cref="MaxOutputLength"/>; only its start is kept.</summary>
    public bool OutputTruncated { get; init; }

    /// <summary>The output scrolled out of the terminal's scrollback before it could be kept.</summary>
    public bool OutputLost { get; init; }

    /// <summary>True when shell integration marked the command; false when it was found from the prompt.</summary>
    public bool Exact { get; init; }

    [JsonIgnore]
    public bool Failed => ExitCode is { } code && code != 0;

    [JsonIgnore]
    public bool Succeeded => ExitCode == 0;

    [JsonIgnore]
    public TimeSpan? Duration => EndedAt is { } ended && ended >= StartedAt ? ended - StartedAt : null;

    /// <summary>Which host a run belongs to when comparing runs: the saved session, or the
    /// target for an unsaved connection.</summary>
    [JsonIgnore]
    public string HostKey => SessionId is { } id ? id.ToString("D") : "target:" + Target;

    /// <summary>The command as runs are matched: trimmed, whitespace runs collapsed to one space.</summary>
    [JsonIgnore]
    public string CommandKey => NormalizeCommand(Command);

    public static string NormalizeCommand(string command) =>
        string.Join(' ', (command ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The output shows a pager prompt, so it holds only the pages that were shown.</summary>
    [JsonIgnore]
    public bool LooksPaged => PagerPrompt.IsMatch(Output);

    private static readonly System.Text.RegularExpressions.Regex PagerPrompt = new(
        @"--\s?More\s?--|---\(more( \d+%)?\)---|<--- More --->|^\(END\)$|Press any key to continue",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>How history names where a session runs: "user@host" (with a non-default
    /// port), "host:port" for telnet, or the shell executable's file name for local profiles.</summary>
    public static string TargetOf(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        switch (session.Kind)
        {
            case SessionKind.Local:
                var executable = session.Local?.Executable ?? "";
                return Path.GetFileName(executable.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : executable;
            case SessionKind.Telnet:
                return session.Port is 23 or 0 ? session.Host : $"{session.Host}:{session.Port}";
            default:
                var host = string.IsNullOrEmpty(session.Username) ? session.Host : $"{session.Username}@{session.Host}";
                return session.Port is 22 or 0 ? host : $"{host}:{session.Port}";
        }
    }

    /// <summary>This entry with oversized fields cut down to the stored limits.</summary>
    public CommandHistoryEntry Normalized()
    {
        var command = Command.Length > MaxCommandLength ? Command[..MaxCommandLength] : Command;
        var output = Output ?? "";
        var truncated = OutputTruncated;
        if (output.Length > MaxOutputLength)
        {
            // Never split a surrogate pair at the cut.
            var cut = MaxOutputLength;
            if (char.IsHighSurrogate(output[cut - 1]))
                cut--;
            output = output[..cut];
            truncated = true;
        }
        return this with { Command = command, Output = output, OutputTruncated = truncated };
    }
}
