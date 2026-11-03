namespace Resesh.App;

/// <summary>WCAG 2 contrast between 0xRRGGBB colours: relative luminance from linearized
/// sRGB, and the (lighter + 0.05) / (darker + 0.05) ratio. Plain numbers, so a headless test
/// can check it; the theme palette and the welcome page both pick text colours with it.</summary>
public static class ColorContrast
{
    public static double RelativeLuminance(uint rgb) =>
        (0.2126 * Linear((byte)(rgb >> 16))) + (0.7152 * Linear((byte)(rgb >> 8))) + (0.0722 * Linear((byte)rgb));

    public static double Ratio(uint first, uint second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05)
            / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    /// <summary>True when black text reads better than white on <paramref name="background"/>
    /// (a tie goes to black).</summary>
    public static bool PrefersBlackText(uint background) =>
        Ratio(0x000000, background) >= Ratio(0xFFFFFF, background);

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
