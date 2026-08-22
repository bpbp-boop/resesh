using System.Text.Json;
using System.Text.Json.Serialization;
using Resesh.Core.Models;

namespace Resesh.Core.Storage;

/// <summary>App-wide shell and terminal settings.</summary>
public sealed record AppSettings
{
    /// <summary>The main window's last normal desktop bounds and presentation state.</summary>
    public WindowPlacement? WindowPlacement { get; init; }

    /// <summary>Session tree pane width in pixels; null = default.</summary>
    public double? TreePaneWidth { get; init; }

    /// <summary>Per-tab file pane width in pixels; null = default.</summary>
    public double? FilePaneWidth { get; init; }

    /// <summary>Sessions pinned in the tab strip, in display order; reopened automatically on launch.</summary>
    public IReadOnlyList<Guid> PinnedSessionIds { get; init; } = [];

    /// <summary>The local profile "+ Session" / Ctrl+Shift+T opens; null = highest-priority
    /// discovered shell (see LocalShellDiscovery.DefaultProfile).</summary>
    public Guid? DefaultLocalProfileId { get; init; }

    public string Theme { get; init; } = "dark";
    public string FontFamily { get; init; } = "Cascadia Mono, Consolas, monospace";
    public int FontSize { get; init; } = 14;
    public int Scrollback { get; init; } = 10000;
    public bool CopyOnSelect { get; init; } = true;
    public bool RightClickPaste { get; init; } = true;

    /// <summary>Automatically start a disk recording for each new terminal tab.</summary>
    public bool AlwaysRecord { get; init; }

    public string RecordingDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Resesh Recordings");


    /// <summary>Age and memory caps for each tab's always-on in-memory rewind stream.</summary>
    public int RewindMinutes { get; init; } = 30;
    public int RewindMegabytes { get; init; } = 32;

    /// <summary>Show the agent icon and attention badge on tabs (Phase 6.2).</summary>
    public bool ShowAgentIcons { get; init; } = true;

    /// <summary>Flash the taskbar button when a background tab's agent needs the user.</summary>
    public bool AgentAlertFlash { get; init; } = true;

    /// <summary>Also play the system notification sound for those alerts.</summary>
    public bool AgentAlertSound { get; init; }

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
                AlwaysRecord = overrides.AlwaysRecord ?? AlwaysRecord,
            };
}

public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool IsMaximized = false);

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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Resesh", "settings.json");

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
