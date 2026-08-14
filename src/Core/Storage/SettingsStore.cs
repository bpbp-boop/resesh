using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sessions.Core.Storage;

/// <summary>App-wide settings (window/split layout now; terminal preferences in M5).</summary>
public sealed record AppSettings
{
    /// <summary>Left group's share of the tab area width when split (0..1); null = never split.</summary>
    public double? SplitterFraction { get; init; }

    /// <summary>Session tree pane width in pixels; null = default.</summary>
    public double? TreePaneWidth { get; init; }

    /// <summary>Sessions pinned in the tab strip, in display order; reopened automatically on launch.</summary>
    public IReadOnlyList<Guid> PinnedSessionIds { get; init; } = [];

    public string Theme { get; init; } = "dark";
    public string FontFamily { get; init; } = "Cascadia Mono, Consolas, monospace";
    public int FontSize { get; init; } = 14;
    public int Scrollback { get; init; } = 10000;
    public bool CopyOnSelect { get; init; } = true;
    public bool RightClickPaste { get; init; } = true;
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
