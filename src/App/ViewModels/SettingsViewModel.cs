using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>What the Settings page needs from the app: the live settings and history storage.</summary>
public sealed class SettingsEnvironment
{
    public required Func<AppSettings> Current { get; init; }

    /// <summary>Persists settings; false when they could not be saved.</summary>
    public required Func<AppSettings, bool> Save { get; init; }

    /// <summary>Whether Windows is in light mode, for the System appearance.</summary>
    public Func<bool> SystemIsLight { get; init; } = () => false;

    /// <summary>Bytes of saved command history; throws when history storage fails.</summary>
    public Func<long> HistorySize { get; init; } = () => 0;
    public string HistoryDirectory { get; init; } = "";
    public Action ClearHistory { get; init; } = () => { };
    public Func<Exception, bool> IsStorageFailure { get; init; } = _ => false;
    public Action<Exception> ReportError { get; init; } = _ => { };

    /// <summary>Starting at sign-in lives in Windows, not in settings.json, so Task Manager can
    /// turn it off too. Unavailable in demo mode or with --data-dir, which a sign-in launch drops.</summary>
    public bool CanLaunchAtSignIn { get; init; }
    public Func<bool> LaunchAtSignIn { get; init; } = () => false;

    /// <summary>Turns starting at sign-in on or off; false when Windows refused.</summary>
    public Func<bool, bool> SetLaunchAtSignIn { get; init; } = _ => false;
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

    private AppSettings? _ownSave;

    /// <summary>Re-reads everything, after settings changed elsewhere. Settings this page saved
    /// itself are not "elsewhere": every window re-reads its pages when the store announces a
    /// save, and the control that made it already shows the value (see <see cref="Save{T}"/>
    /// for why echoing it back is harmful; a text box would also lose its caret).</summary>
    public void Refresh()
    {
        if (_ownSave is not null && ReferenceEquals(Current, _ownSave))
            return;
        RefreshHistoryUsage();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Saves through the environment, remembering the result as this page's own.</summary>
    private bool SaveSettings(AppSettings updated)
    {
        if (!_environment.Save(updated))
            return false;
        _ownSave = updated;
        return true;
    }

    /// <summary>Saves a value a control sent. The control already shows it, so the page is
    /// told only when the stored value differs: clamped, trimmed, rejected, or unsaved.
    /// Echoing an accepted value back is not harmless: a ComboBox whose SelectedItem is
    /// set from inside its own change callback raises that callback again, forever.</summary>
    private void Save<T>(T sent, Func<AppSettings, AppSettings> change, Func<T> stored,
        [CallerMemberName] string property = "")
    {
        var current = Current;
        var updated = change(current);
        if (updated != current && SaveSettings(updated))
            SettingChanged?.Invoke(property);
        if (!EqualityComparer<T>.Default.Equals(stored(), sent))
            OnPropertyChanged(property);
    }

    /// <summary>Whole numbers from a number box, kept in range; NaN (an emptied box) is rejected.</summary>
    private static int? Whole(double value, int minimum, int maximum) =>
        double.IsNaN(value) ? null : Math.Clamp((int)value, minimum, maximum);

    private static int? Whole(double value, IntRange range) => Whole(value, range.Minimum, range.Maximum);

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---- Theme ----

    public List<AppearanceChoice> AppearanceChoices { get; } = [.. AppearanceChoice.All];

    /// <summary>Match Windows (a theme for each Windows mode) or One theme (always the same).</summary>
    public AppearanceChoice Appearance
    {
        get => AppearanceChoices[IsFollowingWindows ? 0 : 1];
        set
        {
            if (value is null)
                return;
            // Leaving System keeps the theme drawn now, so the switch changes nothing on screen.
            Save(value, s => value.Id == ThemeAppearances.System
                ? s with { Appearance = ThemeAppearances.System }
                : ThemeAppearances.Normalize(s.Appearance) == ThemeAppearances.System
                    ? s.WithFixedTheme(s.ThemeFor(_environment.SystemIsLight()))
                    : s, () => Appearance);
            NotifyThemes();
        }
    }

    public bool IsFollowingWindows => ThemeAppearances.Normalize(Current.Appearance) == ThemeAppearances.System;

    // Bound to ItemsSource: a concrete List lets CsWinRT generate its WinRT marshalling for AOT.
    public List<ThemeChoice> AllThemes { get; } = [.. ThemeCatalog.All];
    public List<ThemeChoice> LightThemes { get; } = [.. ThemeCatalog.All.Where(theme => theme.IsLight)];
    public List<ThemeChoice> DarkThemes { get; } = [.. ThemeCatalog.All.Where(theme => !theme.IsLight)];

    /// <summary>The theme drawn now; picking one fixes the appearance to it (One theme).</summary>
    public ThemeChoice Theme
    {
        get => ThemeCatalog.Find(Current.ThemeFor(_environment.SystemIsLight()));
        set
        {
            if (value is null)
                return;
            Save(value, s => s.WithFixedTheme(value.Id), () => Theme);
            NotifyThemes();
        }
    }

    /// <summary>System's theme while Windows is in light mode.</summary>
    public ThemeChoice LightTheme
    {
        get => ThemeCatalog.Find(Current.LightTheme);
        set
        {
            if (value is null)
                return;
            Save(value, s => s with { LightTheme = value.Id }, () => LightTheme);
            NotifyThemes();
        }
    }

    /// <summary>System's theme while Windows is in dark mode.</summary>
    public ThemeChoice DarkTheme
    {
        get => ThemeCatalog.Find(Current.DarkTheme);
        set
        {
            if (value is null)
                return;
            Save(value, s => s with { DarkTheme = value.Id }, () => DarkTheme);
            NotifyThemes();
        }
    }

    /// <summary>Windows switched between light and dark mode, which can change the drawn theme.</summary>
    public void ThemeModeChanged() => OnPropertyChanged(nameof(Theme));

    /// <summary>One theme setting saved changes what the others show: the drawn theme, the
    /// mode's theme, and which galleries the page shows. Galleries ignore a selection they
    /// already show, so these notifications can't echo back into a save.</summary>
    private void NotifyThemes()
    {
        OnPropertyChanged(nameof(IsFollowingWindows));
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(LightTheme));
        OnPropertyChanged(nameof(DarkTheme));
    }

