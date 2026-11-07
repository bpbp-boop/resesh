namespace Resesh.Core.Storage;

/// <summary>
/// What a theme adds to its terminal palette to color the app around it, as 0xRRGGBB.
/// The terminal's background, text and selection colors are not repeated here: the app reads
/// them from <see cref="ThemeChoice.Palette"/>, so the active tab and the session tree always
/// match the terminal they frame. Two colors define the rest of the surface ramp; a theme that
/// needs more sets the optional members, which replace the derived values one for one.
/// Built-in themes are tuned by hand; collection themes take theirs from <see cref="Derive"/>.
/// </summary>
public sealed record ThemeChrome(uint Strip, uint Divider)
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

    /// <summary>Chrome from the terminal palette alone. The strip sits a step darker than the
    /// terminal, as the built-ins' do, except on a near-black background, where it steps
    /// toward the text instead; dividers lie a sixth of the way to the text (darker on light
    /// themes, whose text may be colored).</summary>
    public static ThemeChrome Derive(ThemePalette palette)
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
