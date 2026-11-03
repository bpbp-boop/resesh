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
    public IReadOnlyList<Guid> PinnedSessionIds { get; set; } = [];
    /// <summary>Saved sessions opened most recently, newest first.</summary>
    public IReadOnlyList<Guid> RecentSessionIds { get; set; } = [];

    /// <summary>Whether the bottom status bar is visible.</summary>
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>Whether the content pane beside the compact sessions rail is open.</summary>
    public bool SessionsPaneOpen { get; set; } = true;

    /// <summary>The selected compact-rail tab: sessions, recent, or recordings.</summary>
    public string SessionsRailTab { get; set; } = "sessions";

    /// <summary>Whether the sessions pane floats over the tabs or docks beside them; see
    /// <see cref="SessionsPaneLayouts"/>.</summary>
    public string SessionsPaneLayout { get; set; } = SessionsPaneLayouts.Automatic;


    /// <summary>Restore the clean-exit tab-group layout when the app next launches.</summary>
    public bool ReopenLastLayoutAtStartup { get; init; }

    /// <summary>False for a new installation until the welcome tab is finished. Null marks
    /// settings created before onboarding existed, so upgrades do not reopen it automatically.</summary>
    public bool? OnboardingCompleted { get; init; }

    /// <summary>Ask before closing a running, unpinned terminal session.</summary>
    public bool ConfirmCloseActiveSessions { get; set; } = true;

    /// <summary>Write unhandled exception details to the local crash log. Nothing is uploaded.</summary>
    public bool WriteCrashReports { get; set; } = true;

    /// <summary>The local profile "+ Session" / Ctrl+Shift+T opens; null = highest-priority
    /// discovered shell (see LocalShellDiscovery.DefaultProfile).</summary>
    public Guid? DefaultLocalProfileId { get; init; }

    public string Theme { get; set; } = "dark";
    public string FontFamily { get; set; } = FontFamilies.Default;
    public int FontSize { get; set; } = 14;
    public int Scrollback { get; set; } = 10000;
    public bool CopyOnSelect { get; set; } = true;
    public bool RightClickPaste { get; set; } = true;

    /// <summary>How much of the mouse a program that asks for it gets; see
    /// <see cref="MouseCaptureModes"/>. Defaults to the wheel only, so selecting, copying and
    /// right-click paste keep working in TUIs such as Claude Code and Codex.</summary>
    public string MouseCapture { get; set; } = MouseCaptureModes.Scroll;

    /// <summary>Ask before a paste that would press Enter, unless the program turned on
    /// bracketed paste. On by default: a stray multi-line paste into a device CLI runs
    /// every line.</summary>
    public bool ConfirmMultilinePaste { get; set; } = true;

    /// <summary>SSH algorithms turned off for every session that does not choose its own.
    /// Null offers everything SSH.NET has.</summary>
    public SshAlgorithms? SshAlgorithms { get; set; }

    /// <summary>Automatically start a disk recording for each new terminal tab.</summary>
    public bool AlwaysRecord { get; init; }

    public string RecordingDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Resesh Recordings");


    /// <summary>Keep a searchable local history of finished commands and their output.
    /// Off until the user turns it on: output can include secrets a server prints.</summary>
    public bool KeepCommandHistory { get; init; }

    /// <summary>Days command history is kept before it is deleted.</summary>
    public int CommandHistoryDays { get; set; } = 90;

    /// <summary>Age and memory caps for each tab's always-on in-memory rewind stream.</summary>
    public int RewindMinutes { get; set; } = 30;
    public int RewindMegabytes { get; set; } = 32;

    /// <summary>Show the agent icon and attention badge on tabs (Phase 6.2).</summary>
    public bool ShowAgentIcons { get; set; } = true;

    /// <summary>Flash the taskbar button when a background tab's agent needs the user.</summary>
    public bool AgentAlertFlash { get; set; } = true;

    /// <summary>Also play the system notification sound for those alerts.</summary>
    public bool AgentAlertSound { get; init; }

    /// <summary>These settings with a session's overrides layered on top (null members inherit).</summary>
    public AppSettings WithOverrides(TerminalOverrides? overrides) =>
        overrides is null
            ? this
            : this with
            {
                Theme = overrides.Theme ?? Theme,
                // A session's font goes in front of the app's fallbacks rather than replacing them.
                FontFamily = FontFamilies.Layer(overrides.FontFamily, FontFamily),
                FontSize = overrides.FontSize ?? FontSize,
                Scrollback = overrides.Scrollback ?? Scrollback,
                MouseCapture = overrides.MouseCapture ?? MouseCapture,
                AlwaysRecord = overrides.AlwaysRecord ?? AlwaysRecord,
                KeepCommandHistory = overrides.KeepCommandHistory ?? KeepCommandHistory,
            };

    /// <summary>These settings with every saved-session reference passed through
    /// <paramref name="map"/>, which gives the id the session has here, or null when it has
    /// none (the reference is then dropped). Every setting that names a session is mapped
    /// here and only here, so restoring a backup cannot miss one.</summary>
    public AppSettings WithSessionIds(Func<Guid, Guid?> map)
    {
        List<Guid> MapList(IEnumerable<Guid> ids) =>
            ids.Select(map).OfType<Guid>().Distinct().ToList();

        return this with
        {
            PinnedSessionIds = MapList(PinnedSessionIds),
            RecentSessionIds = MapList(RecentSessionIds),
            DefaultLocalProfileId = DefaultLocalProfileId is { } id ? map(id) : null,
        };
    }
}

