using Resesh.Core.Storage;

namespace Resesh.App;

/// <summary>
/// What a theme adds to its terminal palette to color the app around it, as 0xRRGGBB.
/// The terminal's background, text and selection colors are not repeated here:
/// <c>ThemeVisualPalette</c> reads them from <c>TerminalThemes</c>, so the active tab
/// and the session tree always match the terminal they frame. Two colors define the rest of
/// the surface ramp; a theme that needs more sets the optional members, which replace the
/// derived values one for one. Plain data, so a headless test can check that every theme in
/// the catalog has an entry.
/// </summary>
internal sealed record ThemeChrome(uint Strip, uint Divider)
{
    /// <summary>Null: the strip color.</summary>
    public uint? InactiveTab { get; init; }
    public uint? Shell { get; init; }
    public uint? Chrome { get; init; }
    public uint? Input { get; init; }

    /// <summary>Null: the terminal background, so the selected tab and its terminal read as one surface.</summary>
    public uint? ActiveTab { get; init; }
    public uint? HoverTab { get; init; }
    public uint? TreeMutedForeground { get; init; }

    /// <summary>Null: the app's default accent (SessionDefaultAccentColor in App.xaml).</summary>
    public uint? Accent { get; init; }
    public double AccentBarThickness { get; init; } = 1;
    public uint? PaneBorder { get; init; }
    public uint? PaneFocusBorder { get; init; }
}

internal static class ThemeChromes
{
    /// <summary>One entry per built-in theme in <see cref="ThemeCatalog"/>, tuned by hand.
    /// Collection themes take theirs from <see cref="Derive"/>.</summary>
    public static IReadOnlyDictionary<string, ThemeChrome> ById { get; } =
        new Dictionary<string, ThemeChrome>(StringComparer.OrdinalIgnoreCase)
        {
            [ThemeIds.Dark] = new(0x181818, 0x2B2B2B),
            [ThemeIds.Light] = new(0xF3F3F3, 0xD8D8D8),
            ["solarized-dark"] = new(0x073642, 0x0B3B46),
            ["solarized-light"] = new(0xEEE8D5, 0xE3DCC9),
            ["dracula"] = new(0x21222C, 0x44475A),
            ["one-dark"] = new(0x21252B, 0x3E4451),
            ["nord"] = new(0x272C36, 0x4C566A),
            ["gruvbox-dark"] = new(0x1D2021, 0x504945),
            ["monokai"] = new(0x20211C, 0x49483E),
            ["tokyo-night"] = new(0x16161E, 0x414868),
            ["catppuccin-mocha"] = new(0x181825, 0x45475A),
            ["phthalo-green"] = new(0x0B2118, 0x2D5A48),
            // Cool charcoal surfaces let the pink tab accent and cyan pane focus carry
            // the theme, with a little violet reserved for raised and selected surfaces.
            // The selected tab is that violet rather than the near-black terminal.
            ["vaporwave"] = new(0x1D202D, 0x43465F)
            {
                Shell = 0x272A3B,
                Chrome = 0x1D202D,
                Input = 0x151925,
                ActiveTab = 0x34334B,
                HoverTab = 0x3B3D53,
                TreeMutedForeground = 0xAFB2C8,
                Accent = 0xFF4FD8,
                AccentBarThickness = 3,
                PaneBorder = 0x43465F,
                PaneFocusBorder = 0x55E7EF,
            },
            // The tab takes the terminal's wine red and a bold red accent, so a crimson
            // tab is unmistakable even among tabs drawn with another theme. Inactive and
            // hovered tabs are a clear red rather than the near-black strip, so a production
            // tab still stands out when it isn't selected.
            ["crimson"] = new(0x1F060A, 0x5A1C26)
            {
                InactiveTab = 0x4A101A,
                HoverTab = 0x5C1622,
                Accent = 0xFF4D5E,
                AccentBarThickness = 3,
                PaneFocusBorder = 0xE0384A,
            },
        };

    /// <summary>The chrome for <paramref name="id"/>, or the default theme's for an unknown id.</summary>
    public static ThemeChrome Find(string? id)
    {
        var theme = ThemeCatalog.Find(id);
        return ById.TryGetValue(theme.Id, out var chrome) ? chrome : Derive(theme.Palette);
    }

    /// <summary>Chrome from the terminal palette alone. The strip sits a step darker than the
    /// terminal, as the built-ins' do, except on a near-black background, where it steps
    /// toward the text instead; dividers lie a sixth of the way to the text (darker on light
    /// themes, whose text may be colored).</summary>
    internal static ThemeChrome Derive(ThemePalette palette)
    {
        var background = palette.Background;
        if (ColorMath.RelativeLuminance(background) > 0.5)
            return new(ColorMath.Mix(background, 0x000000, 0.05), ColorMath.Mix(background, 0x000000, 0.15));
        var strip = ColorMath.RelativeLuminance(background) < 0.01
            ? ColorMath.Mix(background, palette.Foreground, 0.06)
            : ColorMath.Mix(background, 0x000000, 0.25);
        return new(strip, ColorMath.Mix(background, palette.Foreground, 0.16));
    }
}
