using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Resesh.Core.Models;
using Windows.Storage.Streams;
using Windows.UI;

namespace Resesh.App.Icons;

/// <summary>
/// Brand marks drawn sharp at the size they are shown, in one colour. The session tree tints
/// 64 px PNGs, which blur when shrunk to 16–24 px; here each mark's SVG is recoloured and
/// rasterised at the exact physical size instead, the way the coloured session badges are.
/// Cached per (key, pixels, colour); UI-thread only.
/// </summary>
public sealed class BrandIconImages
{
    private static string Directory => Path.Combine(AppContext.BaseDirectory, "Assets", "SessionTreeIcons");

    private readonly Dictionary<(string Key, int Pixels, Color Color), ImageSource> _cache = [];
    private readonly Dictionary<string, string?> _svgText = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Supplies the window's rasterization scale; until then images draw at scale 1.</summary>
    public Func<double>? ScaleProvider { get; set; }

    /// <summary>The brand mark for a key at a logical size in a colour, or null for a key that
    /// is not a brand or whose file is missing.</summary>
    public ImageSource? Get(string key, double logicalSize, Color color)
    {
        if (key.Length == 0)
            return null;
        var scale = ScaleProvider?.Invoke() ?? 1.0;
        var pixels = (int)Math.Ceiling(logicalSize * (double.IsNaN(scale) || scale <= 0 ? 1.0 : scale));
        var cacheKey = (key.ToLowerInvariant(), pixels, color);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;
        if (Svg(key) is not { } svg)
            return null;

        var source = new SvgImageSource { RasterizePixelWidth = pixels, RasterizePixelHeight = pixels };
        _ = LoadAsync(source, Tint(svg, color));
        _cache[cacheKey] = source;
        return source;
    }

    private static async Task LoadAsync(SvgImageSource source, string svg)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteString(svg);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await source.SetSourceAsync(stream);
        }
        catch (Exception e) when (e is IOException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // A mark that will not load draws nothing; the button still has its label.
        }
    }

    private string? Svg(string key)
    {
        if (_svgText.TryGetValue(key, out var known))
            return known;
        string? text = null;
        if (SessionIcons.TreeIcons.FirstOrDefault(brand => brand.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } brand)
        {
            try
            {
                var path = Path.Combine(Directory, Path.ChangeExtension(brand.File, ".svg"));
                text = File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                text = null;
            }
        }
        _svgText[key] = text;
        return text;
    }

    /// <summary>Paints a mark in one colour. Simple Icons and the traced vendor marks are filled
    /// shapes that inherit the root's fill; the Lucide glyphs stroke in currentColor.</summary>
    internal static string Tint(string svg, Color color)
    {
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        svg = svg.Replace("currentColor", hex, StringComparison.Ordinal);
        var start = svg.IndexOf("<svg", StringComparison.Ordinal);
        if (start < 0)
            return svg;
        var end = svg.IndexOf('>', start);
        var root = svg[start..end];
        if (root.Contains(" fill=", StringComparison.Ordinal))
            return svg;
        return svg[..(start + 4)] + $" fill=\"{hex}\"" + svg[(start + 4)..];
    }
}
