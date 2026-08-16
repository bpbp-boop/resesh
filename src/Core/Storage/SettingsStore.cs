using System.Text.Json;
using System.Text.Json.Serialization;
using Sessions.Core.Models;

namespace Sessions.Core.Storage;

/// <summary>App-wide shell and terminal settings.</summary>
public sealed record AppSettings
{
    /// <summary>Session tree pane width in pixels; null = default.</summary>
    public double? TreePaneWidth { get; init; }

    /// <summary>SFTP file pane width in pixels; null = default.</summary>
    public double? FilePaneWidth { get; init; }

    /// <summary>Sessions pinned in the tab strip, in display order; reopened automatically on launch.</summary>
    public IReadOnlyList<Guid> PinnedSessionIds { get; init; } = [];

    public string Theme { get; init; } = "dark";
    public string FontFamily { get; init; } = "Cascadia Mono, Consolas, monospace";
    public int FontSize { get; init; } = 14;
    public int Scrollback { get; init; } = 10000;
    public bool CopyOnSelect { get; init; } = true;
    public bool RightClickPaste { get; init; } = true;

    /// <summary>These settings with a session's overrides layered on top (null members inherit).</summary>
    public AppSettings WithOverrides(TerminalOverrides? overrides) =>
        overrides is null
            ? this
            : this with
            {
                Theme = overrides.Theme ?? Theme,
                FontFamily = overrides.FontFamily ?? FontFamily,
                FontSize = overrides.FontSize ?? FontSize,
                Scrollback = overrides.Scrollback ?? Scrollback,
            };
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public AppSettings Current { get; private set; } = new();

    public SettingsStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sessions", "settings.json");

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                    Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                Current = new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Current = settings;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(_path))
                File.Replace(tmp, _path, null);
            else
                File.Move(tmp, _path);
        }
    }
}
