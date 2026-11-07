using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Storage;
using Resesh.Terminal;
using Windows.UI;

namespace Resesh.App;

/// <summary>
/// Colors every standard WinUI control (text, buttons, inputs, switches, lists, navigation,
/// menus, flyouts, tooltips, dialogs, info bars) from the theme, when the theme colors setting
/// is Everything.
/// <para>
/// Fluent draws with ~1,900 named brushes per light/dark set. Rather than pick some by hand,
/// <see cref="Install"/> copies all of them into a dictionary merged after
/// XamlControlsResources, so the copies win (the app's own theme dictionaries are searched
/// after merged ones, so they would not), and <see cref="Apply"/> recolors each copy from its
/// Fluent original with <see cref="ThemeRetint"/> on every theme change. Copying every name,
/// not only Fluent's base names, matters: control-level names such as ButtonForeground were
/// resolved from the base names inside Fluent's own dictionary and would not follow them.
/// Acrylic surfaces become solid in their fallback color, gradients keep their stops.
/// </para>
/// <para>
/// The originals come from the set for the app's RequestedTheme, the mode it started in, only.
/// Read through code, Fluent's other set holds that mode's colors too (white text in "Light"
/// after a dark start), so copies taken from it turned text white when a dark start switched
/// to a light theme. <see cref="ThemeRetint"/> maps a color from either set onto any theme, so
/// one set serves both modes, and the copies sit at the dictionary's top level, found before
/// any theme dictionary whichever mode an element is drawn in.
/// </para>
/// <para>
/// Adding or removing the dictionary reaches drawn controls only when their theme resources
/// resolve again, so the windows re-resolve after <see cref="Install"/>
/// (MainWindow.ApplyThemeToApp). High Contrast never gets the copies.
/// </para>
/// </summary>
internal static class FluentThemeColors
{
    /// <summary>One recolorable copy, a brush or a gradient stop, with its Fluent original
    /// (0xAARRGGBB) from the set <see cref="_originalsDark"/> names.</summary>
    private sealed record Entry(uint Original, bool IsText, SolidColorBrush? Brush, GradientStop? Stop);

    private static readonly List<Entry> Entries = [];

    /// <summary>Whether the originals are Fluent's dark set: the app's RequestedTheme when the
    /// copies were made.</summary>
    private static bool _originalsDark;

    /// <summary>The app's own fixed light/dark brushes (App.xaml's theme dictionaries) that
    /// stand for a theme surface or the accent rather than a status, with the role each takes.
    /// Status colors (success, caution, critical, recording, the info blue) keep their meaning.</summary>
    private static readonly (string Key, Func<ThemeVisualPalette, Color> Role)[] AppBrushes =
    [
        ("SessionSurfaceBrush", palette => palette.Chrome),
        ("SessionFrameBrush", palette => palette.Frame),
        ("SessionDividerBrush", palette => palette.Divider),
        ("SessionSplitterHoverBrush", palette => palette.Accent),
    ];

    private static readonly Dictionary<string, SolidColorBrush> AppCopies =
        AppBrushes.ToDictionary(entry => entry.Key, _ => new SolidColorBrush());

    /// <summary>The copies, merged after XamlControlsResources while installed; built once.</summary>
    private static ResourceDictionary? _copies;

    private static bool _installed;

    /// <summary>Bumped whenever the copies are added or removed, so each window can tell
    /// whether its theme resources have resolved since.</summary>
    public static int Version { get; private set; }

    /// <summary>Adds or removes the theme's copies, and returns <see cref="Version"/>.</summary>
    public static int Install(bool on)
    {
        if (on == _installed)
            return Version;
        var merged = Application.Current.Resources.MergedDictionaries;
        if (on)
            merged.Add(_copies ??= Copy(merged.OfType<XamlControlsResources>().First()));
        else
            merged.Remove(_copies!);
        _installed = on;
        return ++Version;
    }

