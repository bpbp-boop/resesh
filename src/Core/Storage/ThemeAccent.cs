namespace Resesh.Core.Storage;

/// <summary>Saved values of <see cref="AppSettings.ThemeColors"/>.</summary>
public static class ThemeColorModes
{
    /// <summary>The default blue accent and Windows' own control colors.</summary>
    public const string Off = "off";

    /// <summary>The theme's accent (<see cref="ThemeAccent"/>) on resesh's own highlights.</summary>
    public const string Accent = "accent";

    /// <summary>The accent, plus text, controls, menus, flyouts and tooltips from the theme.</summary>
    public const string Everything = "all";

    /// <summary>A saved value this version knows, or <see cref="Accent"/>.</summary>
    public static string Normalize(string? mode) => mode is Off or Everything ? mode : Accent;
}

/// <summary>
/// The accent a theme implies, for focus rings, the selected tab's bar and accent buttons.
/// The cursor comes first: themes often make it their signature color (Synthwave's cyan,
/// Vaporwave's pink). Then the most colorful of ANSI 1-6. A candidate must be colorful and
/// stand out from the background at 3:1 (WCAG's minimum for UI parts); a theme with none,
/// such as a grey or monochrome one, has no accent and the app keeps its default.
/// </summary>
public static class ThemeAccent
{
    /// <summary>Below this max-minus-min channel spread a color reads as grey.</summary>
    private const int MinimumChroma = 60;

    private const double MinimumContrast = 3;

    public static uint? Pick(ThemePalette palette)
    {
        if (Usable(palette.Cursor, palette.Background))
            return palette.Cursor;
        uint? best = null;
        var bestChroma = 0;
        for (var i = 1; i <= 6; i++)
        {
            var color = palette.Ansi[i];
            var chroma = Chroma(color);
            if (chroma > bestChroma && Usable(color, palette.Background))
                (best, bestChroma) = (color, chroma);
        }
        return best;
    }

    private static bool Usable(uint color, uint background) =>
        Chroma(color) >= MinimumChroma && Contrast(color, background) >= MinimumContrast;

    private static int Chroma(uint rgb)
    {
        int r = (int)(rgb >> 16) & 0xFF, g = (int)(rgb >> 8) & 0xFF, b = (int)rgb & 0xFF;
        return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
    }

    private static double Contrast(uint first, uint second)
    {
        double a = ColorMath.RelativeLuminance(first), b = ColorMath.RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
