using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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
        : Mix(TreeForeground, Input, 0.45);

    private static Windows.UI.Color Mix(Windows.UI.Color from, Windows.UI.Color to, double weight) =>
        Windows.UI.Color.FromArgb(
            255,
            (byte)Math.Round(from.R + ((to.R - from.R) * weight)),
            (byte)Math.Round(from.G + ((to.G - from.G) * weight)),
            (byte)Math.Round(from.B + ((to.B - from.B) * weight)));

    private static Windows.UI.Color Hex(uint rgb) => Windows.UI.Color.FromArgb(
        255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>The chrome for a resolved theme id (never System). Background, text and
    /// selection come from the theme's terminal palette; <see cref="ThemeChromes"/> adds the rest.</summary>
    public static ThemeVisualPalette For(string id)
    {
        if (App.IsHighContrast)
            return HighContrast();

        var terminal = Resesh.Terminal.Native.TerminalThemes.Find(id);
        var chrome = ThemeChromes.Find(id);
        var active = chrome.ActiveTab ?? terminal.Background;
        var strip = chrome.Strip;
        var divider = chrome.Divider;
        // The surface ramp. Secondary chrome (the status bar) lifts just off the shell, and
        // the accent stays the stock Windows blue on a hairline bar unless the theme sets both.
        return new(
            Shell: Hex(chrome.Shell ?? strip),
            Chrome: chrome.Chrome is { } surface ? Hex(surface) : Blend(strip, divider),
            Input: chrome.Input is { } input ? Hex(input) : Blend(active, strip),
            Frame: Hex(divider),
            Divider: Hex(divider),
            ActiveTab: Hex(active),
            HoverTab: chrome.HoverTab is { } hover ? Hex(hover) : Blend(active, divider),
            InactiveTab: Hex(chrome.InactiveTab ?? strip),
            TreeForeground: Hex(terminal.Foreground),
            TreeMutedForeground: chrome.TreeMutedForeground is { } muted
                ? Hex(muted)
                : Blend(terminal.Foreground, strip),
            TreeSelection: Hex(terminal.Selection),
            TreeSelectionForeground: Hex(terminal.Foreground),
            Accent: chrome.Accent is { } accent ? Hex(accent) : DefaultAccent,
            AccentBarThickness: chrome.AccentBarThickness,
            PaneBorder: Hex(chrome.PaneBorder ?? divider),
            PaneFocusBorder: Hex(chrome.PaneFocusBorder ?? terminal.Selection),
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
    }

    private static ThemeVisualPalette HighContrast()
    {
        var ui = new UISettings();
        var window = SystemColor("SystemColorWindowColor", ui.GetColorValue(UIColorType.Background));
        var text = SystemColor("SystemColorWindowTextColor", ui.GetColorValue(UIColorType.Foreground));
        var highlight = SystemColor("SystemColorHighlightColor", ui.GetColorValue(UIColorType.Accent));
        var highlightText = SystemColor("SystemColorHighlightTextColor", Contrast(highlight));
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

    /// <summary>Black or white, whichever has the higher WCAG contrast ratio on
    /// <paramref name="color"/>, as the welcome page picks its text.</summary>
    private static Windows.UI.Color Contrast(Windows.UI.Color color) =>
        ColorContrast.PrefersBlackText((uint)(color.R << 16 | color.G << 8 | color.B)) ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;

    private static Windows.UI.Color Blend(uint first, uint second) => Windows.UI.Color.FromArgb(
        255,
        (byte)((((first >> 16) & 0xff) * 2 + ((second >> 16) & 0xff)) / 3),
        (byte)((((first >> 8) & 0xff) * 2 + ((second >> 8) & 0xff)) / 3),
        (byte)(((first & 0xff) * 2 + (second & 0xff)) / 3));
}
