using Resesh.App.ViewModels;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

public sealed class SettingsViewModelTests
{
    private AppSettings _settings = new();
    private readonly List<string> _changes = [];
    private bool _saveSucceeds = true;
    private bool _systemIsLight;
    private long _historyBytes;
    private int _clears;

    private SettingsViewModel Settings()
    {
        var settings = new SettingsViewModel(new SettingsEnvironment
        {
            Current = () => _settings,
            SystemIsLight = () => _systemIsLight,
            Save = updated =>
            {
                if (!_saveSucceeds)
                    return false;
                _settings = updated;
                return true;
            },
            HistorySize = () => _historyBytes,
            HistoryDirectory = @"C:\history",
            ClearHistory = () =>
            {
                _clears++;
                _historyBytes = 0;
            },
        });
        settings.SettingChanged += _changes.Add;
        return settings;
    }

    [Fact]
    public void ChangeSavesAtOnceAndKeepsFieldsChangedElsewhere()
    {
        var settings = Settings();
        // Another window (or a pane resize) saves after the page opened.
        _settings = _settings with { TreePaneWidth = 333, ShowStatusBar = false };

        settings.CopyOnSelect = false;

        Assert.False(_settings.CopyOnSelect);
        Assert.Equal(333, _settings.TreePaneWidth);
        Assert.False(_settings.ShowStatusBar);
        Assert.Equal([nameof(SettingsViewModel.CopyOnSelect)], _changes);
    }

    [Fact]
    public void SshAlgorithmChoicesSaveAndClear()
    {
        var settings = Settings();
        var legacyOff = new Resesh.Core.Models.SshAlgorithms { DisabledCiphers = ["3des-cbc"] };

        settings.SshAlgorithms = legacyOff;
        Assert.Same(legacyOff, _settings.SshAlgorithms);

        settings.SshAlgorithms = null;
        Assert.Null(_settings.SshAlgorithms);
        Assert.Equal([nameof(SettingsViewModel.SshAlgorithms), nameof(SettingsViewModel.SshAlgorithms)], _changes);
    }

    [Fact]
    public void SshAlgorithmChangesKeepKindsChangedInAnotherWindow()
    {
        var settings = Settings();
        var mac = Resesh.Core.Ssh.SshAlgorithmPolicy.Supported(Resesh.Core.Ssh.SshAlgorithmKind.Mac)[0];
        var cipher = Resesh.Core.Ssh.SshAlgorithmPolicy.Supported(Resesh.Core.Ssh.SshAlgorithmKind.Cipher)[0];
        var shown = settings.SshAlgorithms;
        // Settings in another window turns a MAC off after this page last showed the setting.
        _settings = _settings with { SshAlgorithms = new Resesh.Core.Models.SshAlgorithms { DisabledMacs = [mac] } };

        settings.ChangeSshAlgorithms(shown, new Resesh.Core.Models.SshAlgorithms { DisabledCiphers = [cipher] });

        Assert.Equal([cipher], _settings.SshAlgorithms!.DisabledCiphers!);
        Assert.Equal([mac], _settings.SshAlgorithms.DisabledMacs!);
        Assert.Equal([nameof(SettingsViewModel.SshAlgorithms)], _changes);
    }

    [Fact]
    public void SessionsPaneLayoutSavesTheChosenLayout()
    {
        var settings = Settings();
        Assert.Equal(SessionsPaneLayouts.Automatic, settings.SessionsPaneLayout.Id);

        settings.SessionsPaneLayout = settings.SessionsPaneLayoutChoices.Single(c => c.Id == SessionsPaneLayouts.Float);

        Assert.Equal(SessionsPaneLayouts.Float, _settings.SessionsPaneLayout);
        Assert.Equal([nameof(SettingsViewModel.SessionsPaneLayout)], _changes);
    }

    [Fact]
    public void UnknownSessionsPaneLayoutReadsAsAutomatic()
    {
        _settings = _settings with { SessionsPaneLayout = "sideways" };

        Assert.Equal(SessionsPaneLayouts.Automatic, Settings().SessionsPaneLayout.Id);
    }

    [Fact]
    public void MouseCaptureDefaultsToScrollingAndSavesTheChoice()
    {
        var settings = Settings();
        Assert.Equal(MouseCaptureModes.Scroll, settings.MouseCapture.Id);

        settings.MouseCapture = settings.MouseCaptureChoices.Single(c => c.Id == MouseCaptureModes.Always);

        Assert.Equal(MouseCaptureModes.Always, _settings.MouseCapture);
        Assert.Equal([nameof(SettingsViewModel.MouseCapture)], _changes);
    }

