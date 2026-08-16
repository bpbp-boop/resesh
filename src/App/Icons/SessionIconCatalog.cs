using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Sessions.Core.Models;

namespace Sessions.App.Icons;

/// <summary>One entry in the icon picker: a key plus its display name and image.
/// Key is null for the "Auto-detect" choice and <see cref="SessionIcons.None"/> for "No icon".</summary>
public sealed record IconChoice(string? Key, string Name, ImageSource? Image, string Glyph = "");

/// <summary>
/// Resolves icon keys to renderable images: built-in badges bundled under
/// Assets\SessionIcons (SVG or PNG), plus user files dropped into
/// %APPDATA%\Sessions\icons\ (icon key = filename). Loaded images are cached and
/// shared by every view; UI-thread only.
/// </summary>
public sealed class SessionIconCatalog
{
    /// <summary>Logical display sizes; must match the Image Width/Height in the XAML that
    /// shows them, or the exact-size rasterization guarantee is lost.</summary>
    public const double ListIconSize = 16;
    public const double PickerTileSize = 24;

    private static readonly string[] CustomExtensions = [".svg", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp"];

    // Cached per (key, physical pixel size): each display surface gets a bitmap rendered at
    // its exact on-screen size, so the GPU never resamples (its bilinear-only minification
    // is what caused visible aliasing when one 72 px raster served every surface).
    private readonly Dictionary<(string Key, int Pixels), ImageSource> _cache = [];

    /// <summary>Supplies the window's XamlRoot.RasterizationScale (e.g. 1.5 at 150% DPI);
    /// set by the main window. Until it's available, images render at scale 1 and are
    /// re-rasterized crisply on the next fetch once the real scale is known.</summary>
    public Func<double>? ScaleProvider { get; set; }

    private int PhysicalPixels(double logicalSize)
    {
        var scale = ScaleProvider?.Invoke() ?? 1.0;
        if (double.IsNaN(scale) || scale <= 0)
            scale = 1.0;
        return (int)Math.Ceiling(logicalSize * scale);
    }

    public static string CustomIconsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sessions", "icons");

    private static string BuiltInDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "SessionIcons");

    /// <summary>The image for an icon key rendered for a display size in logical pixels
    /// (16 in the tree/tab strip, 24 in the picker), or null for no/unknown icon (callers
    /// fall back to the default glyph).</summary>
    public ImageSource? GetImage(string? key, double logicalSize)
    {
        if (string.IsNullOrEmpty(key) || key == SessionIcons.None)
            return null;
        var cacheKey = (key.ToLowerInvariant(), PhysicalPixels(logicalSize));
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;
        var source = Load(key, cacheKey.Item2);
        if (source is not null)
            _cache[cacheKey] = source;
        return source;
    }

    /// <summary>
    /// The bundled badge for an agent identity (Phase 6.2), or null when the key names no
    /// agent. Agent icons live in their own directory and their own cache namespace: they
    /// answer "what is running here", never "what is this host", so the two sets must not
    /// be able to resolve each other's keys.
    /// </summary>
    public ImageSource? GetAgentImage(string? key, double logicalSize)
    {
        if (!Sessions.Core.Agents.AgentIdentities.IsAgentKey(key))
            return null;
        var cacheKey = ("agent:" + key!.ToLowerInvariant(), PhysicalPixels(logicalSize));
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AgentIcons", key.ToLowerInvariant() + ".svg");
            if (!File.Exists(path))
                return null;
            var source = LoadFile(path, cacheKey.Item2);
            _cache[cacheKey] = source;
            return source;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or UriFormatException)
        {
            return null;
        }
    }

    private static ImageSource? Load(string key, int pixels)
    {
        try
        {
            if (SessionIcons.IsBuiltIn(key))
            {
                foreach (var ext in (string[])[".svg", ".png"])
                {
                    var path = Path.Combine(BuiltInDirectory, key + ext);
                    if (File.Exists(path))
                        return LoadFile(path, pixels);
                }
                return null;
            }

            // Custom keys are bare filenames; GetFileName guards against path segments.
            var file = Path.Combine(CustomIconsDirectory, Path.GetFileName(key));
            if (File.Exists(file) && CustomExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                return LoadFile(file, pixels);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or UriFormatException)
        {
        }
        return null;
    }

    private static ImageSource LoadFile(string path, int pixels) =>
        Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)
            // SVG rasterizes directly at target resolution; PNG downscales in WIC's
            // high-quality Fant scaler at decode time. Neither is resampled at draw.
            ? new SvgImageSource(new Uri(path)) { RasterizePixelWidth = pixels, RasterizePixelHeight = pixels }
            : new BitmapImage(new Uri(path)) { DecodePixelWidth = pixels };

    /// <summary>
    /// Everything the picker offers: Auto-detect, No icon, the built-in packs, then custom
    /// files (re-scanned on every call so newly dropped files appear; their cache entries
    /// are refreshed in case a file was replaced).
    /// </summary>
    public List<IconChoice> PickerEntries()
    {
        var entries = new List<IconChoice>
        {
            new(null, "Auto-detect (suggested from the server on first connect)", null, ""),
            new(SessionIcons.None, "No icon", null, ""),
        };

        foreach (var info in SessionIcons.BuiltIn)
            entries.Add(new IconChoice(info.Key, info.Name, GetImage(info.Key, PickerTileSize)));

        try
        {
            if (Directory.Exists(CustomIconsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(CustomIconsDirectory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (!CustomExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        continue;
                    var key = Path.GetFileName(file);
                    foreach (var stale in _cache.Keys.Where(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
                        _cache.Remove(stale);
                    entries.Add(new IconChoice(key, Path.GetFileNameWithoutExtension(file), GetImage(key, PickerTileSize)));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return entries;
    }

    /// <summary>Clears cached custom images after a backup import replaces icon files.</summary>
    public void InvalidateCustomIcons()
    {
        foreach (var key in _cache.Keys.Where(k => !SessionIcons.IsBuiltIn(k.Key)).ToList())
            _cache.Remove(key);
    }
}
