using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Resesh.App;

/// <summary>
/// Theme brushes for UI built or recolored in code. <c>Application.Current.Resources[key]</c>
/// answers for the app's RequestedTheme, which can only be set once, at startup; a later
/// theme change sets the window's (or a dialog's) RequestedTheme instead. So a lookup through
/// the application finds the startup theme's brush: start Dark, switch to Light, and the
/// file pane drew near-white text. These look the key up in the theme dictionary of the
/// element's own ActualTheme (High Contrast's when it is on), and set brushes again whenever
/// that theme changes, as a {ThemeResource} in XAML would.
/// </summary>
internal static class ThemeResources
{
    private sealed class Tracked
    {
        public List<(DependencyProperty Property, string Key)> Brushes { get; } = [];
    }

    private static readonly ConditionalWeakTable<FrameworkElement, Tracked> s_tracked = new();

    /// <summary>The brush for this key in the element's theme, or null when none is defined.</summary>
    public static Brush? Brush(FrameworkElement element, string key) =>
        Lookup(element.ActualTheme, key) as Brush;

    /// <summary>Sets a brush property from a theme resource, now and whenever the element's
    /// theme changes or it is loaded into a tree with another theme. A later call for the
    /// same property replaces the key.</summary>
    public static void SetBrush(FrameworkElement element, DependencyProperty property, string key)
    {
        var tracked = s_tracked.GetValue(element, Track);
        tracked.Brushes.RemoveAll(entry => ReferenceEquals(entry.Property, property));
        tracked.Brushes.Add((property, key));
        Apply(element, property, key);
    }

    private static Tracked Track(FrameworkElement element)
    {
        var tracked = new Tracked();
        void Reapply(FrameworkElement sender)
        {
            foreach (var (property, key) in tracked.Brushes)
                Apply(sender, property, key);
        }
        element.ActualThemeChanged += (sender, _) => Reapply(sender);
        element.Loaded += (sender, _) => Reapply((FrameworkElement)sender);
        return tracked;
    }

    private static void Apply(FrameworkElement element, DependencyProperty property, string key)
    {
        if (Brush(element, key) is { } brush)
            element.SetValue(property, brush);
    }

    /// <summary>Searches the app's theme dictionaries (its own, then the merged ones such as
    /// the Fluent control resources, later ones first, as XAML does) for the theme, and falls
    /// back to the plain app lookup for keys that are not themed.</summary>
    private static object? Lookup(ElementTheme theme, string key)
    {
        string[] names = App.IsHighContrast ? ["HighContrast"]
            : theme == ElementTheme.Light ? ["Light"]
            : ["Dark", "Default"];
        var resources = Application.Current.Resources;
        return Find(resources, names, key)
            ?? (resources.TryGetValue(key, out var value) ? value : null);
    }

    private static object? Find(ResourceDictionary dictionary, string[] names, string key)
    {
        foreach (var name in names)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(name, out var themed)
                && themed is ResourceDictionary theme
                && theme.TryGetValue(key, out var value))
            {
                return value;
            }
        }
        for (var index = dictionary.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            if (Find(dictionary.MergedDictionaries[index], names, key) is { } value)
                return value;
        }
        return null;
    }
}
