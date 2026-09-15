namespace Resesh.Terminal;

/// <summary>An exact shell command lifecycle, identified within one terminal surface.
/// A null completed exit code means the next prompt arrived without a status.</summary>
public sealed record TerminalCommandExecution(long Id, string CommandLine, bool Completed, int? ExitCode);
