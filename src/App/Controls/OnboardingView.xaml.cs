using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Resesh.App.Dialogs;
using Resesh.Core.Import;
using Resesh.Core.Storage;

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

    public event Action? FinishRequested;

    public OnboardingView(
        AppSettings settings,
        Action<string> previewTheme,
        Action sessionsChanged)
    {
        _savedSettings = settings;
        _previewTheme = previewTheme;
        _sessionsChanged = sessionsChanged;
        _selectedTheme = settings.Theme;

        InitializeComponent();

        ConfirmCloseToggle.IsOn = settings.ConfirmCloseActiveSessions;
        CopyOnSelectToggle.IsOn = settings.CopyOnSelect;
        RightClickPasteToggle.IsOn = settings.RightClickPaste;
        CrashReportsToggle.IsOn = settings.WriteCrashReports;
        PopulateThemeFlyout();
        UpdateThemeSelection();
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

        var puttyTask = Task.Run(() => PuttyRegistryImporter.Scan());
        var openSshTask = Task.Run(() => OpenSshConfigImporter.Scan(OpenSshConfigImporter.DefaultConfigPath));
        var secureCrtTask = Task.Run(SecureCrtImporter.ScanDefault);

        try
        {
            _puttyScan = await puttyTask;
            SetImportButton(PuttyImportButton, PuttyImportLabel, PuttyBadge, _puttyScan.Importable.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetImportUnavailable(PuttyImportButton, PuttyImportLabel, "Unavailable");
        }

        try
        {
            _openSshScan = await openSshTask;
            SetImportButton(OpenSshImportButton, OpenSshImportLabel, OpenSshBadge, _openSshScan.Importable.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetImportUnavailable(OpenSshImportButton, OpenSshImportLabel, "Unavailable");
        }

        try
        {
            _secureCrtScan = await secureCrtTask;
            SetImportButton(SecureCrtImportButton, SecureCrtImportLabel, SecureCrtBadge, _secureCrtScan.Importable.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetImportUnavailable(SecureCrtImportButton, SecureCrtImportLabel, "Unavailable");
        }
    }

    private static void SetImportButton(Button button, TextBlock label, InfoBadge badge, int count)
    {
        button.IsEnabled = count > 0;
        label.Text = count > 0 ? "Import" : "None found";
        badge.Value = count;
        badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetImportUnavailable(Button button, TextBlock label, string text)
    {
        button.IsEnabled = false;
        label.Text = text;
    }

    private async void PuttyImport_Click(object sender, RoutedEventArgs e)
    {
        if (_puttyScan is null)
            return;
        await PreviewAndCommitImportAsync(
            _puttyScan,
            PuttyImportButton,
            PuttyImportLabel,
            PuttyBadge,
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
            OpenSshBadge,
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
            SecureCrtBadge,
            "SecureCRT");
    }

    private async Task PreviewAndCommitImportAsync(
        ImportScanResult scan,
        Button button,
        TextBlock label,
        InfoBadge badge,
        string source)
    {
        try
        {
            var preview = new ImportPreviewDialog(scan, source)
            {
                XamlRoot = XamlRoot,
            };
            await preview.ShowAsync();
            if (preview.Confirmed is not { Count: > 0 } confirmed)
                return;

            var (imported, duplicates) = SecureCrtImporter.Commit(App.Store, confirmed);
            _sessionsChanged();
            button.IsEnabled = false;
            label.Text = "Imported";
            badge.Visibility = Visibility.Collapsed;
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

    private void TokyoNightTheme_Click(object sender, RoutedEventArgs e) => SelectTheme("tokyo-night");

    private void PhthaloGreenTheme_Click(object sender, RoutedEventArgs e) => SelectTheme("phthalo-green");

    private void PopulateThemeFlyout()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = theme.Name,
                Tag = theme.Id,
            };
            item.Click += ThemeFlyoutItem_Click;
            AllThemesFlyout.Items.Add(item);
        }
    }

    private void ThemeFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string theme })
            SelectTheme(theme);
    }

    private void SelectTheme(string theme)
    {
        _selectedTheme = theme;
        UpdateThemeSelection();
        _previewTheme(theme);
    }

    private void UpdateThemeSelection()
    {
        LightThemeToggle.IsChecked = _selectedTheme == "light";
        DarkThemeToggle.IsChecked = _selectedTheme == "dark";
        SystemThemeToggle.IsChecked = _selectedTheme == "system";

        var isTokyo = _selectedTheme == "tokyo-night";
        var isPhthalo = _selectedTheme == "phthalo-green";

        TokyoNightThemeCard.IsChecked = isTokyo;
        TokyoNightCheck.Visibility = isTokyo ? Visibility.Visible : Visibility.Collapsed;

        PhthaloGreenThemeCard.IsChecked = isPhthalo;
        PhthaloGreenCheck.Visibility = isPhthalo ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in AllThemesFlyout.Items)
        {
            if (item is ToggleMenuFlyoutItem { Tag: string themeItem } themeMenuItem)
            {
                themeMenuItem.IsChecked = string.Equals(
                    themeItem,
                    _selectedTheme,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        var resolved = App.ResolveTheme(_selectedTheme);
        if (_selectedTheme == "system")
        {
            var currentMode = resolved == "light" ? "Light" : "Dark";
            SystemThemeHint.Text = $"System mode tracks your Windows color scheme (currently {currentMode}).";
            SystemThemeHint.Visibility = Visibility.Visible;
        }
        else
        {
            SystemThemeHint.Visibility = Visibility.Collapsed;
        }
    }

    private void FinishSetup_Click(object sender, RoutedEventArgs e) => FinishRequested?.Invoke();

    private void FinishSetup_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FinishRequested?.Invoke();
    }
}