    [Fact]
    public void UnknownMouseCaptureReadsAsScrolling()
    {
        _settings = _settings with { MouseCapture = "sometimes" };

        Assert.Equal(MouseCaptureModes.Scroll, Settings().MouseCapture.Id);
    }

    [Fact]
    public void MouseCaptureOverrideListRoundTripsEveryChoice()
    {
        Assert.Equal(0, MouseCaptureChoice.OverrideIndex(null));
        Assert.Null(MouseCaptureChoice.OverrideAt(0));
        foreach (var choice in MouseCaptureChoice.All)
        {
            var index = MouseCaptureChoice.OverrideIndex(choice.Id);
            Assert.Equal(choice.Name, MouseCaptureChoice.OverrideNames()[index]);
            Assert.Equal(choice.Id, MouseCaptureChoice.OverrideAt(index));
        }
    }

    [Fact]
    public void EmptiedNumberBoxAndBlankTextKeepTheSavedValue()
    {
        var settings = Settings();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.FontSize = double.NaN;
        settings.TerminalFont = "   ";
        settings.RecordingDirectory = "";

        Assert.Equal(new AppSettings().FontSize, _settings.FontSize);
        Assert.Equal(new AppSettings().FontFamily, _settings.FontFamily);
        Assert.Equal(new AppSettings().RecordingDirectory, _settings.RecordingDirectory);
        Assert.Empty(_changes);
        // The page is told anyway, so the control snaps back to the saved value.
        Assert.Contains(nameof(SettingsViewModel.FontSize), raised);
    }

    [Fact]
    public void AcceptedValueIsNotEchoedBackToTheControl()
    {
        // A ComboBox re-raises its SelectedItem callback when set from inside it; echoing
        // the value it just sent looped until the stack overflowed.
        var settings = Settings();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.CopyOnSelect = false;
        settings.FontSize = 16;
        settings.Refresh();

        Assert.Empty(raised);
    }

    [Fact]
    public void ClampedValueIsPushedBackToTheControl()
    {
        var settings = Settings();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.FontSize = 99;

        Assert.Equal([nameof(SettingsViewModel.FontSize)], raised);
    }

    [Fact]
    public void NumbersAreWholeAndInRange()
    {
        var settings = Settings();

        settings.FontSize = 99;
        settings.Scrollback = 12345.7;

        Assert.Equal(32, _settings.FontSize);
        Assert.Equal(12345, _settings.Scrollback);
    }

    [Fact]
    public void TerminalFontIsTrimmed()
    {
        var settings = Settings();

        settings.TerminalFont = "  Fira Code ";

        Assert.Equal("Fira Code", settings.TerminalFont);
    }

    [Fact]
    public void PickingAFontKeepsTheFallbacks()
    {
        var settings = Settings();

        settings.TerminalFont = "Geist Mono";

        Assert.Equal("Geist Mono, Consolas, monospace", _settings.FontFamily);
        Assert.Equal("Consolas, monospace", settings.FallbackFonts);
    }

    [Fact]
    public void FallbacksChangeWithoutTouchingTheFont()
    {
        var settings = Settings();

        settings.FallbackFonts = "JetBrainsMono Nerd Font Mono,  Consolas";

        Assert.Equal("Cascadia Mono, JetBrainsMono Nerd Font Mono, Consolas", _settings.FontFamily);
    }

    [Fact]
    public void ResetFontsRestoresTheDefaultList()
    {
        var settings = Settings();
        settings.TerminalFont = "Geist Mono";
        settings.FallbackFonts = "";
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.ResetFontsCommand.Execute(null);

        Assert.Equal(FontFamilies.Default, _settings.FontFamily);
        Assert.Contains(nameof(SettingsViewModel.TerminalFont), raised);
        Assert.Contains(nameof(SettingsViewModel.FallbackFonts), raised);
    }

    [Fact]
    public void UnchangedValueDoesNotSave()
    {
        var settings = Settings();

        settings.CopyOnSelect = _settings.CopyOnSelect;

        Assert.Empty(_changes);
    }

    [Fact]
    public void FailedSaveKeepsTheOldValueAndReportsNoChange()
    {
        var settings = Settings();
        _saveSucceeds = false;

        settings.KeepCommandHistory = !_settings.KeepCommandHistory;

        Assert.Equal(new AppSettings().KeepCommandHistory, settings.KeepCommandHistory);
        Assert.Empty(_changes);
    }

