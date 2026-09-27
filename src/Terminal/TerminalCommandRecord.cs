namespace Resesh.Terminal;

/// <summary>A finished command for command history: its line, result, timing (Unix ms), and
/// the output the terminal still held. <paramref name="OutputLost"/> means the output left
/// the scrollback before the command ended.</summary>
public sealed record TerminalCommandRecord(
    string CommandLine,
    int? ExitCode,
    bool Exact,
    long StartedUnixMs,
    long? EndedUnixMs,
    string Output,
    bool OutputTruncated,
    bool OutputLost,
    string? Directory = null);
