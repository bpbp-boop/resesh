namespace Resesh.Core.Storage;

public sealed record ThemeChoice(string Id, string Name, bool IsLight = false)
{
    public override string ToString() => Name;
}

/// <summary>The theme ids code chooses by name. The rest are only ever picked from
/// <see cref="ThemeCatalog.All"/>.</summary>
public static class ThemeIds
{
    public const string Dark = "dark";
    public const string Light = "light";

    /// <summary>Follows the Windows color mode; resolved to <see cref="Light"/> or
    /// <see cref="Dark"/> before anything is drawn, so it has no colors of its own.</summary>
    public const string System = "system";

    /// <summary>New installs, and the fallback for an id this version doesn't know.
    /// Resesh.Terminal can't see this, so TerminalThemes keeps a copy that a test checks.</summary>
    public const string Default = Dark;
}

/// <summary>Stable theme identifiers shared by settings and per-session overrides.</summary>
public static class ThemeCatalog
{
    public static IReadOnlyList<ThemeChoice> All { get; } =
    [
        new(ThemeIds.Dark, "resesh Dark"),
        new(ThemeIds.Light, "resesh Light", true),
        new(ThemeIds.System, "System"),
        new("solarized-dark", "Solarized Dark"),
        new("solarized-light", "Solarized Light", true),
        new("dracula", "Dracula"),
        new("one-dark", "One Dark"),
        new("nord", "Nord"),
        new("gruvbox-dark", "Gruvbox Dark"),
        new("monokai", "Monokai"),
        new("tokyo-night", "Tokyo Night"),
        new("catppuccin-mocha", "Catppuccin Mocha"),
        new("phthalo-green", "Phthalo Green"),
        new("vaporwave", "Vaporwave"),
        new("crimson", "Crimson"),
    ];

    private static readonly ThemeChoice DefaultChoice = All.First(theme => theme.Id == ThemeIds.Default);

    /// <summary>The theme with this id, or the default for an unknown or missing one.</summary>
    public static ThemeChoice Find(string? id) =>
        All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? DefaultChoice;

    public static bool IsLight(string? id) => Find(id).IsLight;

    public static bool IsSystem(string? id) => string.Equals(id, ThemeIds.System, StringComparison.OrdinalIgnoreCase);
}