    /// <summary>Recolors every copy for the theme drawn with <paramref name="palette"/>.</summary>
    public static void Apply(ThemeVisualPalette palette)
    {
        if (!_installed)
            return;
        var target = Target(palette);
        foreach (var entry in Entries)
        {
            var color = Argb(ThemeRetint.Map(entry.Original, _originalsDark, target, entry.IsText));
            if (entry.Brush is { } brush)
                brush.Color = color;
            else
                entry.Stop!.Color = color;
        }
        foreach (var (key, role) in AppBrushes)
            AppCopies[key].Color = role(palette);
    }

    /// <summary>An app color chosen on Fluent's grey scale (0xAARRGGBB, for its dark or light
    /// set), moved into the theme like Fluent's own while "Everything" is on; as is otherwise.</summary>
    public static Color Retint(uint argb, bool fluentDark, ThemeVisualPalette palette) =>
        !_installed || palette.IsHighContrast
            ? Argb(argb)
            : Argb(ThemeRetint.Map(argb, fluentDark, Target(palette)));

    private static RetintTarget Target(ThemeVisualPalette palette)
    {
        var surface = RgbColor.ToRgb(palette.Shell);
        return new RetintTarget(surface, ColorMath.Readable(RgbColor.ToRgb(palette.TreeForeground), surface, 4.5),
            RgbColor.ToRgb(palette.Accent));
    }

    private static ResourceDictionary Copy(XamlControlsResources fluent)
    {
        _originalsDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        var originals = new Dictionary<object, object>();
        Collect(fluent, _originalsDark, originals);
        var copies = new ResourceDictionary();
        foreach (var (key, value) in originals)
        {
            var isText = IsText(key);
            if (Brush(value) is { } solid)
            {
                var brush = new SolidColorBrush { Opacity = solid.Opacity };
                Entries.Add(new Entry(Argb(solid.Color), isText, brush, null));
                copies[key] = brush;
            }
            else if (value is LinearGradientBrush gradient)
            {
                var stops = new GradientStopCollection();
                foreach (var stop in gradient.GradientStops)
                {
                    var copyStop = new GradientStop { Offset = stop.Offset };
                    Entries.Add(new Entry(Argb(stop.Color), isText, null, copyStop));
                    stops.Add(copyStop);
                }
                copies[key] = new LinearGradientBrush
                {
                    StartPoint = gradient.StartPoint,
                    EndPoint = gradient.EndPoint,
                    MappingMode = gradient.MappingMode,
                    RelativeTransform = gradient.RelativeTransform,
                    Opacity = gradient.Opacity,
                    GradientStops = stops,
                };
            }
        }
        foreach (var (key, brush) in AppCopies)
            copies[key] = brush;
        return copies;
    }

    /// <summary>A brush's color as a solid one: acrylic becomes its fallback color, so menus and
    /// flyouts are solid theme surfaces rather than tinted by whatever sits behind the window.</summary>
    private static (Color Color, double Opacity)? Brush(object value) => value switch
    {
        SolidColorBrush solid => (solid.Color, solid.Opacity),
        AcrylicBrush acrylic => (acrylic.FallbackColor, acrylic.Opacity),
        _ => null,
    };

    private static bool IsText(object key) => key is string name
        && (name.Contains("Foreground", StringComparison.Ordinal) || name.Contains("Text", StringComparison.Ordinal)
            || name.Contains("Glyph", StringComparison.Ordinal));

    /// <summary>Every brush Fluent defines for one light/dark set. Per-control resources sit in
    /// nested merged dictionaries as well as XamlControlsResources' own theme dictionaries.</summary>
    private static void Collect(ResourceDictionary dictionary, bool dark, Dictionary<object, object> into)
    {
        // Fluent keys its dark set "Default" (sometimes "Dark"); its light set "Light".
        foreach (var name in dark ? new[] { "Default", "Dark" } : ["Light"])
        {
            if (dictionary.ThemeDictionaries.TryGetValue(name, out var themed) && themed is ResourceDictionary set)
                foreach (var (key, value) in set)
                    into[key] = value;
        }
        foreach (var merged in dictionary.MergedDictionaries)
            Collect(merged, dark, into);
    }

    private static uint Argb(Color color) => (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);

    private static Color Argb(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