    // ---- General ----

    /// <summary>The terminal font: the first entry of the saved font list. Picking one keeps
    /// the fallbacks after it.</summary>
    public string TerminalFont
    {
        get => FontFamilies.Primary(Current.FontFamily);
        set => Save(value, s => s with
        {
            FontFamily = NonBlank(value) is { } font ? FontFamilies.WithPrimary(s.FontFamily, font) : s.FontFamily,
        }, () => TerminalFont);
    }

    /// <summary>The fonts after the first, comma-separated, tried in order for characters the
    /// terminal font lacks. Blank leaves only the renderer's built-in fallbacks.</summary>
    public string FallbackFonts
    {
        get => FontFamilies.Fallbacks(Current.FontFamily);
        set => Save(value, s => s with { FontFamily = FontFamilies.WithFallbacks(s.FontFamily, value ?? "") },
            () => FallbackFonts);
    }

    /// <summary>Puts the font and its fallbacks back to the shipped defaults.</summary>
    [RelayCommand]
    private void ResetFonts()
    {
        var current = Current;
        if (current.FontFamily != FontFamilies.Default
            && SaveSettings(current with { FontFamily = FontFamilies.Default }))
            SettingChanged?.Invoke(nameof(TerminalFont));
        OnPropertyChanged(nameof(TerminalFont));
        OnPropertyChanged(nameof(FallbackFonts));
    }

    public double FontSize
    {
        get => Current.FontSize;
        set => Save(value, s => s with { FontSize = Whole(value, SettingRanges.FontSize) ?? s.FontSize }, () => FontSize);
    }

    public double Scrollback
    {
        get => Current.Scrollback;
        set => Save(value, s => s with { Scrollback = Whole(value, SettingRanges.Scrollback) ?? s.Scrollback }, () => Scrollback);
    }

    public bool CopyOnSelect
    {
        get => Current.CopyOnSelect;
        set => Save(value, s => s with { CopyOnSelect = value }, () => CopyOnSelect);
    }

    public bool RightClickPaste
    {
        get => Current.RightClickPaste;
        set => Save(value, s => s with { RightClickPaste = value }, () => RightClickPaste);
    }

    public List<MouseCaptureChoice> MouseCaptureChoices { get; } = [.. MouseCaptureChoice.All];

    public MouseCaptureChoice MouseCapture
    {
        get
        {
            var saved = MouseCaptureModes.Normalize(Current.MouseCapture);
            return MouseCaptureChoices.First(choice => choice.Id == saved);
        }
        set
        {
            if (value is not null)
                Save(value, s => s with { MouseCapture = value.Id }, () => MouseCapture);
        }
    }

