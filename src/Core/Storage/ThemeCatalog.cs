namespace Resesh.Core.Storage;

public sealed record ThemeChoice(string Id, string Name, bool IsLight = false)
{
    public override string ToString() => Name;
}

/// <summary>Stable theme identifiers shared by settings and per-session overrides.</summary>
public static class ThemeCatalog
{
    public static IReadOnlyList<ThemeChoice> All { get; } =
    [
        new("dark", "resesh Dark"),
        new("light", "resesh Light", true),
        new("system", "System"),
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
    ];

    public static ThemeChoice Find(string? id) =>
        All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static bool IsLight(string? id) => Find(id).IsLight;
}
