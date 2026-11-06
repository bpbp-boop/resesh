namespace Resesh.Core.Storage;

/// <summary>The colors <see cref="ThemeRetint"/> maps Windows' standard control colors onto,
/// as 0xRRGGBB: the app's surface, its text and its accent.</summary>
public readonly record struct RetintTarget(uint Background, uint Foreground, uint Accent);

/// <summary>
/// Moves one of Fluent's standard control colors (as 0xAARRGGBB) into a theme, so every
/// control can follow the theme without a hand-written rule per color.
/// <para>
/// Fluent builds its colors from a few ingredients, and each maps to the theme's version:
/// <list type="bullet">
/// <item>Greys sit on a line from its surface (#202020 dark, #F3F3F3 light) to its text
/// (white dark, black light). The same point on the line from the theme's surface to its text
/// is the theme's version; translucent ones keep their alpha, so a white-at-6% fill becomes
/// the theme's text at 6% over whatever it covers. Greys beyond the surface (deeper shadows,
/// brighter cards) go the same distance past the theme's surface, away from its text.</item>
/// <item>The accent blues become the theme's accent at the blue's own lightness, so hover and
/// pressed steps keep their order.</item>
/// <item>Other colors (status green, amber, red) mean something and stay as they are.</item>
/// </list>
/// </para>
/// </summary>
public static class ThemeRetint
{
    private const uint DarkSurface = 0x202020, DarkText = 0xFFFFFF;
    private const uint LightSurface = 0xF3F3F3, LightText = 0x000000;

    /// <summary>Above this channel spread a color is colored rather than grey.</summary>
    private const int MinimumChroma = 40;

    /// <param name="fluentDark">Whether <paramref name="argb"/> is from Fluent's dark set.</param>
    /// <param name="isText">Text and glyph colors are kept at 3:1 against the theme's surface.</param>
    public static uint Map(uint argb, bool fluentDark, RetintTarget theme, bool isText = false)
    {
        var alpha = argb & 0xFF000000;
        var rgb = argb & 0xFFFFFF;
        var (hue, _, lightness, chroma) = Hsl(rgb);
        uint mapped;
        if (chroma > MinimumChroma)
        {
            // Fluent's accent ramp is blue (#4CC2FF, #99EBFF, #0078D4, #005FB8...).
            if (hue is < 185 or > 225)
                return argb;
            var (accentHue, accentSaturation, _, _) = Hsl(theme.Accent);
            mapped = FromHsl(accentHue, accentSaturation, lightness);
            if (isText)
                mapped = Readable(mapped, theme);
        }
        else
        {
            var surface = fluentDark ? DarkSurface : LightSurface;
            var text = fluentDark ? DarkText : LightText;
            double value = Average(rgb), from = Average(surface), to = Average(text);
            var along = (value - from) / (to - from);
            if (along >= 0)
            {
                mapped = ColorMath.Mix(theme.Background, theme.Foreground, Math.Min(1, along));
            }
            else
            {
                // Past the surface, away from the text: the theme goes the same way.
                var away = fluentDark ? 0.0 : 255.0;
                var past = (value - from) / (away - from);
                var themeAway = ColorMath.RelativeLuminance(theme.Foreground) > ColorMath.RelativeLuminance(theme.Background)
                    ? 0x000000u
                    : 0xFFFFFFu;
                mapped = ColorMath.Mix(theme.Background, themeAway, Math.Clamp(past, 0, 1));
            }
        }
        return alpha | mapped;
    }

    /// <summary>The color pulled toward the theme's text until it stands out from the surface at 3:1.</summary>
    private static uint Readable(uint color, RetintTarget theme)
    {
        for (var weight = 0.0; weight < 1; weight += 0.1)
        {
            var candidate = ColorMath.Mix(color, theme.Foreground, weight);
            if (Contrast(candidate, theme.Background) >= 3)
                return candidate;
        }
        return theme.Foreground;
    }

    private static double Contrast(uint first, uint second)
    {
        double a = ColorMath.RelativeLuminance(first), b = ColorMath.RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Average(uint rgb) => (((rgb >> 16) & 0xFF) + ((rgb >> 8) & 0xFF) + (rgb & 0xFF)) / 3.0;

    private static (double Hue, double Saturation, double Lightness, int Chroma) Hsl(uint rgb)
    {
        int r = (int)(rgb >> 16) & 0xFF, g = (int)(rgb >> 8) & 0xFF, b = (int)rgb & 0xFF;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var chroma = max - min;
        var lightness = (max + min) / 510.0;
        if (chroma == 0)
            return (0, 0, lightness, 0);
        var saturation = chroma / 255.0 / (1 - Math.Abs((2 * lightness) - 1));
        var hue = max == r ? ((g - b) / (double)chroma % 6 + 6) % 6
            : max == g ? ((b - r) / (double)chroma) + 2
            : ((r - g) / (double)chroma) + 4;
        return (hue * 60, saturation, lightness, chroma);
    }

    private static uint FromHsl(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var x = c * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = lightness - (c / 2);
        var (r, g, b) = (hue / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        uint Channel(double v) => (uint)Math.Clamp(Math.Round((v + m) * 255), 0, 255);
        return (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
    }
}
