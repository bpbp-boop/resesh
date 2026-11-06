using System.Text.RegularExpressions;

namespace Resesh.Core.Storage;

/// <summary>Groups to browse themes by. A theme can be in several, or in none.</summary>
[Flags]
public enum ThemeCategory
{
    None = 0,
    /// <summary>Plain black, white or grey backgrounds with standard terminal colors, and
    /// the defaults of well-known terminals.</summary>
    Traditional = 1 << 0,
    /// <summary>Blue, teal or purple backgrounds.</summary>
    Cool = 1 << 1,
    /// <summary>Red, orange, brown or yellow backgrounds.</summary>
    Warm = 1 << 2,
    /// <summary>Light, soft ANSI colors.</summary>
    Pastel = 1 << 3,
    /// <summary>Fully saturated ANSI colors on a dark background.</summary>
    Vivid = 1 << 4,
    /// <summary>Old hardware and phosphor screens: CRT, CGA, C64, green and amber screens.</summary>
    Retro = 1 << 5,
    /// <summary>Text at a WCAG contrast of 15:1 or more against the background.</summary>
    HighContrast = 1 << 6,
}

/// <summary>What a theme browser shows: names containing <see cref="Text"/> (any case), in
/// <see cref="Category"/> unless it is None, and light or dark ones only when
/// <see cref="Light"/> says which.</summary>
public sealed record ThemeQuery(string Text = "", ThemeCategory Category = ThemeCategory.None, bool? Light = null)
{
    public static ThemeQuery All { get; } = new();

    public bool Matches(ThemeChoice theme) =>
        theme.Name.Contains(Text, StringComparison.OrdinalIgnoreCase)
        && (Category == ThemeCategory.None || theme.Categories.HasFlag(Category))
        && (Light is not { } light || theme.IsLight == light);
}

/// <summary>Sorts a theme into <see cref="ThemeCategory"/> groups. The collection carries no
/// tags, so this reads the palette (background hue and greyness, the saturation and lightness
/// of ANSI 1-6, text contrast) plus a few name keywords for themes a palette can't reveal,
/// such as a terminal's own defaults or a recreated old machine.</summary>
public static partial class ThemeCategories
{
    /// <summary>The categories the theme browsers offer, in display order, with their labels.</summary>
    public static IReadOnlyList<(ThemeCategory Category, string Name)> All { get; } =
    [
        (ThemeCategory.Traditional, "Traditional"),
        (ThemeCategory.Cool, "Cool"),
        (ThemeCategory.Warm, "Warm"),
        (ThemeCategory.Pastel, "Pastel"),
        (ThemeCategory.Vivid, "Vivid"),
        (ThemeCategory.Retro, "Retro"),
        (ThemeCategory.HighContrast, "High contrast"),
    ];

    public static ThemeCategory Of(string name, ThemePalette palette)
    {
        var background = ColorMath.ToHsl(palette.Background);
        var foreground = ColorMath.ToHsl(palette.Foreground);
        double lightness = 0, saturation = 0;
        for (var i = 1; i <= 6; i++)
        {
            var color = ColorMath.ToHsl(palette.Ansi[i]);
            lightness += color.Lightness / 6;
            saturation += color.Saturation / 6;
        }
        var darkBackground = ColorMath.RelativeLuminance(palette.Background) < 0.5;
        var categories = ThemeCategory.None;

        // Chroma (max - min channel) rather than HSL saturation: a near-black background has
        // a large saturation for a hue no one can see.
        var grey = background.Chroma < 14;
        if (ClassicName().IsMatch(name) || (grey && foreground.Chroma < 30 && saturation > 0.6 && lightness < 0.6))
            categories |= ThemeCategory.Traditional;
        if (!grey && background.Hue is >= 170 and <= 290)
            categories |= ThemeCategory.Cool;
        if (!grey && background.Hue is <= 70 or >= 300)
            categories |= ThemeCategory.Warm;
        if (lightness > 0.66 && saturation > 0.35)
            categories |= ThemeCategory.Pastel;
        if (darkBackground && saturation > 0.9 && lightness is >= 0.4 and <= 0.62)
            categories |= ThemeCategory.Vivid;
        // A bright colored text on near black with muted ANSI colors reads as a phosphor screen.
        if (RetroName().IsMatch(name)
            || (ColorMath.RelativeLuminance(palette.Background) < 0.2 && foreground.Chroma > 80 && saturation < 0.5))
            categories |= ThemeCategory.Retro;
        if (ColorMath.Contrast(palette.Foreground, palette.Background) >= 15)
            categories |= ThemeCategory.HighContrast;
        return categories;
    }

    [GeneratedRegex(@"builtin|xterm|tango|ubuntu|campbell|windows|terminal(\.app)?$|apple system|kitty default|iterm2 (default|dark|light|tango)|konsole|gnome|linux|putty|vscode|default|\bbasic\b|one half|solarized|^resesh ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClassicName();

    [GeneratedRegex(@"retro|vintage|\b[789]0s\b|amber|phosphor|\bcrt\b|c64|commodore|apple ?(ii|classic)|\bibm\b|\bdos\b|\bcga\b|\bega\b|\bvga\b|teletype|matrix|hacker|hax0r|borland|turbo|atari|amiga|spectrum|mainframe|vt ?\d{3}|monochrome|homebrew|green ?screen|synthwave|outrun|1984|man page|\bunix\b|irix|nextstep|\bsgi\b|\btron\b|encom|cassette|vhs|arcade|8.?bit|pixel", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetroName();
}
