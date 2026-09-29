using Resesh.App.ViewModels;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

public sealed class SettingsViewModelTests
{
    private AppSettings _settings = new();
    private readonly List<string> _changes = [];
    private bool _saveSucceeds = true;
    private long _historyBytes;
    private int _clears;

    private SettingsViewModel Settings()
    {
        var settings = new SettingsViewModel(new SettingsEnvironment
        {
            Current = () => _settings,
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
    public void EmptiedNumberBoxAndBlankTextKeepTheSavedValue()
    {
        var settings = Settings();
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.FontSize = double.NaN;
        settings.FontFamily = "   ";
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

        settings.Theme = settings.Themes.First(theme => theme.IsLight);
        settings.CopyOnSelect = false;
        settings.FontSize = 16;

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
    public void FontFamilyIsTrimmed()
    {
        var settings = Settings();

        settings.FontFamily = "  Fira Code ";

        Assert.Equal("Fira Code", _settings.FontFamily);
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

    [Fact]
    public void ThemeSavesTheChoiceId()
    {
        var settings = Settings();
        var light = settings.Themes.First(theme => theme.IsLight);

        settings.Theme = light;

        Assert.Equal(light.Id, _settings.Theme);
        Assert.Equal(light, settings.Theme);
        Assert.Equal([nameof(SettingsViewModel.Theme)], _changes);
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
}
