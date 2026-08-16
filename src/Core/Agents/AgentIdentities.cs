namespace Sessions.Core.Agents;

/// <summary>
/// One agent identity: its stable key (also the bundled SVG filename, without extension),
/// display name, and badge accent. <see cref="IsAgent"/> is false for the "normal shell"
/// identity, which is a real detection result but paints no icon.
/// </summary>
public sealed record AgentIdentityInfo(string Key, string Name, string Accent, bool IsAgent = true);

/// <summary>
/// The built-in agent catalog. An agent identity answers "what is currently running in
/// this tab", which is deliberately separate from the session icon ("where and how this
/// tab is running") — the two are never merged and never overwrite each other.
/// </summary>
public static class AgentIdentities
{
    /// <summary>Sentinel meaning "never show an agent icon here"; blocks detection the way
    /// <c>SessionIcons.None</c> blocks icon suggestion. Null instead means auto-detect.</summary>
    public const string None = "none";

    /// <summary>A normal shell — a detection result, not an agent.</summary>
    public const string Shell = "shell";

    /// <summary>An agent we recognize as an agent but can't name.</summary>
    public const string Generic = "agent";

    public static readonly IReadOnlyList<AgentIdentityInfo> All =
    [
        new("claude", "Claude Code", "#D97757"),
        new("codex", "Codex", "#10A37F"),
        new("gemini", "Gemini CLI", "#4285F4"),
        new("pi", "Pi / oh-my-pi", "#8B5CF6"),
        new("grok", "Grok Build", "#7D8590"),
        new(Generic, "Agent", "#5B8DEF"),
        new(Shell, "Shell", "#8A8A8A", IsAgent: false),
    ];

    public static AgentIdentityInfo? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a key that names an actual agent (so the tab paints an icon).
    /// <see cref="Shell"/>, <see cref="None"/>, null and unknown keys are all false.</summary>
    public static bool IsAgentKey(string? key) => Find(key) is { IsAgent: true };

    public static string DisplayName(string? key) => Find(key)?.Name ?? key ?? "";
}
