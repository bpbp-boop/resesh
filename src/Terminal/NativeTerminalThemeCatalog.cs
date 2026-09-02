namespace Resesh.Terminal;

/// <summary>Native COLORREF palettes for every stable terminal theme.</summary>
internal static class NativeTerminalThemeCatalog
{
    private static readonly Dictionary<string, NativeTerminalApi.TerminalTheme> Themes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["dark"] = New(
                0x0C0C0C, 0xCCCCCC, 0xFFFFFF, 0x264F78,
                0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00,
                0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
                0x767676, 0xE74856, 0x16C60C, 0xF9F1A5,
                0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2),
            ["light"] = New(
                0xFFFFFF, 0x383A42, 0x383A42, 0xBFCEFF,
                0x383A42, 0xE45649, 0x50A14F, 0xC18401,
                0x0184BC, 0xA626A4, 0x0997B3, 0xFAFAFA,
                0x4F525E, 0xE06C75, 0x98C379, 0xE5C07B,
                0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF),
            ["system"] = New(
                0x0C0C0C, 0xCCCCCC, 0xFFFFFF, 0x264F78,
                0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00,
                0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
                0x767676, 0xE74856, 0x16C60C, 0xF9F1A5,
                0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2),
            ["solarized-dark"] = New(
                0x002B36, 0x839496, 0x93A1A1, 0x274852,
                0x073642, 0xDC322F, 0x859900, 0xB58900,
                0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
                0x002B36, 0xCB4B16, 0x586E75, 0x657B83,
                0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3),
            ["solarized-light"] = New(
                0xFDF6E3, 0x657B83, 0x586E75, 0xEEE8D5,
                0x073642, 0xDC322F, 0x859900, 0xB58900,
                0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
                0x002B36, 0xCB4B16, 0x586E75, 0x657B83,
                0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3),
            ["dracula"] = New(
                0x282A36, 0xF8F8F2, 0xF8F8F2, 0x44475A,
                0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C,
                0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2,
                0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5,
                0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF),
            ["one-dark"] = New(
                0x282C34, 0xABB2BF, 0x528BFF, 0x3E4451,
                0x282C34, 0xE06C75, 0x98C379, 0xE5C07B,
                0x61AFEF, 0xC678DD, 0x56B6C2, 0xABB2BF,
                0x5C6370, 0xE06C75, 0x98C379, 0xE5C07B,
                0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF),
            ["nord"] = New(
                0x2E3440, 0xD8DEE9, 0xD8DEE9, 0x434C5E,
                0x3B4252, 0xBF616A, 0xA3BE8C, 0xEBCB8B,
                0x81A1C1, 0xB48EAD, 0x88C0D0, 0xE5E9F0,
                0x4C566A, 0xBF616A, 0xA3BE8C, 0xEBCB8B,
                0x81A1C1, 0xB48EAD, 0x8FBCBB, 0xECEFF4),
            ["gruvbox-dark"] = New(
                0x282828, 0xEBDBB2, 0xEBDBB2, 0x504945,
                0x282828, 0xCC241D, 0x98971A, 0xD79921,
                0x458588, 0xB16286, 0x689D6A, 0xA89984,
                0x928374, 0xFB4934, 0xB8BB26, 0xFABD2F,
                0x83A598, 0xD3869B, 0x8EC07C, 0xEBDBB2),
            ["monokai"] = New(
                0x272822, 0xF8F8F2, 0xF8F8F0, 0x49483E,
                0x272822, 0xF92672, 0xA6E22E, 0xF4BF75,
                0x66D9EF, 0xAE81FF, 0xA1EFE4, 0xF8F8F2,
                0x75715E, 0xF92672, 0xA6E22E, 0xF4BF75,
                0x66D9EF, 0xAE81FF, 0xA1EFE4, 0xF9F8F5),
            ["tokyo-night"] = New(
                0x1A1B26, 0xC0CAF5, 0xC0CAF5, 0x33467C,
                0x15161E, 0xF7768E, 0x9ECE6A, 0xE0AF68,
                0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xA9B1D6,
                0x414868, 0xF7768E, 0x9ECE6A, 0xE0AF68,
                0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xC0CAF5),
            ["catppuccin-mocha"] = New(
                0x1E1E2E, 0xCDD6F4, 0xF5E0DC, 0x45475A,
                0x45475A, 0xF38BA8, 0xA6E3A1, 0xF9E2AF,
                0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xBAC2DE,
                0x585B70, 0xF38BA8, 0xA6E3A1, 0xF9E2AF,
                0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xA6ADC8),
            ["phthalo-green"] = New(
                0x123524, 0xD7EEE5, 0x72E0AD, 0x245A46,
                0x081812, 0xFF6B6B, 0x62D796, 0xE6C766,
                0x69A7FF, 0xCF8DF7, 0x59D6C3, 0xD7EEE5,
                0x3B6B58, 0xFF8585, 0x7EE2AA, 0xF1D67B,
                0x86B8FF, 0xDDA8FA, 0x78E2D2, 0xF1FFF9),
            ["vaporwave"] = New(
                0x12101A, 0xE8E4F0, 0xFF2D95, 0x4B2E83,
                0x1F1A2B, 0xFF5C8A, 0x5EF0B7, 0xFFD76E,
                0x63DDFF, 0xFF6AD5, 0x7CF6E8, 0xE8E4F0,
                0x6C6482, 0xFF8AAE, 0x8DFFD0, 0xFFE8A3,
                0x9BEAFF, 0xFF9BE3, 0xA9FBF0, 0xFFFFFF),
        };

    internal static NativeTerminalApi.TerminalTheme Find(string? id) =>
        id is not null && Themes.TryGetValue(id, out var theme)
            ? theme
            : Themes["dark"];

    private static NativeTerminalApi.TerminalTheme New(
        uint background,
        uint foreground,
        uint cursor,
        uint selection,
        params uint[] colorTable)
    {
        if (colorTable.Length != 16)
            throw new ArgumentException("A terminal palette must contain 16 ANSI colors.", nameof(colorTable));

        for (var index = 0; index < colorTable.Length; index++)
            colorTable[index] = ToColorRef(colorTable[index]);

        return new NativeTerminalApi.TerminalTheme
        {
            DefaultBackground = ToColorRef(background),
            DefaultForeground = ToColorRef(foreground),
            DefaultSelectionBackground = ToColorRef(selection),
            CursorColor = ToColorRef(cursor),
            CursorStyle = 5,
            ColorTable = colorTable,
        };
    }

    private static uint ToColorRef(uint rgb) =>
        ((rgb >> 16) & 0xFF) | (rgb & 0x00FF00) | ((rgb & 0xFF) << 16);
}