public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool IsMaximized = false);

/// <summary>Saved values of <see cref="AppSettings.SessionsPaneLayout"/>.</summary>
public static class SessionsPaneLayouts
{
    /// <summary>Floats over the tabs on narrow windows and docks on wide ones.</summary>
    public const string Automatic = "auto";
    public const string Float = "float";
    public const string Dock = "dock";

    /// <summary>A saved value this version knows, or <see cref="Automatic"/>.</summary>
    public static string Normalize(string? layout) => layout is Float or Dock ? layout : Automatic;
}

/// <summary>Saved values of <see cref="AppSettings.MouseCapture"/>.</summary>
public static class MouseCaptureModes
{
    /// <summary>Clicks, drags and the wheel go to the program; Shift selects instead.</summary>
    public const string Always = "always";

    /// <summary>Only the wheel goes to the program, which has no scrollback of its own to
    /// scroll otherwise; Shift sends a click to it.</summary>
    public const string Scroll = "scroll";

    /// <summary>The program never gets the mouse.</summary>
    public const string Never = "never";

    /// <summary>A saved value this version knows, or <see cref="Scroll"/>.</summary>
    public static string Normalize(string? mode) => mode is Always or Never ? mode : Scroll;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = CoreJson.With(new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    });

    private readonly StoreFile _file;
    private readonly object _gate = new();

    public string? LoadWarning { get; private set; }

    /// <summary>Whether saves are allowed; see <see cref="StoreLoadState"/>.</summary>
    public StoreLoadState LoadState
    {
        get { lock (_gate) return _file.State; }
    }

    public AppSettings Current { get; private set; } = new();

    public SettingsStore(string path)
    {
        _file = new StoreFile(path, "Settings");
    }

    public static string DefaultPath =>
        AppDataPaths.Roaming("settings.json");

    public void Load()
    {
        lock (_gate)
        {
            var data = _file.Load(Read);
            Current = data ?? new AppSettings { OnboardingCompleted = File.Exists(_file.Path) ? null : false };
            LoadWarning = data is null && _file.Warning is not null
                ? _file.Warning + " Default settings are in use."
                : _file.Warning;
        }
    }

    /// <summary>Persists the candidate before publishing it as <see cref="Current"/>.
    /// A failed write leaves the current settings and backup recovery state unchanged.</summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            _file.Write(JsonSerializer.Serialize(settings, JsonOptions.Info<AppSettings>()));
            Current = settings;
        }
    }

    private static AppSettings? Read(string path)
    {
        var settings = JsonSerializer.Deserialize(File.ReadAllText(path), JsonOptions.Info<AppSettings>());
        // Validate before AtomicFile.Load accepts the primary, so recovery can use .bak.
        if (settings is not null
            && (settings.PinnedSessionIds is null || settings.RecentSessionIds is null))
        {
            throw new InvalidDataException("The settings payload contains a null session list.");
        }
        return settings;
    }

    /// <summary>Moves a saved session to the front of the bounded recent list.</summary>
    public void RecordRecentSession(Guid sessionId, int maximumCount = 12)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        if (sessionId == Guid.Empty)
            return;

        lock (_gate)
        {
            var recent = new List<Guid>(Math.Min(maximumCount, Current.RecentSessionIds.Count + 1))
            {
                sessionId,
            };
            foreach (var id in Current.RecentSessionIds)
            {
                if (id != sessionId)
                    recent.Add(id);
                if (recent.Count == maximumCount)
                    break;
            }
            Save(Current with { RecentSessionIds = recent });
        }
    }
}
