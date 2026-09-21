using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Resesh.App.Dialogs;
using Resesh.Core.Import;
using Resesh.Core.Storage;
using Windows.UI;

namespace Resesh.App.Controls;

public sealed partial class OnboardingView : UserControl, IDisposable
{
    private readonly AppSettings _savedSettings;
    private readonly Action<string> _previewTheme;
    private readonly Action _sessionsChanged;
    private ImportScanResult? _puttyScan;
    private ImportScanResult? _openSshScan;
    private ImportScanResult? _secureCrtScan;
    private string _selectedTheme;
    private bool _scanStarted;
    private bool _completed;
    private bool _updatingThemeUi;

    public event Action? FinishRequested;
    public event Action? NewSessionRequested;
    public event Action? LocalShellRequested;

    public OnboardingView(
        AppSettings settings,
        Action<string> previewTheme,
        Action sessionsChanged,
        string? localShellName)
    {
        _savedSettings = settings;
        _previewTheme = previewTheme;
        _sessionsChanged = sessionsChanged;
        _selectedTheme = settings.Theme;

        InitializeComponent();
        Foreground = Brush("OnboardingPrimaryTextBrush");
        WelcomeLocalShellLabel.Text = localShellName is null ? "No local shell available" : $"Open {localShellName}";
        WelcomeLocalShellButton.IsEnabled = localShellName is not null;

        _updatingThemeUi = true;
        ConfirmCloseToggle.IsOn = settings.ConfirmCloseActiveSessions;
        CopyOnSelectToggle.IsOn = settings.CopyOnSelect;
        RightClickPasteToggle.IsOn = settings.RightClickPaste;
        CrashReportsToggle.IsOn = settings.WriteCrashReports;
        UpdateThemeSelection();
        _updatingThemeUi = false;
        UpdateSaveButtonState();
        Loaded += OnLoaded;
    }

    public AppSettings Complete()
    {
        _completed = true;
        return App.Settings.Current with
        {
            Theme = _selectedTheme,
            ConfirmCloseActiveSessions = ConfirmCloseToggle.IsOn,
            CopyOnSelect = CopyOnSelectToggle.IsOn,
            RightClickPaste = RightClickPasteToggle.IsOn,
            WriteCrashReports = CrashReportsToggle.IsOn,
            OnboardingCompleted = true,
        };
    }

    public void CancelPreview()
    {
        if (!_completed)
            _previewTheme(_savedSettings.Theme);
    }

