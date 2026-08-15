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
    private static readonly string[] CustomExtensions = [".svg", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp"];

    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static string CustomIconsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sessions", "icons");

    private static string BuiltInDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "SessionIcons");

    /// <summary>The image for an icon key, or null for no/unknown icon (callers fall back
    /// to the default glyph).</summary>
    public ImageSource? GetImage(string? key)
    {
        if (string.IsNullOrEmpty(key) || key == SessionIcons.None)
            return null;
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        var source = Load(key);
        if (source is not null)
            _cache[key] = source;
        return source;
    }

    private static ImageSource? Load(string key)
    {
        try
        {
            if (SessionIcons.IsBuiltIn(key))
            {
                foreach (var ext in (string[])[".svg", ".png"])
                {
                    var path = Path.Combine(BuiltInDirectory, key + ext);
                    if (File.Exists(path))
                        return LoadFile(path);
                }
                return null;
            }

            // Custom keys are bare filenames; GetFileName guards against path segments.
            var file = Path.Combine(CustomIconsDirectory, Path.GetFileName(key));
            if (File.Exists(file) && CustomExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                return LoadFile(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or UriFormatException)
        {
        }
        return null;
    }

    private static ImageSource LoadFile(string path) =>
        Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)
            // Rasterize above display size (16-36px) so downscaling stays crisp.
            ? new SvgImageSource(new Uri(path)) { RasterizePixelWidth = 72, RasterizePixelHeight = 72 }
            : new BitmapImage(new Uri(path)) { DecodePixelWidth = 72 };

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
            entries.Add(new IconChoice(info.Key, info.Name, GetImage(info.Key)));

        try
        {
            if (Directory.Exists(CustomIconsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(CustomIconsDirectory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (!CustomExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        continue;
                    var key = Path.GetFileName(file);
                    _cache.Remove(key);
                    entries.Add(new IconChoice(key, Path.GetFileNameWithoutExtension(file), GetImage(key)));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return entries;
    }
}
