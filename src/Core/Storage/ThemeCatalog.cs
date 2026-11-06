using System.Globalization;

namespace Resesh.Core.Storage;

/// <summary>A theme's terminal colors as 0xRRGGBB: the defaults plus the 16 ANSI colors.
/// <see cref="SelectionForeground"/> null keeps selected text in its own color.</summary>
public sealed record ThemePalette(
    uint Background,
    uint Foreground,
    uint Cursor,
    uint Selection,
    uint? SelectionForeground,
    IReadOnlyList<uint> Ansi);

/// <summary>A theme a user can pick. Resesh's own built-ins carry hand-tuned app chrome
/// (<see cref="TunedChrome"/>); collection themes, from iTerm2-Color-Schemes, derive theirs.</summary>
public sealed record ThemeChoice(string Id, string Name, ThemePalette Palette, ThemeChrome? TunedChrome = null)
{
    /// <summary>One of resesh's own themes, with a hand-tuned accent or deliberately none.</summary>
    public bool IsBuiltIn => TunedChrome is not null;

    /// <summary>The app colors around the terminal.</summary>
    public ThemeChrome Chrome { get; } = TunedChrome ?? ThemeChrome.Derive(Palette);

    /// <summary>Drawn on a light background, so it belongs to light mode.</summary>
    public bool IsLight { get; } = ColorMath.RelativeLuminance(Palette.Background) > 0.5;

    /// <summary>The groups the theme browsers list it under.</summary>
    public ThemeCategory Categories { get; } = ThemeCategories.Of(Name, Palette);

    public override string ToString() => Name;
}

/// <summary>The theme ids code chooses by name. The rest are only ever picked from
/// <see cref="ThemeCatalog.All"/>.</summary>
public static class ThemeIds
{
    public const string Dark = "dark";
    public const string Light = "light";

    /// <summary>New installs, and the fallback for an id this version doesn't know.</summary>
    public const string Default = Dark;
}

/// <summary>Saved values of <see cref="AppSettings.Appearance"/>: which of the light and dark
/// themes the app draws with.</summary>
public static class ThemeAppearances
{
    /// <summary>Follows the Windows color mode.</summary>
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>A saved value this version knows, or <see cref="Dark"/>.</summary>
    public static string Normalize(string? appearance) => appearance is System or Light ? appearance : Dark;

    /// <summary>The theme an appearance draws with, of a light and a dark theme, when Windows
    /// is in light mode or not. The one rule, for saved settings and Welcome's preview.</summary>
    public static string ThemeFor(string? appearance, string? lightTheme, string? darkTheme, bool systemIsLight) =>
        ThemeCatalog.Find(Normalize(appearance) switch
        {
            Light => lightTheme,
            Dark => darkTheme,
            _ => systemIsLight ? lightTheme : darkTheme,
        }).Id;
}

/// <summary>Stable theme identifiers shared by settings and per-session overrides: resesh's
/// built-in themes first, then the bundled collection by name.</summary>
public static class ThemeCatalog
{
    /// <summary>The bundled iTerm2-Color-Schemes themes, written by eng/update-color-schemes.ps1.</summary>
    private const string CollectionResource = "ColorSchemes/color-schemes.tsv";

    public static IReadOnlyList<ThemeChoice> All { get; } = Load();

    private static readonly Dictionary<string, ThemeChoice> ById =
        All.ToDictionary(theme => theme.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly ThemeChoice DefaultChoice = ById[ThemeIds.Default];

    /// <summary>The theme with this id, or the default for an unknown or missing one.</summary>
    public static ThemeChoice Find(string? id) =>
        id is not null && ById.TryGetValue(id, out var theme) ? theme : DefaultChoice;

    public static bool Contains(string? id) => id is not null && ById.ContainsKey(id);

    public static bool IsLight(string? id) => Find(id).IsLight;

    private static List<ThemeChoice> Load()
    {
        var themes = BuiltIn();
        // A collection theme that shares a built-in's id or name is the same scheme; the
        // built-in, with chrome made for it, is the one shown.
        var taken = new HashSet<string>(themes.SelectMany(theme => new[] { theme.Id, theme.Name }),
            StringComparer.OrdinalIgnoreCase);
        using var stream = typeof(ThemeCatalog).Assembly.GetManifestResourceStream(CollectionResource)
            ?? throw new InvalidOperationException($"The {CollectionResource} resource is missing.");
        using var reader = new StreamReader(stream);
        var collection = new List<ThemeChoice>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var theme = ParseCollectionLine(line);
            if (taken.Add(theme.Id))
                collection.Add(theme);
        }
        collection.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        themes.AddRange(collection);
        return themes;
    }

