namespace Resesh.Terminal;

/// <summary>One enabled highlight rule as the terminal surface applies it.</summary>
public sealed record TerminalHighlight(
    string? Id,
    string Pattern,
    string? Color,
    bool Bold = false,
    bool Underline = false,
    bool MatchCase = false,
    bool ShowInOverview = false);
