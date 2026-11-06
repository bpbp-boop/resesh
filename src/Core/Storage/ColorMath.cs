namespace Resesh.Core.Storage;

/// <summary>A color's hue (0-360), HSL saturation and lightness (0-1), and chroma, the
/// max-minus-min channel spread (0-255), which unlike saturation stays small for colors too
/// dark or light to look colored.</summary>
public readonly record struct HslColor(double Hue, double Saturation, double Lightness, int Chroma);

/// <summary>Color arithmetic on 0xRRGGBB values, the one copy for the catalog, the theme
/// derivations and the app: WCAG 2 luminance and contrast, mixing, and HSL.</summary>
public static class ColorMath
{
    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double RelativeLuminance(uint rgb)
    {
        static double Channel(uint value)
        {
            var c = (value & 0xFF) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Channel(rgb >> 16)) + (0.7152 * Channel(rgb >> 8)) + (0.0722 * Channel(rgb));
    }

    /// <summary>The WCAG contrast ratio, 1 (none) to 21 (black on white).</summary>
    public static double Contrast(uint first, uint second)
    {
        double a = RelativeLuminance(first), b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>True when black text reads better than white on <paramref name="background"/>
    /// (a tie goes to black).</summary>
    public static bool PrefersBlackText(uint background) =>
        Contrast(0x000000, background) >= Contrast(0xFFFFFF, background);

    /// <summary><paramref name="color"/>, moved toward black or white (whichever reads better on
    /// <paramref name="surface"/>) until it stands out from the surface at <paramref name="minimumContrast"/>.</summary>
    public static uint Readable(uint color, uint surface, double minimumContrast) =>
        Readable(color, surface, minimumContrast, PrefersBlackText(surface) ? 0x000000u : 0xFFFFFFu);

    /// <summary><paramref name="color"/>, moved toward <paramref name="toward"/> in twentieths
    /// until it stands out from <paramref name="surface"/> at <paramref name="minimumContrast"/>;
    /// <paramref name="toward"/> itself when no step does.</summary>
    public static uint Readable(uint color, uint surface, double minimumContrast, uint toward)
    {
        for (var step = 0; step < 20; step++)
        {
            var candidate = Mix(color, toward, step / 20.0);
            if (Contrast(candidate, surface) >= minimumContrast)
                return candidate;
        }
        return toward;
    }

    /// <summary><paramref name="from"/> moved <paramref name="weight"/> (0 to 1) of the way to <paramref name="to"/>.</summary>
    public static uint Mix(uint from, uint to, double weight)
    {
        uint Channel(int shift)
        {
            double a = (from >> shift) & 0xFF, b = (to >> shift) & 0xFF;
            return (uint)Math.Round(a + ((b - a) * weight)) << shift;
        }
        return Channel(16) | Channel(8) | Channel(0);
    }

    public static HslColor ToHsl(uint rgb)
    {
        int r = (int)(rgb >> 16) & 0xFF, g = (int)(rgb >> 8) & 0xFF, b = (int)rgb & 0xFF;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var chroma = max - min;
        var lightness = (max + min) / 510.0;
        if (chroma == 0)
            return new(0, 0, lightness, 0);
        var saturation = chroma / 255.0 / (1 - Math.Abs((2 * lightness) - 1));
        var hue = max == r ? ((g - b) / (double)chroma % 6 + 6) % 6
            : max == g ? ((b - r) / (double)chroma) + 2
            : ((r - g) / (double)chroma) + 4;
        return new(hue * 60, saturation, lightness, chroma);
    }

    public static uint FromHsl(double hue, double saturation, double lightness)
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
