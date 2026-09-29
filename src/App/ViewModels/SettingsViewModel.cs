using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>What the Settings page needs from the app: the live settings and history storage.</summary>
public sealed class SettingsEnvironment
{
    public required Func<AppSettings> Current { get; init; }

    /// <summary>Persists settings; false when they could not be saved.</summary>
    public required Func<AppSettings, bool> Save { get; init; }

    /// <summary>Bytes of saved command history; throws when history storage fails.</summary>
    public Func<long> HistorySize { get; init; } = () => 0;
    public string HistoryDirectory { get; init; } = "";
    public Action ClearHistory { get; init; } = () => { };
    public Func<Exception, bool> IsStorageFailure { get; init; } = _ => false;
    public Action<Exception> ReportError { get; init; } = _ => { };
}

/// <summary>
/// The Settings page. Every change saves at once: each setter rebases on the live settings,
/// so anything saved meanwhile (pane widths, pinned tabs, another window) survives. Values
/// the page cannot use — an empty number box, a blank path — keep the saved value.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsEnvironment _environment;

    public SettingsViewModel(SettingsEnvironment environment)
    {
        _environment = environment;
        RefreshHistoryUsage();
    }

    /// <summary>Raised after a setting is saved, with the property that changed.</summary>
    public event Action<string>? SettingChanged;

    private AppSettings Current => _environment.Current();

    /// <summary>Re-reads everything, after settings changed elsewhere.</summary>
    public void Refresh()
    {
        RefreshHistoryUsage();
        OnPropertyChanged(string.Empty);
    }

    private void Save(Func<AppSettings, AppSettings> change, [CallerMemberName] string property = "")
    {
        var current = Current;
        var updated = change(current);
        if (updated != current && _environment.Save(updated))
            SettingChanged?.Invoke(property);
        // Raised either way: a rejected or unsaved value snaps the control back.
        OnPropertyChanged(property);
    }

    /// <summary>Whole numbers from a number box, kept in range; NaN (an emptied box) is rejected.</summary>
    private static int? Whole(double value, int minimum, int maximum) =>
        double.IsNaN(value) ? null : Math.Clamp((int)value, minimum, maximum);

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---- General ----

    public IReadOnlyList<ThemeChoice> Themes => ThemeCatalog.All;

    public ThemeChoice Theme
    {
        get => ThemeCatalog.Find(Current.Theme);
        set { if (value is not null) Save(s => s with { Theme = value.Id }); }
    }

    public string FontFamily
    {
        get => Current.FontFamily;
        set => Save(s => s with { FontFamily = NonBlank(value) ?? s.FontFamily });
    }

    public double FontSize
    {
        get => Current.FontSize;
        set => Save(s => s with { FontSize = Whole(value, 8, 32) ?? s.FontSize });
    }

    public double Scrollback
    {
        get => Current.Scrollback;
        set => Save(s => s with { Scrollback = Whole(value, 1000, 100000) ?? s.Scrollback });
    }

    public bool CopyOnSelect
    {
        get => Current.CopyOnSelect;
        set => Save(s => s with { CopyOnSelect = value });
    }

    public bool RightClickPaste
    {
        get => Current.RightClickPaste;
        set => Save(s => s with { RightClickPaste = value });
    }

    public bool ShowStatusBar
    {
        get => Current.ShowStatusBar;
        set => Save(s => s with { ShowStatusBar = value });
    }

    public bool ReopenLastLayoutAtStartup
    {
        get => Current.ReopenLastLayoutAtStartup;
        set => Save(s => s with { ReopenLastLayoutAtStartup = value });
    }

    public bool ConfirmCloseActiveSessions
    {
        get => Current.ConfirmCloseActiveSessions;
        set => Save(s => s with { ConfirmCloseActiveSessions = value });
    }

    public bool WriteCrashReports
    {
        get => Current.WriteCrashReports;
        set => Save(s => s with { WriteCrashReports = value });
    }

    // ---- Recording ----

    public bool KeepCommandHistory
    {
        get => Current.KeepCommandHistory;
        set => Save(s => s with { KeepCommandHistory = value });
    }

    public double CommandHistoryDays
    {
        get => Current.CommandHistoryDays;
        set => Save(s => s with { CommandHistoryDays = Whole(value, 1, 3650) ?? s.CommandHistoryDays });
    }

    public string RecordingDirectory
    {
        get => Current.RecordingDirectory;
        set => Save(s => s with { RecordingDirectory = NonBlank(value) ?? s.RecordingDirectory });
    }

    public bool AlwaysRecord
    {
        get => Current.AlwaysRecord;
        set => Save(s => s with { AlwaysRecord = value });
    }

    public double RewindMinutes
    {
        get => Current.RewindMinutes;
        set => Save(s => s with { RewindMinutes = Whole(value, 1, 1440) ?? s.RewindMinutes });
    }

    public double RewindMegabytes
    {
        get => Current.RewindMegabytes;
        set => Save(s => s with { RewindMegabytes = Whole(value, 1, 1024) ?? s.RewindMegabytes });
    }

    private string _historyUsageText = "";
    private bool _canClearHistory;

    public string HistoryUsageText
    {
        get => _historyUsageText;
        private set => SetProperty(ref _historyUsageText, value);
    }

    public bool CanClearHistory
    {
        get => _canClearHistory;
        private set => SetProperty(ref _canClearHistory, value);
    }

    public void RefreshHistoryUsage()
    {
        long bytes;
        try { bytes = _environment.HistorySize(); }
        catch (Exception exception) when (_environment.IsStorageFailure(exception)) { bytes = 0; }
        HistoryUsageText = bytes == 0
            ? "No history is stored."
            : $"History uses {FormatSize(bytes)} in {_environment.HistoryDirectory}.";
        CanClearHistory = bytes > 0;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        try { _environment.ClearHistory(); }
        catch (Exception exception) when (_environment.IsStorageFailure(exception))
        {
            _environment.ReportError(exception);
        }
        RefreshHistoryUsage();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    // ---- Agents ----

    public bool ShowAgentIcons
    {
        get => Current.ShowAgentIcons;
        set
        {
            Save(s => s with { ShowAgentIcons = value });
            OnPropertyChanged(nameof(AgentAlertsEnabled));
        }
    }

    /// <summary>Alerts come from agent detection, so they need agent icons turned on.</summary>
    public bool AgentAlertsEnabled => ShowAgentIcons;

    public bool AgentAlertFlash
    {
        get => Current.AgentAlertFlash;
        set => Save(s => s with { AgentAlertFlash = value });
    }

    public bool AgentAlertSound
    {
        get => Current.AgentAlertSound;
        set => Save(s => s with { AgentAlertSound = value });
    }
}
