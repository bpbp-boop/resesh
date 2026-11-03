namespace Resesh.Core.Models;

/// <summary>One entry in a colour tag picker; <see cref="Hex"/> null is "None".</summary>
public sealed record SessionColorChoice(string Name, string? Hex);

/// <summary>
/// The colour tags the session editors offer. A saved tag need not be one of them: imports,
/// backups and older builds can carry any #RRGGBB, in either case. The editors match a tag
/// without regard to case and show one they do not know as a Custom choice, so saving a
/// session never changes a colour the user did not touch.
/// </summary>
public static class SessionColors
{
    public static SessionColorChoice None { get; } = new("None", null);

    /// <summary>The named tags, in picker order after None.</summary>
    public static IReadOnlyList<SessionColorChoice> Named { get; } =
    [
        new("Red", "#E74856"),
        new("Orange", "#FF8C00"),
        new("Yellow", "#FFB900"),
        new("Green", "#10893E"),
        new("Blue", "#0078D7"),
        new("Purple", "#886CE4"),
    ];

    /// <summary>None, then the named tags.</summary>
    public static IReadOnlyList<SessionColorChoice> All { get; } = [None, .. Named];

    /// <summary>The choices for a session whose tag is <paramref name="current"/>: <see cref="All"/>,
    /// plus a Custom entry carrying the saved text unchanged when no named tag matches it.</summary>
    public static IReadOnlyList<SessionColorChoice> ChoicesFor(string? current) =>
        Find(current) is not null || string.IsNullOrWhiteSpace(current)
            ? All
            : [.. All, new($"Custom ({current.Trim()})", current)];

    /// <summary>The position of <paramref name="current"/> in <see cref="ChoicesFor"/>.</summary>
    public static int IndexIn(IReadOnlyList<SessionColorChoice> choices, string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
            return 0;
        for (var i = 1; i < choices.Count; i++)
        {
            if (Same(choices[i].Hex, current))
                return i;
        }
        return 0;
    }

    /// <summary>The named tag <paramref name="hex"/> is, ignoring case and spaces; null when none.</summary>
    public static SessionColorChoice? Find(string? hex) =>
        string.IsNullOrWhiteSpace(hex) ? null : Named.FirstOrDefault(choice => Same(choice.Hex, hex));

    private static bool Same(string? first, string? second) =>
        string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);
}