    [Theory]
    [InlineData(false, ThemeAppearances.Dark, "nord")]
    [InlineData(true, ThemeAppearances.Light, "solarized-light")]
    public void SwitchingToThemeKeepsTheDrawnTheme(bool systemIsLight, string appearance, string themeId)
    {
        _settings = _settings with
        {
            Appearance = ThemeAppearances.System,
            LightTheme = "solarized-light",
            DarkTheme = "nord",
            TreePaneWidth = 333,
        };
        _systemIsLight = systemIsLight;
        var settings = Settings();
        Assert.True(settings.IsFollowingWindows);
        Assert.Equal(themeId, settings.Theme.Id);

        settings.Appearance = settings.AppearanceChoices.Single(choice => choice.Id == AppearanceChoice.FixedTheme);

        Assert.Equal(appearance, _settings.Appearance);
        Assert.Equal(themeId, settings.Theme.Id);
        Assert.Equal("solarized-light", _settings.LightTheme);
        Assert.Equal("nord", _settings.DarkTheme);
        Assert.Equal(333, _settings.TreePaneWidth);
        Assert.False(settings.IsFollowingWindows);
        Assert.Equal(AppearanceChoice.FixedTheme, settings.Appearance.Id);
        Assert.Equal([nameof(SettingsViewModel.Appearance)], _changes);
    }

    [Fact]
    public void PickingALightThemeFixesAppearanceAndKeepsTheDarkTheme()
    {
        _settings = _settings with { Appearance = ThemeAppearances.System, DarkTheme = "nord" };
        var settings = Settings();
        var light = settings.AllThemes.Single(theme => theme.Id == "solarized-light");

        settings.Theme = light;

        Assert.Equal(ThemeAppearances.Light, _settings.Appearance);
        Assert.Equal(light.Id, _settings.LightTheme);
        Assert.Equal("nord", _settings.DarkTheme);
        Assert.Equal(light, settings.Theme);
        Assert.False(settings.IsFollowingWindows);
        Assert.Equal([nameof(SettingsViewModel.Theme)], _changes);
    }

    [Fact]
    public void SystemThemeSlotsSaveIndependentlyWithoutChangingAppearance()
    {
        _settings = _settings with { Appearance = ThemeAppearances.System };
        var settings = Settings();
        var light = settings.LightThemes.First(theme => theme.Id != _settings.LightTheme);
        var dark = settings.DarkThemes.First(theme => theme.Id != _settings.DarkTheme);

        settings.LightTheme = light;
        settings.DarkTheme = dark;

        Assert.True(settings.IsFollowingWindows);
        Assert.Equal(ThemeAppearances.System, _settings.Appearance);
        Assert.Equal(light, settings.LightTheme);
        Assert.Equal(dark, settings.DarkTheme);
        Assert.All(settings.LightThemes, theme => Assert.True(theme.IsLight));
        Assert.All(settings.DarkThemes, theme => Assert.False(theme.IsLight));
        Assert.Equal([nameof(SettingsViewModel.LightTheme), nameof(SettingsViewModel.DarkTheme)], _changes);
    }

    [Fact]
    public void SwitchingToSystemKeepsBothThemeSlots()
    {
        _settings = _settings with { Appearance = ThemeAppearances.Light, LightTheme = "solarized-light", DarkTheme = "nord" };
        var settings = Settings();

        settings.Appearance = settings.AppearanceChoices.Single(choice => choice.Id == ThemeAppearances.System);

        Assert.True(settings.IsFollowingWindows);
        Assert.Equal(ThemeAppearances.System, _settings.Appearance);
        Assert.Equal("solarized-light", _settings.LightTheme);
        Assert.Equal("nord", _settings.DarkTheme);
    }

    [Fact]
    public void RefusedAppearanceAndThemeSavesRestoreTheControls()
    {
        var settings = Settings();
        var appearance = settings.Appearance;
        var light = settings.LightTheme;
        var dark = settings.DarkTheme;
        var before = _settings;
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _saveSucceeds = false;

        settings.Appearance = settings.AppearanceChoices.First(choice => choice != appearance);
        settings.LightTheme = settings.LightThemes.First(theme => theme != light);
        settings.DarkTheme = settings.DarkThemes.First(theme => theme != dark);

        Assert.Same(before, _settings);
        Assert.Equal(appearance, settings.Appearance);
        Assert.Equal(light, settings.LightTheme);
        Assert.Equal(dark, settings.DarkTheme);
        Assert.Empty(_changes);
        Assert.Contains(nameof(SettingsViewModel.Appearance), raised);
        Assert.Contains(nameof(SettingsViewModel.LightTheme), raised);
        Assert.Contains(nameof(SettingsViewModel.DarkTheme), raised);
    }

