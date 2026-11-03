namespace Resesh.Terminal.Native;

/// <summary>What a terminal uses before the app passes its settings, and in place of a blank
/// font. They copy the defaults of Core's AppSettings and FontFamilies.Default: this library
/// does not reference Core. TerminalDefaultsTests in Core.Tests fails when the copies differ.</summary>
internal static class TerminalDefaults
{
    public const string FontFamily = "Cascadia Mono, Consolas, monospace";
    public const int FontSize = 14;
    public const int Scrollback = 10000;

    /// <summary>The families a font list names, in order: unquoted, without blanks, repeats
    /// or the CSS generic <c>monospace</c> (which names no font). The renderer loads these
    /// ahead of its own fallbacks; Core's FontFamilies.Primary reads the same first name.</summary>
    public static IReadOnlyList<string> FamilyNames(string? fontFamily) =>
        (fontFamily ?? "").Split(',')
            .Select(name => name.Trim().Trim('"', '\''))
            .Where(name => name.Length > 0 && !IsGeneric(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsGeneric(string family) => family.Equals("monospace", StringComparison.OrdinalIgnoreCase);

    /// <summary>The family the renderer draws a font list in, for text the terminal draws
    /// with XAML (the commands panel, the ruler's card): the first one named that is
    /// installed, else the first installed default, as the renderer picks.</summary>
    public static string PrimaryFamily(string? fontFamily, Func<string, bool> isInstalled)
    {
        var defaults = FamilyNames(FontFamily);
        return FamilyNames(fontFamily).Concat(defaults).FirstOrDefault(isInstalled) ?? defaults[^1];
    }
}
