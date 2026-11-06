using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Storage;
using Resesh.Terminal;
using Windows.UI.ViewManagement;

namespace Resesh.App;

internal sealed record ThemeVisualPalette(
    Windows.UI.Color Shell,
    Windows.UI.Color Chrome,
    Windows.UI.Color Input,
    Windows.UI.Color Frame,
    Windows.UI.Color Divider,
    Windows.UI.Color ActiveTab,
    Windows.UI.Color HoverTab,
    Windows.UI.Color InactiveTab,
    Windows.UI.Color TreeForeground,
    Windows.UI.Color TreeMutedForeground,
    Windows.UI.Color TreeSelection,
    Windows.UI.Color TreeSelectionForeground,
    Windows.UI.Color Accent,
    double AccentBarThickness,
    Windows.UI.Color PaneBorder,
    Windows.UI.Color PaneFocusBorder,
    bool IsHighContrast)
{
    /// <summary>Placeholder text in entry fields. The muted caption color sits only a third
    /// of the way from the text toward the surface, which reads as a typed value; this goes
    /// nearly halfway, still about 3:1 on the input surface. High Contrast keeps the system text.</summary>
    public Windows.UI.Color Placeholder => IsHighContrast
        ? TreeForeground
        : RgbColor.ToColor(ColorMath.Mix(RgbColor.ToRgb(TreeForeground), RgbColor.ToRgb(Input), 0.45));

    /// <summary>The chrome for a resolved theme id. Background, text and selection come from
    /// the theme's terminal palette; <see cref="ThemeChoice.Chrome"/> adds the rest.</summary>
    public static ThemeVisualPalette For(string id)
    {
        if (App.IsHighContrast)
            return HighContrast();

        var theme = ThemeCatalog.Find(id);
        var terminal = theme.Palette;
        var chrome = theme.Chrome;
        // Built-ins have a hand-tuned accent or deliberately none; collection themes take one
        // from their palette unless the user turned theme colors off.
        var accent = chrome.Accent
            ?? (!theme.IsBuiltIn && ThemeColorModes.Normalize(App.Settings.Current.ThemeColors) != ThemeColorModes.Off
                ? ThemeAccent.Pick(terminal)
                : null);
        var active = chrome.ActiveTab ?? terminal.Background;
        var strip = chrome.Strip;
        var divider = chrome.Divider;
        // The surface ramp. Secondary chrome (the status bar) lifts just off the shell, and
        // the accent stays the stock Windows blue on a hairline bar unless the theme sets both.
        static Windows.UI.Color Blend(uint first, uint second) => RgbColor.ToColor(ColorMath.Mix(first, second, 1 / 3.0));
        return new(
            Shell: RgbColor.ToColor(chrome.Shell ?? strip),
            Chrome: chrome.Chrome is { } surface ? RgbColor.ToColor(surface) : Blend(strip, divider),
            Input: chrome.Input is { } input ? RgbColor.ToColor(input) : Blend(active, strip),
            Frame: RgbColor.ToColor(divider),
            Divider: RgbColor.ToColor(divider),
            ActiveTab: RgbColor.ToColor(active),
            HoverTab: chrome.HoverTab is { } hover ? RgbColor.ToColor(hover) : Blend(active, divider),
            InactiveTab: RgbColor.ToColor(chrome.InactiveTab ?? strip),
            TreeForeground: RgbColor.ToColor(terminal.Foreground),
            TreeMutedForeground: chrome.TreeMutedForeground is { } muted
                ? RgbColor.ToColor(muted)
                : Blend(terminal.Foreground, strip),
            TreeSelection: RgbColor.ToColor(terminal.Selection),
            TreeSelectionForeground: RgbColor.ToColor(terminal.SelectionForeground ?? terminal.Foreground),
            Accent: accent is { } picked ? RgbColor.ToColor(picked) : DefaultAccent,
            AccentBarThickness: chrome.AccentBarThickness,
            PaneBorder: RgbColor.ToColor(chrome.PaneBorder ?? divider),
            PaneFocusBorder: RgbColor.ToColor(chrome.PaneFocusBorder ?? terminal.Selection),
            IsHighContrast: false);
    }

    /// <summary>The stock Windows blue, for themes without an accent of their own. Defined
    /// once in App.xaml, where the splitter and status brushes use it too.</summary>
    private static Windows.UI.Color DefaultAccent =>
        _defaultAccent ??= (Windows.UI.Color)Application.Current.Resources["SessionDefaultAccentColor"];

    private static Windows.UI.Color? _defaultAccent;

    /// <summary>Recolors the app-wide brushes custom chrome binds to. App.xaml declares them
    /// without colors: the app applies the saved theme here before the first window opens,
    /// so no second copy of a palette sits in XAML to drift from this one.</summary>
    public void ApplyToAppResources()
    {
        var resources = Application.Current.Resources;
        void Set(string key, Windows.UI.Color color) => ((SolidColorBrush)resources[key]).Color = color;
        Set("SessionShellBrush", Shell);
        Set("SessionPageBrush", ActiveTab);
        Set("SessionChromeBrush", Chrome);
        Set("SessionAccentBrush", Accent);
        Set("SessionInputBrush", Input);
        Set("SessionChromeFrameBrush", Frame);
        Set("SessionTreeForegroundBrush", TreeForeground);
        Set("SessionTreeMutedForegroundBrush", TreeMutedForeground);
        Set("SessionPlaceholderForegroundBrush", Placeholder);
        Set("SessionTreeSelectionBrush", TreeSelection);
        Set("SessionTreeSelectionForegroundBrush", TreeSelectionForeground);
        Set("SettingsCardBorderBrush", Frame);
        Set("SettingsCardBackgroundBrush", Input);
        FluentThemeColors.Apply(this);
    }

    private static ThemeVisualPalette HighContrast()
    {
        var ui = new UISettings();
        var window = SystemColor("SystemColorWindowColor", ui.GetColorValue(UIColorType.Background));
        var text = SystemColor("SystemColorWindowTextColor", ui.GetColorValue(UIColorType.Foreground));
        var highlight = SystemColor("SystemColorHighlightColor", ui.GetColorValue(UIColorType.Accent));
        var highlightText = SystemColor("SystemColorHighlightTextColor",
            ColorMath.PrefersBlackText(RgbColor.ToRgb(highlight)) ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
        return new(window, window, window, text, text, window, highlight, window,
            text, text, highlight, highlightText, highlight, 2, text, highlight, true);
    }

    private static Windows.UI.Color SystemColor(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value))
        {
            if (value is Windows.UI.Color color)
                return color;
            if (value is SolidColorBrush brush)
                return brush.Color;
        }
        return fallback;
    }
}
