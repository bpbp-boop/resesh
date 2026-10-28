namespace Resesh.Core.Storage;

/// <summary>How a workspace looks on the window's rail when nobody has customised it: the
/// initials of its name on a colour taken from its id.</summary>
public static class WorkspaceAppearance
{
    /// <summary>Tile colours that hold white initials at a readable contrast on dark and light
    /// chrome alike. The session colour tags are among them, so a chosen colour matches one.</summary>
    public static IReadOnlyList<(string Name, string Hex)> Palette { get; } =
    [
        ("Blue", "#0078D7"),
        ("Green", "#10893E"),
        ("Purple", "#886CE4"),
        ("Orange", "#CA5010"),
        ("Magenta", "#C239B3"),
        ("Teal", "#038387"),
        ("Red", "#E74856"),
        ("Graphite", "#5D5A58"),
    ];

    /// <summary>The colour a workspace draws: its own, or the palette entry its id picks.
    /// Guid bytes, unlike string hashes, give the same answer in every run.</summary>
    public static string Color(Workspace workspace) =>
        IsColor(workspace.ColorTag) ? workspace.ColorTag! : Palette[workspace.Id.ToByteArray()[0] % Palette.Count].Hex;

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

    private static bool IsColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
}
