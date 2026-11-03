using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Resesh.App.Dialogs;
using Resesh.App.ViewModels;
using Resesh.Core.Import;
using Resesh.Core.Storage;
using Windows.UI;

namespace Resesh.App.Controls;

public sealed partial class OnboardingView : UserControl, IDisposable
{
    private readonly WelcomeChoices _choices;
    private readonly Action<string> _previewTheme;
    private readonly Action _sessionsChanged;
    private ImportScanResult? _puttyScan;
    private ImportScanResult? _openSshScan;
    private ImportScanResult? _secureCrtScan;
    private bool _scanStarted;
    private bool _completed;
    private bool _updatingThemeUi;

    public event Action? FinishRequested;
    public event Action? NewSessionRequested;
    public event Action? LocalShellRequested;
    public event Action? ManageIdentitiesRequested;

    /// <summary>Opens the import review (a tab) and completes with what was imported, or null.</summary>
    public Func<ImportScanResult, string, Task<ImportOutcome?>>? ReviewImport { get; set; }

    public OnboardingView(
        AppSettings settings,
        Action<string> previewTheme,
        Action sessionsChanged,
        string? localShellName)
    {
        _choices = new WelcomeChoices(settings);
        _previewTheme = previewTheme;
        _sessionsChanged = sessionsChanged;

        InitializeComponent();
        AppShortcuts.AddAccelerators(this, Resesh.Core.Input.ShortcutIds.FinishWelcome, FinishSetup_Invoked);
        ToolTipService.SetToolTip(FinishSetupButton,
            AppShortcuts.ToolTip("Save preferences and close Welcome", Resesh.Core.Input.ShortcutIds.FinishWelcome));
        Foreground = Brush("OnboardingPrimaryTextBrush");
        WelcomeLocalShellLabel.Text = localShellName is null ? "No local shell available" : $"Open {localShellName}";
        WelcomeLocalShellButton.IsEnabled = localShellName is not null;

        ShowSettings(settings);
        RefreshLogins();
        Loaded += OnLoaded;
        Loaded += (_, _) => HookStoreChanges(true);
        Unloaded += (_, _) => HookStoreChanges(false);
    }

    private string SelectedTheme => _choices.Shown.Theme;

    /// <summary>Re-reads the saved settings after Settings, in any window, changed them. A
    /// setting changed there replaces what was picked here; the other picks stay.</summary>
    public void Refresh(AppSettings saved)
    {
        if (_completed)
            return;
        _choices.Refresh(saved);
        ShowSettings(saved);
    }

    private void ShowSettings(AppSettings saved)
    {
        var shown = _choices.Shown;
        var wasUpdating = _updatingThemeUi;
        _updatingThemeUi = true;
        ConfirmCloseToggle.IsOn = shown.ConfirmCloseActiveSessions;
        CopyOnSelectToggle.IsOn = shown.CopyOnSelect;
        RightClickPasteToggle.IsOn = shown.RightClickPaste;
        CrashReportsToggle.IsOn = shown.WriteCrashReports;
        CommandHistoryToggle.IsOn = shown.KeepCommandHistory;
        CommandHistoryNote.Text =
            $"Search it with {AppShortcuts.Label(Resesh.Core.Input.ShortcutIds.CommandHistory)}. History stays on this computer " +
            $"and is deleted after {saved.CommandHistoryDays} days. Output can include secrets a server prints, " +
            $"so history is off until you turn it on. Change it later in {SettingsCatalog.PathTo(GlobalSettingsTarget.CommandHistory)}.";
        UpdateThemeSelection();
        _updatingThemeUi = wasUpdating;
        UpdateSaveButtonState();
    }

    /// <summary>The identities summary follows identities and folder logins changed elsewhere.
    /// Hooked while loaded: Welcome can be dragged to another window, which unloads it.</summary>
    private void HookStoreChanges(bool attach)
    {
        App.Identities.Changed -= OnStoreChanged;
        App.SessionsChanged -= OnStoreChanged;
        if (!attach)
            return;
        App.Identities.Changed += OnStoreChanged;
        App.SessionsChanged += OnStoreChanged;
    }

    // Identity changes can be raised off the UI thread.
    private void OnStoreChanged() => DispatcherQueue.TryEnqueue(RefreshLogins);

    /// <summary>Summarizes the identities in use, so the section reads as state, not only as advice.</summary>
    private void RefreshLogins()
    {
        var identities = App.Identities.Identities;
        if (identities.Count == 0)
        {
            LoginsStatus.Text = "No identities yet.";
            DefaultLoginButton.Content = "Set a login for all sessions…";
            return;
        }
        var sessions = App.Store.Sessions;
        var users = sessions.Count(s => s.Kind == Resesh.Core.Models.SessionKind.Ssh
            && App.Logins.Resolve(s).Identity is not null);
        var defaultId = App.Store.FolderIdentity("");
        var defaultName = identities.FirstOrDefault(i => i.Id == defaultId)?.Name;
        LoginsStatus.Text = $"{identities.Count} identit{(identities.Count == 1 ? "y" : "ies")}, used by "
            + $"{users} session{(users == 1 ? "" : "s")}."
            + (defaultName is null ? "" : $" Sessions without a folder login use {defaultName}.");
        DefaultLoginButton.Content = defaultName is null ? "Set a login for all sessions…" : "Change the login for all sessions…";
    }

    private async void DefaultLogin_Click(object sender, RoutedEventArgs e)
    {
        await FolderIdentityDialog.EditAsync(XamlRoot, "");
        RefreshLogins();
    }

    private void ManageIdentities_Click(object sender, RoutedEventArgs e) => ManageIdentitiesRequested?.Invoke();

