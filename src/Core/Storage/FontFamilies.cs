namespace Resesh.Core.Storage;

/// <summary>
/// The terminal font setting is one comma-separated list: the font, then the fonts used in order
/// for characters it lacks (a Nerd Font for icons, a CJK font). These split and rebuild it so the
/// font can be picked without losing the fallbacks.
/// </summary>
public static class FontFamilies
{
    public const string Default = "Cascadia Mono, Consolas, monospace";

    public static IReadOnlyList<string> Split(string? list) =>
        (list ?? "").Split(',')
            .Select(name => name.Trim().Trim('"', '\''))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string Join(IEnumerable<string> names) =>
        string.Join(", ", Split(string.Join(",", names)));

    /// <summary>The CSS generic <c>monospace</c> names no font: the terminal skips it, so it
    /// is never the font, only a fallback.</summary>
    public static bool IsGeneric(string name) => name.Equals("monospace", StringComparison.OrdinalIgnoreCase);

    /// <summary>The font itself: the list's first named family, as the terminal reads it
    /// (its TerminalDefaults.FamilyNames).</summary>
    public static string Primary(string? list) => Split(list).FirstOrDefault(name => !IsGeneric(name)) ?? "";

    /// <summary>Everything but the font, as a list.</summary>
    public static string Fallbacks(string? list) => Join(WithoutPrimary(list));

    /// <summary>The list with its font replaced (or added) and the fallbacks kept. A font that
    /// was also a fallback moves to the front rather than appearing twice.</summary>
    public static string WithPrimary(string? list, string primary) =>
        Join(Split(primary).Concat(WithoutPrimary(list)));

    /// <summary>The list with the same font and new fallbacks.</summary>
    public static string WithFallbacks(string? list, string fallbacks) =>
        Join(Split(Primary(list)).Concat(Split(fallbacks)));

    private static IEnumerable<string> WithoutPrimary(string? list)
    {
        var primary = Primary(list);
        return Split(list).Where(name => !name.Equals(primary, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A session's font in front of the app's list, so a session that only names a
    /// font still gets the app's fallbacks.</summary>
    public static string Layer(string? sessionFonts, string appFonts) =>
        Split(sessionFonts).Count == 0 ? appFonts : Join(Split(sessionFonts).Concat(Split(appFonts)));
}
