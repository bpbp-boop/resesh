namespace Resesh.Terminal.Native;

/// <summary>What a terminal uses before the app passes its settings, and in place of a blank
/// font. They copy the defaults of Core's AppSettings and FontFamilies.Default: this library
/// does not reference Core. TerminalDefaultsTests in Core.Tests fails when the copies differ.</summary>
internal static class TerminalDefaults
{
    public const string FontFamily = "Cascadia Mono, Consolas, monospace";
    public const int FontSize = 14;
    public const int Scrollback = 10000;

    /// <summary>The first family of a font list, unquoted, for text the terminal draws with
    /// XAML (the commands panel, the ruler's card); the default font when the list is blank.</summary>
    public static string PrimaryFamily(string? fontFamily)
    {
        var family = (fontFamily ?? "").Split(',')[0].Trim().Trim('"', '\'');
        return family.Length > 0 ? family : PrimaryFamily(FontFamily);
    }
}
