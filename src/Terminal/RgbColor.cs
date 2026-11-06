namespace Resesh.Terminal;

/// <summary>The one conversion between 0xRRGGBB values, as themes and highlight rules hold
/// colours, and <see cref="Windows.UI.Color"/>, shared by the terminal and the app.</summary>
public static class RgbColor
{
    /// <summary>0xRRGGBB as an opaque colour.</summary>
    public static Windows.UI.Color ToColor(uint rgb) =>
        Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>A colour as 0xRRGGBB, its alpha dropped.</summary>
    public static uint ToRgb(Windows.UI.Color color) => (uint)(color.R << 16 | color.G << 8 | color.B);
}