    /// <summary>One collection theme: its name (also its id, as upstream names are unique and
    /// stable), then 21 RRGGBB colors; see the file's header.</summary>
    internal static ThemeChoice ParseCollectionLine(string line)
    {
        var fields = line.Split('\t');
        if (fields.Length != 22)
            throw new InvalidDataException($"A color scheme line has {fields.Length} fields, not 22: {line}");
        uint Color(int index) => uint.Parse(fields[index], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var ansi = new uint[16];
        for (var i = 0; i < ansi.Length; i++)
            ansi[i] = Color(6 + i);
        return new ThemeChoice(fields[0], fields[0], new ThemePalette(Color(1), Color(2), Color(3), Color(4), Color(5), ansi));
    }

    private static List<ThemeChoice> BuiltIn() =>
    [
        New(ThemeIds.Dark, "resesh Dark", new(0x181818, 0x2B2B2B),
            0x0C0C0C, 0xCCCCCC, 0xFFFFFF, 0x264F78,
            0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00,
            0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
            0x767676, 0xE74856, 0x16C60C, 0xF9F1A5,
            0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2),
        New(ThemeIds.Light, "resesh Light", new(0xF3F3F3, 0xD8D8D8),
            0xFFFFFF, 0x383A42, 0x383A42, 0xBFCEFF,
            0x383A42, 0xE45649, 0x50A14F, 0xC18401,
            0x0184BC, 0xA626A4, 0x0997B3, 0xFAFAFA,
            0x4F525E, 0xE06C75, 0x98C379, 0xE5C07B,
            0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF),
        New("solarized-dark", "Solarized Dark", new(0x073642, 0x0B3B46),
            0x002B36, 0x839496, 0x93A1A1, 0x274852,
            0x073642, 0xDC322F, 0x859900, 0xB58900,
            0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
            0x002B36, 0xCB4B16, 0x586E75, 0x657B83,
            0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3),
        New("solarized-light", "Solarized Light", new(0xEEE8D5, 0xE3DCC9),
            0xFDF6E3, 0x657B83, 0x586E75, 0xEEE8D5,
            0x073642, 0xDC322F, 0x859900, 0xB58900,
            0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
            0x002B36, 0xCB4B16, 0x586E75, 0x657B83,
            0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3),
        New("dracula", "Dracula", new(0x21222C, 0x44475A),
            0x282A36, 0xF8F8F2, 0xF8F8F2, 0x44475A,
            0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C,
            0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2,
            0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5,
            0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF),
        New("one-dark", "One Dark", new(0x21252B, 0x3E4451),
            0x282C34, 0xABB2BF, 0x528BFF, 0x3E4451,
            0x282C34, 0xE06C75, 0x98C379, 0xE5C07B,
            0x61AFEF, 0xC678DD, 0x56B6C2, 0xABB2BF,
            0x5C6370, 0xE06C75, 0x98C379, 0xE5C07B,
            0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF),
        New("nord", "Nord", new(0x272C36, 0x4C566A),
            0x2E3440, 0xD8DEE9, 0xD8DEE9, 0x434C5E,
            0x3B4252, 0xBF616A, 0xA3BE8C, 0xEBCB8B,
            0x81A1C1, 0xB48EAD, 0x88C0D0, 0xE5E9F0,
            0x4C566A, 0xBF616A, 0xA3BE8C, 0xEBCB8B,
            0x81A1C1, 0xB48EAD, 0x8FBCBB, 0xECEFF4),
        New("gruvbox-dark", "Gruvbox Dark", new(0x1D2021, 0x504945),
            0x282828, 0xEBDBB2, 0xEBDBB2, 0x504945,
            0x282828, 0xCC241D, 0x98971A, 0xD79921,
            0x458588, 0xB16286, 0x689D6A, 0xA89984,
            0x928374, 0xFB4934, 0xB8BB26, 0xFABD2F,
            0x83A598, 0xD3869B, 0x8EC07C, 0xEBDBB2),
        New("monokai", "Monokai", new(0x20211C, 0x49483E),
            0x272822, 0xF8F8F2, 0xF8F8F0, 0x49483E,
            0x272822, 0xF92672, 0xA6E22E, 0xF4BF75,
            0x66D9EF, 0xAE81FF, 0xA1EFE4, 0xF8F8F2,
            0x75715E, 0xF92672, 0xA6E22E, 0xF4BF75,
            0x66D9EF, 0xAE81FF, 0xA1EFE4, 0xF9F8F5),
        New("tokyo-night", "Tokyo Night", new(0x16161E, 0x414868),
            0x1A1B26, 0xC0CAF5, 0xC0CAF5, 0x33467C,
            0x15161E, 0xF7768E, 0x9ECE6A, 0xE0AF68,
            0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xA9B1D6,
            0x414868, 0xF7768E, 0x9ECE6A, 0xE0AF68,
            0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xC0CAF5),
        New("catppuccin-mocha", "Catppuccin Mocha", new(0x181825, 0x45475A),
            0x1E1E2E, 0xCDD6F4, 0xF5E0DC, 0x45475A,
            0x45475A, 0xF38BA8, 0xA6E3A1, 0xF9E2AF,
            0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xBAC2DE,
            0x585B70, 0xF38BA8, 0xA6E3A1, 0xF9E2AF,
            0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xA6ADC8),
        New("phthalo-green", "Phthalo Green", new(0x0B2118, 0x2D5A48),
            0x123524, 0xD7EEE5, 0x72E0AD, 0x245A46,
            0x081812, 0xFF6B6B, 0x62D796, 0xE6C766,
            0x69A7FF, 0xCF8DF7, 0x59D6C3, 0xD7EEE5,
            0x3B6B58, 0xFF8585, 0x7EE2AA, 0xF1D67B,
            0x86B8FF, 0xDDA8FA, 0x78E2D2, 0xF1FFF9),
        New("vaporwave", "Vaporwave",
            // Cool charcoal surfaces let the pink tab accent and cyan pane focus carry
            // the theme, with a little violet reserved for raised and selected surfaces.
            // The selected tab is that violet rather than the near-black terminal.
            new(0x1D202D, 0x43465F)
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
            0x151925, 0xEEEAF5, 0xFF4FD8, 0x45415E,
            0x29163F, 0xFF6B9D, 0x65F5C4, 0xFFD58A,
            0xA69BFF, 0xFF71E5, 0x55E7EF, 0xEEEAF5,
            0x9675B3, 0xFF9CC4, 0x9BFFDE, 0xFFE9B5,
            0xC5BAFF, 0xFFA1F0, 0x9AFAFF, 0xFFF5FF),
        // A deep wine background that reads as "careful" at a glance (a production
        // session, say), with ANSI red pushed bright enough to stay legible on it.
        New("crimson", "Crimson",
            // The tab takes the terminal's wine red and a bold red accent, so a crimson
            // tab is unmistakable even among tabs drawn with another theme. Inactive and
            // hovered tabs are a clear red rather than the near-black strip, so a production
            // tab still stands out when it isn't selected.
            new(0x1F060A, 0x5A1C26)
            {
                InactiveTab = 0x4A101A,
                HoverTab = 0x5C1622,
                Accent = 0xFF4D5E,
                AccentBarThickness = 3,
                PaneFocusBorder = 0xE0384A,
            },
            0x330B12, 0xF4E4E6, 0xFF6B7A, 0x6B1F2C,
            0x1F060A, 0xFF5C6C, 0x8BD48B, 0xF2C46D,
            0x7FA8F5, 0xE58AD8, 0x6FD3D1, 0xF4E4E6,
            0x9A6C74, 0xFF8792, 0xA8E6A3, 0xFFD98E,
            0xA2C1FF, 0xF2A9E8, 0x95E6E3, 0xFFF5F6),
    ];

    private static ThemeChoice New(string id, string name, ThemeChrome chrome, uint background, uint foreground,
        uint cursor, uint selection, params uint[] ansi)
    {
        if (ansi.Length != 16)
            throw new ArgumentException("A terminal palette must contain 16 ANSI colors.", nameof(ansi));
        return new ThemeChoice(id, name, new ThemePalette(background, foreground, cursor, selection, null, ansi), chrome);
    }
}
