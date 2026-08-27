using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Resesh.App;

internal sealed record ThemeVisualPalette(
    Windows.UI.Color Shell,
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
    bool IsHighContrast)
{
    private static Windows.UI.Color Hex(uint rgb) => Windows.UI.Color.FromArgb(
        255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static ThemeVisualPalette For(string id)
    {
        if (App.IsHighContrast)
            return HighContrast();

        return id.ToLowerInvariant() switch
        {
        "solarized-dark" => New(0x002B36, 0x073642, 0x0B3B46, 0x839496, 0x274852),
        "solarized-light" => New(0xFDF6E3, 0xEEE8D5, 0xE3DCC9, 0x657B83, 0xEEE8D5),
        "dracula" => New(0x282A36, 0x21222C, 0x44475A, 0xF8F8F2, 0x44475A),
        "one-dark" => New(0x282C34, 0x21252B, 0x3E4451, 0xABB2BF, 0x3E4451),
        "nord" => New(0x2E3440, 0x272C36, 0x4C566A, 0xD8DEE9, 0x434C5E),
        "gruvbox-dark" => New(0x282828, 0x1D2021, 0x504945, 0xEBDBB2, 0x504945),
        "monokai" => New(0x272822, 0x20211C, 0x49483E, 0xF8F8F2, 0x49483E),
        "tokyo-night" => New(0x1A1B26, 0x16161E, 0x414868, 0xC0CAF5, 0x33467C),
        "catppuccin-mocha" => New(0x1E1E2E, 0x181825, 0x45475A, 0xCDD6F4, 0x45475A),
        "phthalo-green" => New(0x123524, 0x0B2118, 0x2D5A48, 0xD7EEE5, 0x245A46),
        "light" => New(0xFFFFFF, 0xF3F3F3, 0xD8D8D8, 0x383A42, 0xBFCEFF),
            _ => New(0x0C0C0C, 0x181818, 0x2B2B2B, 0xCCCCCC, 0x264F78),
        };
    }

    private static ThemeVisualPalette New(
        uint active, uint inactive, uint divider, uint foreground, uint selection) =>
        new(Hex(inactive), Blend(active, inactive), Hex(divider), Hex(divider),
            Hex(active), Blend(active, divider), Hex(inactive),
            Hex(foreground), Blend(foreground, inactive), Hex(selection), Hex(foreground), false);

    private static ThemeVisualPalette HighContrast()
    {
        var ui = new UISettings();
        var window = SystemColor("SystemColorWindowColor", ui.GetColorValue(UIColorType.Background));
        var text = SystemColor("SystemColorWindowTextColor", ui.GetColorValue(UIColorType.Foreground));
        var highlight = SystemColor("SystemColorHighlightColor", ui.GetColorValue(UIColorType.Accent));
        var highlightText = SystemColor("SystemColorHighlightTextColor", Contrast(highlight));
        return new(window, window, text, text, window, highlight, window,
            text, text, highlight, highlightText, true);
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

    private static Windows.UI.Color Contrast(Windows.UI.Color color)
    {
        var luminance = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
        return luminance >= 128 ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
    }

    private static Windows.UI.Color Blend(uint first, uint second) => Windows.UI.Color.FromArgb(
        255,
        (byte)((((first >> 16) & 0xff) * 2 + ((second >> 16) & 0xff)) / 3),
        (byte)((((first >> 8) & 0xff) * 2 + ((second >> 8) & 0xff)) / 3),
        (byte)(((first & 0xff) * 2 + (second & 0xff)) / 3));
}