    /// <summary>SSH algorithms turned off for sessions that do not choose their own.</summary>
    public Resesh.Core.Models.SshAlgorithms? SshAlgorithms
    {
        get => Current.SshAlgorithms;
        set => Save(value, s => s with { SshAlgorithms = value }, () => SshAlgorithms);
    }

    /// <summary>Saves the kinds the picker changed from <paramref name="shown"/> to
    /// <paramref name="edited"/>, onto the live setting: Settings in another window may have
    /// changed the other kinds since this page last showed them.</summary>
    public void ChangeSshAlgorithms(Resesh.Core.Models.SshAlgorithms? shown, Resesh.Core.Models.SshAlgorithms? edited)
    {
        Save(edited, s =>
        {
            var merged = s.SshAlgorithms;
            foreach (var kind in Resesh.Core.Ssh.SshAlgorithmPolicy.Kinds)
            {
                var before = Resesh.Core.Ssh.SshAlgorithmPolicy.Disabled(shown, kind);
                var after = Resesh.Core.Ssh.SshAlgorithmPolicy.Disabled(edited, kind);
                if (!(before ?? []).SequenceEqual(after ?? []) || (before is null) != (after is null))
                    merged = Resesh.Core.Ssh.SshAlgorithmPolicy.With(merged, kind, after);
            }
            return merged == s.SshAlgorithms ? s : s with { SshAlgorithms = merged };
        }, () => SshAlgorithms, nameof(SshAlgorithms));
    }

    public bool ConfirmMultilinePaste
    {
        get => Current.ConfirmMultilinePaste;
        set => Save(value, s => s with { ConfirmMultilinePaste = value }, () => ConfirmMultilinePaste);
    }

    public List<ThemeColorsChoice> ThemeColorsChoices { get; } = [.. ThemeColorsChoice.All];

    public ThemeColorsChoice ThemeColors
    {
        get
        {
            var saved = ThemeColorModes.Normalize(Current.ThemeColors);
            return ThemeColorsChoices.First(choice => choice.Id == saved);
        }
        set
        {
            if (value is not null)
                Save(value, s => s with { ThemeColors = value.Id }, () => ThemeColors);
        }
    }

    public bool ShowStatusBar
    {
        get => Current.ShowStatusBar;
        set => Save(value, s => s with { ShowStatusBar = value }, () => ShowStatusBar);
    }

    public List<SessionsPaneLayoutChoice> SessionsPaneLayoutChoices { get; } =
    [
        new(SessionsPaneLayouts.Automatic, "Float when narrow"),
        new(SessionsPaneLayouts.Float, "Float over tabs"),
        new(SessionsPaneLayouts.Dock, "Dock beside tabs"),
    ];

    public SessionsPaneLayoutChoice SessionsPaneLayout
    {
        get
        {
            var saved = SessionsPaneLayouts.Normalize(Current.SessionsPaneLayout);
            return SessionsPaneLayoutChoices.First(choice => choice.Id == saved);
        }
        set
        {
            if (value is not null)
                Save(value, s => s with { SessionsPaneLayout = value.Id }, () => SessionsPaneLayout);
        }
    }

    public bool ReopenLastLayoutAtStartup
    {
        get => Current.ReopenLastLayoutAtStartup;
        set => Save(value, s => s with { ReopenLastLayoutAtStartup = value }, () => ReopenLastLayoutAtStartup);
    }

    public bool CanLaunchAtSignIn => _environment.CanLaunchAtSignIn;

    public bool LaunchAtSignIn
    {
        get => _environment.CanLaunchAtSignIn && _environment.LaunchAtSignIn();
        set
        {
            if (value != LaunchAtSignIn && _environment.CanLaunchAtSignIn && _environment.SetLaunchAtSignIn(value))
                SettingChanged?.Invoke(nameof(LaunchAtSignIn));
            if (LaunchAtSignIn != value)
                OnPropertyChanged();
        }
    }

    public bool ConfirmCloseActiveSessions
    {
        get => Current.ConfirmCloseActiveSessions;
        set => Save(value, s => s with { ConfirmCloseActiveSessions = value }, () => ConfirmCloseActiveSessions);
    }

    public bool WriteCrashReports
    {
        get => Current.WriteCrashReports;
        set => Save(value, s => s with { WriteCrashReports = value }, () => WriteCrashReports);
    }

    // ---- Recording ----

    public bool KeepCommandHistory
    {
        get => Current.KeepCommandHistory;
        set => Save(value, s => s with { KeepCommandHistory = value }, () => KeepCommandHistory);
    }

    public double CommandHistoryDays
    {
        get => Current.CommandHistoryDays;
        set => Save(value, s => s with { CommandHistoryDays = Whole(value, SettingRanges.CommandHistoryDays) ?? s.CommandHistoryDays }, () => CommandHistoryDays);
    }