    /// <summary>The settings saved now with what was picked here, and Welcome done.</summary>
    public AppSettings Complete() => _choices.Complete(App.Settings.Current);

    public void MarkCompleted() => _completed = true;

    /// <summary>Closing without finishing puts back the theme saved now, which Settings may
    /// have changed since Welcome opened.</summary>
    public void CancelPreview()
    {
        if (!_completed && _choices.PreviewsTheme)
            _previewTheme(App.Settings.Current.Theme);
    }

    public void Dispose()
    {
        Loaded -= OnLoaded;
        HookStoreChanges(false);
    }

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

    private bool _reviewOpen;

    /// <remarks>One review at a time: every import button is off until the review tab imports
    /// or closes. A second click used to open a second review tab, and both could commit.</remarks>
    private async Task PreviewAndCommitImportAsync(
        ImportScanResult scan,
        Button button,
        TextBlock label,
        string source)
    {
        if (_reviewOpen || ReviewImport is null)
            return;
        _reviewOpen = true;
        Button[] buttons = [PuttyImportButton, OpenSshImportButton, SecureCrtImportButton];
        var wasEnabled = buttons.Select(b => b.IsEnabled).ToArray();
        foreach (var b in buttons)
            b.IsEnabled = false;
        var committed = false;
        try
        {
            if (await ReviewImport(scan, source) is not { } outcome)
                return;
            committed = true;
            var (imported, duplicates, identities) = outcome;

            var identityNote = identities.Count switch
            {
                0 => "",
                1 => $"Created the identity {identities[0]}.",
                _ => $"Created {identities.Count} identities: {string.Join(", ", identities)}.",
            };
            _sessionsChanged();
            label.Text = $"{imported} imported";
            label.Foreground = Brush("OnboardingAccentTextBrush");
            ImportStatus.Severity = InfoBarSeverity.Success;
            ImportStatus.Title = $"{source} import complete";
            ImportStatus.Message = (duplicates == 0
                ? $"Added {imported} session{(imported == 1 ? "" : "s")}."
                : $"Added {imported}; skipped {duplicates} duplicate{(duplicates == 1 ? "" : "s")}.")
                + (identityNote.Length > 0 ? $" {identityNote}" : "");
            RefreshLogins();
            ImportStatus.IsOpen = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ImportStatus.Severity = InfoBarSeverity.Error;
            ImportStatus.Title = $"{source} import failed";
            ImportStatus.Message = exception.Message;
            ImportStatus.IsOpen = true;
        }
        finally
        {
            _reviewOpen = false;
            // A source that imported stays off; the others go back to how they were.
            for (var i = 0; i < buttons.Length; i++)
                buttons[i].IsEnabled = wasEnabled[i] && !(committed && ReferenceEquals(buttons[i], button));
        }
    }

    private void LightTheme_Click(object sender, RoutedEventArgs e) => SelectTheme(ThemeIds.Light);

    private void DarkTheme_Click(object sender, RoutedEventArgs e) => SelectTheme(ThemeIds.Dark);

    private void SystemTheme_Click(object sender, RoutedEventArgs e) => SelectTheme(ThemeIds.System);

    private void SelectTheme(string theme)
    {
        _choices.Choose(_choices.Shown with { Theme = theme });
        _previewTheme(theme);
        UpdateThemeSelection();
        UpdateSaveButtonState();
    }

    private void UpdateThemeSelection()
    {
        var wasUpdating = _updatingThemeUi;
        _updatingThemeUi = true;
        var isSystem = ThemeCatalog.IsSystem(SelectedTheme);
        var isLight = !isSystem && ThemeCatalog.IsLight(SelectedTheme);
        LightThemeToggle.IsChecked = isLight;
        DarkThemeToggle.IsChecked = !isSystem && !isLight;
        SystemThemeToggle.IsChecked = isSystem;

        // Mode is a shortcut, not a second persisted setting. Light and Dark filter
        // the exact palette choices. Selecting a palette owns the mode. System owns
        // the palette and disables the fixed-theme picker while it follows Windows.
        if (isSystem)
        {
            ThemePicker.ItemsSource = new[] { ThemeCatalog.Find(ThemeIds.System) };
            ThemePicker.SelectedIndex = 0;
            ThemePicker.IsEnabled = false;
        }
        else
        {
            var choices = ThemeCatalog.All
                .Where(theme => theme.Id != ThemeIds.System && theme.IsLight == isLight)
                .ToList();
            ThemePicker.ItemsSource = choices;
            ThemePicker.SelectedItem = choices.FirstOrDefault(theme =>
                string.Equals(theme.Id, SelectedTheme, StringComparison.OrdinalIgnoreCase));
            ThemePicker.IsEnabled = true;
        }

        var resolved = App.ResolveTheme(SelectedTheme);
        var palette = ThemeVisualPalette.For(resolved);
        ApplyPagePalette(palette);
        if (isSystem)
        {
            var currentMode = resolved == ThemeIds.Light ? "Light" : "Dark";
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
        if (_updatingThemeUi)
            return;
        _choices.Choose(_choices.Shown with
        {
            ConfirmCloseActiveSessions = ConfirmCloseToggle.IsOn,
            CopyOnSelect = CopyOnSelectToggle.IsOn,
            RightClickPaste = RightClickPasteToggle.IsOn,
            WriteCrashReports = CrashReportsToggle.IsOn,
            KeepCommandHistory = CommandHistoryToggle.IsOn,
        });
        UpdateSaveButtonState();
    }

    private void UpdateSaveButtonState()
    {
        if (FinishSetupButton is null)
            return;

        FinishSetupButton.IsEnabled = _choices.HasChanges(App.Settings.Current);
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