    public void Dispose() => Loaded -= OnLoaded;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_scanStarted)
            return;
        _scanStarted = true;

        var puttyTask = Task.Run(() => DemoMode.IsEnabled ? DemoMode.EmptyImportScan() : PuttyRegistryImporter.Scan());
        var openSshTask = Task.Run(() => DemoMode.IsEnabled ? DemoMode.EmptyImportScan() : OpenSshConfigImporter.Scan(OpenSshConfigImporter.DefaultConfigPath));
        var secureCrtTask = Task.Run(DemoMode.ScanSecureCrt);

        try
        {
            _puttyScan = await puttyTask;
            SetImportButton(PuttyImportButton, PuttyImportLabel, _puttyScan.Importable.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetImportUnavailable(PuttyImportButton, PuttyImportLabel, "Unavailable");
        }

        try
        {
            _openSshScan = await openSshTask;
            SetImportButton(OpenSshImportButton, OpenSshImportLabel, _openSshScan.Importable.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetImportUnavailable(OpenSshImportButton, OpenSshImportLabel, "Unavailable");
        }

        try
        {
            _secureCrtScan = await secureCrtTask;
            SetImportButton(SecureCrtImportButton, SecureCrtImportLabel, _secureCrtScan.Importable.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetImportUnavailable(SecureCrtImportButton, SecureCrtImportLabel, "Unavailable");
        }
    }

    private void SetImportButton(Button button, TextBlock label, int count)
    {
        button.IsEnabled = count > 0;
        label.Text = count > 0
            ? $"{count} session{(count == 1 ? "" : "s")} found"
            : "None found";
        label.Foreground = Brush(count > 0
            ? "OnboardingAccentTextBrush"
            : "OnboardingSecondaryTextBrush");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button,
            count > 0 ? $"{count} sessions available" : "No sessions found");
    }

    private void SetImportUnavailable(Button button, TextBlock label, string text)
    {
        button.IsEnabled = false;
        label.Text = text;
        label.Foreground = Brush("OnboardingSecondaryTextBrush");
    }

    private async void PuttyImport_Click(object sender, RoutedEventArgs e)
    {
        if (_puttyScan is null)
            return;
        await PreviewAndCommitImportAsync(
            _puttyScan,
            PuttyImportButton,
            PuttyImportLabel,
            "PuTTY");
    }

    private async void OpenSshImport_Click(object sender, RoutedEventArgs e)
    {
        if (_openSshScan is null)
            return;
        await PreviewAndCommitImportAsync(
            _openSshScan,
            OpenSshImportButton,
            OpenSshImportLabel,
            "OpenSSH");
    }

    private async void SecureCrtImport_Click(object sender, RoutedEventArgs e)
    {
        if (_secureCrtScan is null)
            return;
        await PreviewAndCommitImportAsync(
            _secureCrtScan,
            SecureCrtImportButton,
            SecureCrtImportLabel,
            "SecureCRT");
    }

    private async Task PreviewAndCommitImportAsync(
        ImportScanResult scan,
        Button button,
        TextBlock label,
        string source)
    {
        try
        {
            var preview = new ImportPreviewDialog(scan, source)
            {
                XamlRoot = XamlRoot,
            };
            await preview.ShowModalAsync();
            if (preview.Confirmed is not { Count: > 0 } confirmed)
                return;

            var (imported, duplicates) = SecureCrtImporter.Commit(App.Store, confirmed, App.SshKeys);
            _sessionsChanged();
            button.IsEnabled = false;
            label.Text = $"{imported} imported";
            label.Foreground = Brush("OnboardingAccentTextBrush");
            ImportStatus.Severity = InfoBarSeverity.Success;
            ImportStatus.Title = $"{source} import complete";
            ImportStatus.Message = duplicates == 0
                ? $"Added {imported} session{(imported == 1 ? "" : "s")}."
                : $"Added {imported}; skipped {duplicates} duplicate{(duplicates == 1 ? "" : "s")}.";
            ImportStatus.IsOpen = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ImportStatus.Severity = InfoBarSeverity.Error;
            ImportStatus.Title = $"{source} import failed";
            ImportStatus.Message = exception.Message;
            ImportStatus.IsOpen = true;
        }
    }

    private void LightTheme_Click(object sender, RoutedEventArgs e) => SelectTheme("light");

    private void DarkTheme_Click(object sender, RoutedEventArgs e) => SelectTheme("dark");

    private void SystemTheme_Click(object sender, RoutedEventArgs e) => SelectTheme("system");

    private void SelectTheme(string theme)
    {
        _selectedTheme = theme;
        _previewTheme(theme);
        UpdateThemeSelection();
        UpdateSaveButtonState();
    }

    private void UpdateThemeSelection()
    {
        var wasUpdating = _updatingThemeUi;
        _updatingThemeUi = true;
        var isSystem = _selectedTheme == "system";
        var isLight = !isSystem && ThemeCatalog.IsLight(_selectedTheme);
        LightThemeToggle.IsChecked = isLight;
        DarkThemeToggle.IsChecked = !isSystem && !isLight;
        SystemThemeToggle.IsChecked = isSystem;

        // Mode is a shortcut, not a second persisted setting. Light and Dark filter
        // the exact palette choices. Selecting a palette owns the mode. System owns
        // the palette and disables the fixed-theme picker while it follows Windows.
        if (isSystem)
        {
            ThemePicker.ItemsSource = new[] { ThemeCatalog.Find("system") };
            ThemePicker.SelectedIndex = 0;
            ThemePicker.IsEnabled = false;
        }
        else
        {
            var choices = ThemeCatalog.All
                .Where(theme => theme.Id != "system" && theme.IsLight == isLight)
                .ToList();
            ThemePicker.ItemsSource = choices;
            ThemePicker.SelectedItem = choices.FirstOrDefault(theme =>
                string.Equals(theme.Id, _selectedTheme, StringComparison.OrdinalIgnoreCase));
            ThemePicker.IsEnabled = true;
        }

        var resolved = App.ResolveTheme(_selectedTheme);
        var palette = ThemeVisualPalette.For(resolved);
        ApplyPagePalette(palette);
        if (isSystem)
        {
            var currentMode = resolved == "light" ? "Light" : "Dark";
            SystemThemeHint.Text = $"System follows your Windows color mode (currently {currentMode}). Choose Light or Dark to select a fixed color theme.";
            SystemThemeHint.Visibility = Visibility.Visible;
        }
        else
        {
            SystemThemeHint.Visibility = Visibility.Collapsed;
        }
        _updatingThemeUi = wasUpdating;
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingThemeUi && ThemePicker.SelectedItem is ThemeChoice theme)
            SelectTheme(theme.Id);
    }

    private void PreferenceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingThemeUi)
            UpdateSaveButtonState();
    }

    private void UpdateSaveButtonState()
    {
        if (FinishSetupButton is null)
            return;

        FinishSetupButton.IsEnabled = _savedSettings.OnboardingCompleted != true
            || !string.Equals(_selectedTheme, _savedSettings.Theme, StringComparison.OrdinalIgnoreCase)
            || ConfirmCloseToggle.IsOn != _savedSettings.ConfirmCloseActiveSessions
            || CopyOnSelectToggle.IsOn != _savedSettings.CopyOnSelect
            || RightClickPasteToggle.IsOn != _savedSettings.RightClickPaste
            || CrashReportsToggle.IsOn != _savedSettings.WriteCrashReports;
    }

    private void ApplyPagePalette(ThemeVisualPalette palette)
    {
        var primary = EnsureContrast(palette.TreeForeground, palette.Shell, 4.5);
        var secondary = EnsureContrast(palette.TreeMutedForeground, palette.Shell, 4.5);
        var accentText = EnsureContrast(palette.Accent, palette.Shell, 4.5);
        var accentForeground = BetterContrast(Microsoft.UI.Colors.Black, Microsoft.UI.Colors.White, palette.Accent);
        var previewBorder = EnsureContrast(palette.Frame, palette.Shell, 3.0);

        SetBrush("OnboardingPrimaryTextBrush", primary);
        SetBrush("OnboardingSecondaryTextBrush", secondary);
        SetBrush("OnboardingAccentTextBrush", accentText);
        SetBrush("OnboardingAccentForegroundBrush", accentForeground);
        SetBrush("OnboardingPreviewBackgroundBrush", palette.ActiveTab);
        SetBrush("OnboardingPreviewHeaderBrush", palette.InactiveTab);
        SetBrush("OnboardingPreviewBorderBrush", previewBorder);
        SetBrush("OnboardingPreviewTextBrush", EnsureContrast(palette.TreeForeground, palette.ActiveTab, 4.5));
    }

    private void SetBrush(string key, Color color) => ((SolidColorBrush)Resources[key]).Color = color;

    private Brush Brush(string key) => (Brush)Resources[key];

    private static Color EnsureContrast(Color foreground, Color background, double minimum)
    {
        if (ContrastRatio(foreground, background) >= minimum)
            return foreground;

        var target = BetterContrast(Microsoft.UI.Colors.Black, Microsoft.UI.Colors.White, background);
        for (var step = 1; step <= 20; step++)
        {
            var candidate = Mix(foreground, target, step / 20.0);
            if (ContrastRatio(candidate, background) >= minimum)
                return candidate;
        }
        return target;
    }

    private static Color BetterContrast(Color first, Color second, Color background) =>
        ContrastRatio(first, background) >= ContrastRatio(second, background) ? first : second;

    private static Color Mix(Color first, Color second, double amount) => Color.FromArgb(
        255,
        (byte)Math.Round(first.R + ((second.R - first.R) * amount)),
        (byte)Math.Round(first.G + ((second.G - first.G) * amount)),
        (byte)Math.Round(first.B + ((second.B - first.B) * amount)));

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05)
            / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private void FinishSetup_Click(object sender, RoutedEventArgs e) => FinishRequested?.Invoke();

    private void NewSession_Click(object sender, RoutedEventArgs e) => NewSessionRequested?.Invoke();

    private void LocalShell_Click(object sender, RoutedEventArgs e) => LocalShellRequested?.Invoke();

    private void FinishSetup_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!FinishSetupButton.IsEnabled)
            return;
        args.Handled = true;
        FinishRequested?.Invoke();
    }
}