    [Fact]
    public void SystemAppearanceFollowsTheMutableWindowsModeWithoutSaving()
    {
        _settings = _settings with { Appearance = ThemeAppearances.System };
        var settings = Settings();
        var before = _settings;
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _systemIsLight = true;
        settings.ThemeModeChanged();
        Assert.Equal(_settings.LightTheme, settings.Theme.Id);

        _systemIsLight = false;
        settings.ThemeModeChanged();
        Assert.Equal(_settings.DarkTheme, settings.Theme.Id);

        Assert.Equal([nameof(SettingsViewModel.Theme), nameof(SettingsViewModel.Theme)], raised);
        Assert.Same(before, _settings);
        Assert.Empty(_changes);
    }

    [Fact]
    public void AgentAlertsFollowAgentIcons()
    {
        var settings = Settings();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.ShowAgentIcons = false;

        Assert.False(settings.AgentAlertsEnabled);
        Assert.Contains(nameof(SettingsViewModel.AgentAlertsEnabled), raised);
    }

    [Fact]
    public void HistoryUsageDescribesStorageAndClears()
    {
        _historyBytes = 3 * 1024 * 1024 / 2;
        var settings = Settings();

        Assert.Equal(@"History uses 1.5 MB in C:\history.", settings.HistoryUsageText);
        Assert.True(settings.CanClearHistory);

        settings.ClearHistoryCommand.Execute(null);

        Assert.Equal(1, _clears);
        Assert.Equal("No history is stored.", settings.HistoryUsageText);
        Assert.False(settings.CanClearHistory);
    }

    [Fact]
    public void LaunchAtSignInGoesToWindowsNotSettings()
    {
        var registered = false;
        var windowsAccepts = true;
        var settings = new SettingsViewModel(new SettingsEnvironment
        {
            Current = () => _settings,
            Save = updated => { _settings = updated; return true; },
            CanLaunchAtSignIn = true,
            LaunchAtSignIn = () => registered,
            SetLaunchAtSignIn = enabled =>
            {
                if (windowsAccepts)
                    registered = enabled;
                return windowsAccepts;
            },
        });
        settings.SettingChanged += _changes.Add;
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var before = _settings;

        settings.LaunchAtSignIn = true;

        Assert.True(registered);
        Assert.Same(before, _settings);
        Assert.Equal([nameof(SettingsViewModel.LaunchAtSignIn)], _changes);
        Assert.Empty(raised);

        // Refused by Windows: the switch goes back to what is registered.
        windowsAccepts = false;
        settings.LaunchAtSignIn = false;

        Assert.True(settings.LaunchAtSignIn);
        Assert.Equal([nameof(SettingsViewModel.LaunchAtSignIn)], raised);
    }

    [Fact]
    public void LaunchAtSignInIsOffAndLockedWithoutTheDefaultData()
    {
        var calls = 0;
        var settings = new SettingsViewModel(new SettingsEnvironment
        {
            Current = () => _settings,
            Save = _ => true,
            CanLaunchAtSignIn = false,
            LaunchAtSignIn = () => true,
            SetLaunchAtSignIn = _ => { calls++; return true; },
        });

        settings.LaunchAtSignIn = true;

        Assert.False(settings.CanLaunchAtSignIn);
        Assert.False(settings.LaunchAtSignIn);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ThePageSavingIgnoresTheStoresAnnouncementOfItsOwnSave()
    {
        var settings = Settings();
        var other = Settings();
        var echoed = new List<string?>();
        var otherRefreshed = new List<string?>();
        settings.PropertyChanged += (_, e) => echoed.Add(e.PropertyName);
        other.PropertyChanged += (_, e) => otherRefreshed.Add(e.PropertyName);

        settings.CopyOnSelect = false;
        // The store announces the save, during it or a moment later, and every window
        // refreshes its pages.
        settings.Refresh();
        other.Refresh();

        // The control already shows the value; re-raising it can loop or move a caret.
        Assert.Empty(echoed);
        Assert.Contains(string.Empty, otherRefreshed);

        // A later change made elsewhere does reach the page.
        _settings = _settings with { FontSize = 20 };
        settings.Refresh();
        Assert.Contains(string.Empty, echoed);
    }
}