    public string RecordingDirectory
    {
        get => Current.RecordingDirectory;
        set => Save(value, s => s with { RecordingDirectory = NonBlank(value) ?? s.RecordingDirectory }, () => RecordingDirectory);
    }

    public bool AlwaysRecord
    {
        get => Current.AlwaysRecord;
        set => Save(value, s => s with { AlwaysRecord = value }, () => AlwaysRecord);
    }

    public double RewindMinutes
    {
        get => Current.RewindMinutes;
        set => Save(value, s => s with { RewindMinutes = Whole(value, Core.Recording.RewindLimits.MinimumMinutes, Core.Recording.RewindLimits.MaximumMinutes) ?? s.RewindMinutes }, () => RewindMinutes);
    }

    public double RewindMegabytes
    {
        get => Current.RewindMegabytes;
        set => Save(value, s => s with { RewindMegabytes = Whole(value, Core.Recording.RewindLimits.MinimumMegabytes, Core.Recording.RewindLimits.MaximumMegabytes) ?? s.RewindMegabytes }, () => RewindMegabytes);
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
            : $"History uses {ByteSize.Format(bytes)} in {_environment.HistoryDirectory}.";
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

    // ---- Agents ----

    public bool ShowAgentIcons
    {
        get => Current.ShowAgentIcons;
        set
        {
            Save(value, s => s with { ShowAgentIcons = value }, () => ShowAgentIcons);
            OnPropertyChanged(nameof(AgentAlertsEnabled));
        }
    }

    /// <summary>Alerts come from agent detection, so they need agent icons turned on.</summary>
    public bool AgentAlertsEnabled => ShowAgentIcons;

    public bool AgentAlertFlash
    {
        get => Current.AgentAlertFlash;
        set => Save(value, s => s with { AgentAlertFlash = value }, () => AgentAlertFlash);
    }

    public bool AgentAlertSound
    {
        get => Current.AgentAlertSound;
        set => Save(value, s => s with { AgentAlertSound = value }, () => AgentAlertSound);
    }
}

/// <summary>One option in the Settings appearance list: follow Windows with a theme for each
/// of its modes, or always one theme. Theme is saved as the light or dark appearance its
/// theme belongs to (<see cref="AppSettings.WithFixedTheme"/>), so it has no saved value.</summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record AppearanceChoice(string Id, string Name)
{
    public const string FixedTheme = "theme";

    /// <summary>System first: <see cref="SettingsViewModel.Appearance"/> indexes this list.</summary>
    public static IReadOnlyList<AppearanceChoice> All { get; } =
    [
        new(ThemeAppearances.System, "Match Windows"),
        new(FixedTheme, "One theme"),
    ];
}

/// <summary>One option for how much of the app takes the theme's colors.</summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record ThemeColorsChoice(string Id, string Name)
{
    public static IReadOnlyList<ThemeColorsChoice> All { get; } =
    [
        new(ThemeColorModes.Off, "Off"),
        new(ThemeColorModes.Accent, "Accent"),
        new(ThemeColorModes.Everything, "Everything"),
    ];
}

/// <summary>One option in the Settings sessions pane layout list.</summary>
// DisplayMemberPath reads properties by name; this generates that lookup for AOT.
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record SessionsPaneLayoutChoice(string Id, string Name);

/// <summary>One option for how much of the mouse programs get, in Settings and the session editors.</summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record MouseCaptureChoice(string Id, string Name)
{
    public static IReadOnlyList<MouseCaptureChoice> All { get; } =
    [
        new(MouseCaptureModes.Scroll, "Scrolling only"),
        new(MouseCaptureModes.Always, "Clicks and scrolling"),
        new(MouseCaptureModes.Never, "Never"),
    ];

    /// <summary>The session editors' list: "Use app setting" first, then <see cref="All"/>.</summary>
    public static List<string> OverrideNames() => ["Use app setting", .. All.Select(choice => choice.Name)];

    /// <summary>The position of a session's override in <see cref="OverrideNames"/>.</summary>
    public static int OverrideIndex(string? mode) =>
        mode is null ? 0 : 1 + All.ToList().FindIndex(choice => choice.Id == MouseCaptureModes.Normalize(mode));

    /// <summary>The override picked at <paramref name="index"/> in <see cref="OverrideNames"/>; null inherits.</summary>
    public static string? OverrideAt(int index) => index >= 1 && index <= All.Count ? All[index - 1].Id : null;
}
