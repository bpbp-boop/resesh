namespace Sessions.App;

internal sealed record ThemeVisualPalette(
    Windows.UI.Color Shell,
    Windows.UI.Color Input,
    Windows.UI.Color Frame,
    Windows.UI.Color Divider,
    Windows.UI.Color ActiveTab,
    Windows.UI.Color HoverTab,
    Windows.UI.Color InactiveTab)
{
    private static Windows.UI.Color Hex(uint rgb) => Windows.UI.Color.FromArgb(
        255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static ThemeVisualPalette For(string id) => id.ToLowerInvariant() switch
    {
        "solarized-dark" => New(0x002B36, 0x073642, 0x0B3B46),
        "solarized-light" => New(0xFDF6E3, 0xEEE8D5, 0xE3DCC9),
        "dracula" => New(0x282A36, 0x21222C, 0x44475A),
        "one-dark" => New(0x282C34, 0x21252B, 0x3E4451),
        "nord" => New(0x2E3440, 0x272C36, 0x4C566A),
        "gruvbox-dark" => New(0x282828, 0x1D2021, 0x504945),
        "monokai" => New(0x272822, 0x20211C, 0x49483E),
        "tokyo-night" => New(0x1A1B26, 0x16161E, 0x414868),
        "catppuccin-mocha" => New(0x1E1E2E, 0x181825, 0x45475A),
        "light" => New(0xFFFFFF, 0xF3F3F3, 0xD8D8D8),
        _ => New(0x0C0C0C, 0x181818, 0x2B2B2B),
    };

    private static ThemeVisualPalette New(uint active, uint inactive, uint divider) =>
        new(Hex(inactive), Blend(active, inactive), Hex(divider), Hex(divider),
            Hex(active), Blend(active, divider), Hex(inactive));

    private static Windows.UI.Color Blend(uint first, uint second) => Windows.UI.Color.FromArgb(
        255,
        (byte)((((first >> 16) & 0xff) * 2 + ((second >> 16) & 0xff)) / 3),
        (byte)((((first >> 8) & 0xff) * 2 + ((second >> 8) & 0xff)) / 3),
        (byte)(((first & 0xff) * 2 + (second & 0xff)) / 3));
}
