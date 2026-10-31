using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Icons;

/// <summary>One icon file a user dropped into the custom icons folder: its key (the file name)
/// and the name the picker shows.</summary>
public sealed record CustomIcon(string Key, string Name);

/// <summary>
/// Resolves session icon keys to images. Built-in icons are one-colour marks: the tree draws
/// their 64 px PNGs, and everything else draws their SVGs through <see cref="BrandIconImages"/>.
/// Custom icons are files the user dropped into %APPDATA%\Resesh\icons\ (icon key = filename),
/// shown as they are. Loaded images are cached and shared by every view; UI-thread only.
/// </summary>
public sealed class SessionIconCatalog
{
    /// <summary>Logical display size; must match the Image Width/Height in the XAML that
    /// shows it, or the exact-size rasterization guarantee is lost.</summary>
    public const double ListIconSize = 16;

    private static readonly string[] CustomExtensions = [".svg", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp"];

    /// <summary>Tree icon file per built-in key: the device glyphs and every brand mark.</summary>
    private static readonly IReadOnlyDictionary<string, string> TreeIconFiles =
        SessionIcons.TreeIcons.ToDictionary(icon => icon.Key, icon => icon.File, StringComparer.OrdinalIgnoreCase);

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

    public static string CustomIconsDirectory { get; } =
        Program.StorePath("icons", AppDataPaths.Roaming("icons"));

    private static string TreeIconDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "SessionTreeIcons");

    /// <summary>A custom icon's image rendered for a display size in logical pixels, or null
    /// for a built-in key (drawn one-colour by <see cref="BrandIconImages"/>), no icon, or a
    /// missing file.</summary>
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

    /// <summary>The monochrome tree icon URI for an icon key, or null for the default glyph.
    /// Built-ins use transparent one-color assets; custom images use a generic terminal
    /// glyph so arbitrary image colors and backgrounds never leak into the session tree.</summary>
    public Uri? GetTreeUri(string? key)
    {
        if (string.IsNullOrEmpty(key) || key == SessionIcons.None)
            return null;
        try
        {
            var filename = TreeIconFiles.TryGetValue(key, out var builtIn)
                ? builtIn
                : ResolvePath(key) is not null ? "terminal.png" : null;
            if (filename is null)
                return null;
            var path = Path.Combine(TreeIconDirectory, filename);
            return File.Exists(path) ? new Uri(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The bundled badge for an agent identity (Phase 6.2), or null when the key names no
    /// agent. Agent icons live in their own directory and their own cache namespace: they
    /// answer "what is running here", never "what is this host", so the two sets must not
    /// be able to resolve each other's keys.
    /// </summary>
    public ImageSource? GetAgentImage(string? key, double logicalSize)
    {
        if (!Resesh.Core.Agents.AgentIdentities.IsAgentKey(key))
            return null;
        var cacheKey = ("agent:" + key!.ToLowerInvariant(), PhysicalPixels(logicalSize));
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;
        try
        {
            // SVG where the brand publishes one; a few (Aider, Maki) only ship PNG icons.
            var stem = Path.Combine(AppContext.BaseDirectory, "Assets", "AgentIcons", key.ToLowerInvariant());
            var path = File.Exists(stem + ".svg") ? stem + ".svg" : stem + ".png";
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
            return ResolvePath(key) is { } path ? LoadFile(path, pixels) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>A custom icon's file, or null for a built-in key or a missing file.</summary>
    private static string? ResolvePath(string key)
    {
        if (SessionIcons.IsBuiltIn(key))
            return null;

        // Custom keys are bare filenames; GetFileName guards against path segments.
        var file = Path.Combine(CustomIconsDirectory, Path.GetFileName(key));
        return File.Exists(file) && CustomExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
            ? file
            : null;
    }

    private static ImageSource LoadFile(string path, int pixels) =>
        Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)
            // SVG rasterizes directly at target resolution; PNG downscales in WIC's
            // high-quality Fant scaler at decode time. Neither is resampled at draw.
            ? new SvgImageSource(new Uri(path)) { RasterizePixelWidth = pixels, RasterizePixelHeight = pixels }
            : new BitmapImage(new Uri(path)) { DecodePixelWidth = pixels };

    /// <summary>The icon files in the custom icons folder, re-scanned on every call so newly
    /// dropped files appear; their cached images are refreshed in case a file was replaced.</summary>
    public List<CustomIcon> CustomIcons()
    {
        var icons = new List<CustomIcon>();
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
                    icons.Add(new CustomIcon(key, Path.GetFileNameWithoutExtension(file)));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return icons;
    }

    /// <summary>Clears cached custom images after a backup import replaces icon files.</summary>
    public void InvalidateCustomIcons()
    {
        foreach (var key in _cache.Keys.Where(k => !SessionIcons.IsBuiltIn(k.Key)).ToList())
            _cache.Remove(key);
    }
}
