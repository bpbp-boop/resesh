namespace Resesh.Core.Storage;

/// <summary>One icon a workspace can show on the rail: a stable key that is saved, a name for
/// the picker, extra words the picker's filter matches, and how it is drawn: a Segoe Fluent
/// Icons glyph, or path data on a 16×16 grid for a shape the font lacks. Exactly one of
/// <paramref name="Glyph"/> and <paramref name="Path"/> is set.</summary>
public sealed record WorkspaceIcon(string Key, string Name, string Keywords, string Glyph, string Path = "")
{
    /// <summary>Matches the filter text against the name, key and keywords, ignoring case.</summary>
    public bool Matches(string filter) =>
        string.IsNullOrWhiteSpace(filter)
        || filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            Name.Contains(word, StringComparison.OrdinalIgnoreCase)
            || Key.Contains(word, StringComparison.OrdinalIgnoreCase)
            || Keywords.Contains(word, StringComparison.OrdinalIgnoreCase));
}

/// <summary>How a workspace looks on the window's rail: a chosen icon, or the initials of its
/// name when it has none.</summary>
public static class WorkspaceAppearance
{
    /// <summary>Home's glyph; kept out of <see cref="Icons"/> so no workspace looks like Home.</summary>
    public const string HomeGlyph = "";

    /// <summary>A database cylinder, which Segoe Fluent Icons has no glyph for, drawn as a
    /// 1px outline so it sits with the font's glyphs.</summary>
    private const string DatabasePath =
        "M2.5,3.5 A5.5,2 0 0 1 13.5,3.5 A5.5,2 0 0 1 2.5,3.5 Z " +
        "M2.5,3.5 L2.5,12.5 A5.5,2 0 0 0 13.5,12.5 L13.5,3.5 " +
        "M2.5,8 A5.5,2 0 0 0 13.5,8";

    /// <summary>A lab beaker with a lip and a fill line, drawn like <see cref="DatabasePath"/>.</summary>
    private const string BeakerPath =
        "M1.5,1.5 L14.5,1.5 " +
        "M3,1.5 L3,12.5 A2,2 0 0 0 5,14.5 L11,14.5 A2,2 0 0 0 13,12.5 L13,1.5 " +
        "M3,9 L13,9";

    /// <summary>Monochrome outline icons that match the rail's tool icons and follow every
    /// theme. Each glyph exists in Segoe Fluent Icons and in Segoe MDL2 Assets, which Windows
    /// 10 falls back to. Grouped as machines, infrastructure, tools, security, operations and
    /// general marks; keys are saved, so never rename one.</summary>
    public static IReadOnlyList<WorkspaceIcon> Icons { get; } =
    [
        new("server", "Server", "host machine vm rack", ""),
        new("database", "Database", "db sql postgres mysql oracle mongo redis data storage", "", DatabasePath),
        new("desktop", "Desktop", "workstation pc monitor", ""),
        new("laptop", "Laptop", "notebook pc", ""),
        new("chip", "Hardware", "cpu embedded iot firmware", ""),
        new("cloud", "Cloud", "aws azure gcp hosted", ""),
        new("globe", "Web", "internet www site http", ""),
        new("cluster", "Cluster", "kubernetes k8s nodes mesh", ""),
        new("cube", "Container", "docker pod image", ""),
        new("box", "Archive", "backup package storage", ""),
        new("terminal", "Terminal", "shell console cli command", ""),
        new("code", "Code", "dev development source git", ""),
        new("tools", "Tools", "devtools utilities", ""),
        new("wrench", "Maintenance", "fix repair change", ""),
        new("lock", "Lock", "secure private vpn", ""),
        new("key", "Key", "ssh credentials secrets auth", ""),
        new("shield", "Security", "firewall protect", ""),
        new("warning", "Incident", "alert outage emergency", ""),
        new("lightning", "Production", "prod live power", ""),
        new("pulse", "Monitoring", "health heartbeat status", ""),
        new("chart", "Metrics", "graph grafana stats", ""),
        new("briefcase", "Work", "client customer business", ""),
        new("people", "Team", "group users", ""),
        new("education", "Learning", "training school course", ""),
        new("beaker", "Lab", "beaker science experiment test staging sandbox", "", BeakerPath),
        new("lightbulb", "Ideas", "experiment", ""),
        new("puzzle", "Plugins", "extensions integration", ""),
        new("flag", "Flag", "milestone", ""),
        new("tag", "Tag", "label release", ""),
        new("pin", "Site", "location datacenter office", ""),
        new("star", "Favorite", "important", ""),
    ];

    /// <summary>The icon saved under this key, or null for none or a key this build does not
    /// know (such as a session icon saved by an earlier build), which draws the initials.</summary>
    public static WorkspaceIcon? Find(string? key) =>
        string.IsNullOrEmpty(key)
            ? null
            : Icons.FirstOrDefault(icon => string.Equals(icon.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The most characters a rail button fits at a readable size.</summary>
    public const int MaxInitials = 3;

    /// <summary>The letters a workspace's button shows: its own, or the name's.</summary>
    public static string Initials(Workspace workspace) =>
        NormalizeInitials(workspace.Initials) ?? Monogram(workspace.Name);

    /// <summary>Custom initials without spaces, cut to <see cref="MaxInitials"/> characters as a
    /// reader sees them (an emoji or accented letter counts once); null for none. Case is
    /// left as typed.</summary>
    public static string? NormalizeInitials(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var compact = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(compact);
        var kept = new System.Text.StringBuilder();
        for (var count = 0; count < MaxInitials && elements.MoveNext(); count++)
            kept.Append(elements.GetTextElement());
        return kept.ToString();
    }

    /// <summary>Up to two letters for a tile: the first letters of the first two words, or the
    /// first two letters of a one-word name ("Prod incident" → "PI", "lab" → "La").</summary>
    public static string Monogram(string name)
    {
        var words = name.Split([' ', '-', '_', '.', '/', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(char.IsLetterOrDigit)]))
            .Where(word => word.Length > 0)
            .ToList();
        if (words.Count == 0)
            return "?";
        if (words.Count == 1)
        {
            var word = words[0];
            return word.Length == 1
                ? word.ToUpperInvariant()
                : char.ToUpperInvariant(word[0]) + word[1..2].ToLowerInvariant();
        }
        return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0]));
    }
}
