using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Resesh.App.Dialogs;
using Resesh.App.ViewModels;
using Resesh.Core.Import;
using Resesh.Core.Storage;
using Resesh.Terminal;
using Windows.UI;

namespace Resesh.App.Controls;

public sealed partial class OnboardingView : UserControl, IDisposable
{
    private readonly WelcomeChoices _choices;
    private readonly Action<string> _previewTheme;
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
        string? localShellName)
    {
        _choices = new WelcomeChoices(settings);
        _previewTheme = previewTheme;

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
    private void OnStoreChanged() => DispatcherQueue.TryPost(RefreshLogins);

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
            _previewTheme(App.CurrentTheme);
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

    private static readonly IReadOnlyList<ThemeChoice> LightThemes = [.. ThemeCatalog.All.Where(theme => theme.IsLight)];
    private static readonly IReadOnlyList<ThemeChoice> DarkThemes = [.. ThemeCatalog.All.Where(theme => !theme.IsLight)];

    private void SystemTheme_Click(object sender, RoutedEventArgs e) =>
        Choose(_choices.Shown with { Appearance = ThemeAppearances.System });

    /// <summary>Leaving System keeps the theme drawn now, as Settings does.</summary>
    private void FixedTheme_Click(object sender, RoutedEventArgs e) =>
        Choose(_choices.Shown.Appearance == ThemeAppearances.System
            ? _choices.Shown.WithFixedTheme(_choices.Shown.ThemeFor(App.SystemIsLight()))
            : _choices.Shown);

    private void Choose(WelcomeValues shown)
    {
        _choices.Choose(shown);
        _previewTheme(shown.ThemeFor(App.SystemIsLight()));
        UpdateThemeSelection();
        UpdateSaveButtonState();
    }

    private void UpdateThemeSelection()
    {
        var wasUpdating = _updatingThemeUi;
        _updatingThemeUi = true;
        var shown = _choices.Shown;
        var isSystem = shown.Appearance == ThemeAppearances.System;
        var resolved = shown.ThemeFor(App.SystemIsLight());
        var isLight = ThemeCatalog.IsLight(resolved);
        SystemThemeToggle.IsChecked = isSystem;
        FixedThemeToggle.IsChecked = !isSystem;

        // Theme lists every theme (with a light/dark filter) and fixes the one picked. System
        // lists the themes of the mode Windows is in and sets that mode's; the other mode's
        // theme stays as it was.
        ThemeFilter.ShowModes = !isSystem;
        var choices = !isSystem ? ThemeCatalog.All : isLight ? LightThemes : DarkThemes;
        if (!ReferenceEquals(ThemePicker.Themes, choices))
            ThemePicker.Themes = choices;
        ThemePicker.SelectedTheme = ThemeCatalog.Find(resolved);

        ApplyPagePalette(ThemeVisualPalette.For(resolved));
        if (isSystem)
        {
            SystemThemeHint.Text = $"System follows your Windows color mode (currently {(isLight ? "Light" : "Dark")}), "
                + $"and this picks the {(isLight ? "light" : "dark")} theme. Choose both in {SettingsCatalog.PathTo(SettingsSection.Theme)}.";
            SystemThemeHint.Visibility = Visibility.Visible;
        }
        else
        {
            SystemThemeHint.Visibility = Visibility.Collapsed;
        }
        _updatingThemeUi = wasUpdating;
    }

    private void ThemePicker_SelectionChanged(object? sender, ThemeChoice theme)
    {
        if (_updatingThemeUi)
            return;
        Choose(_choices.Shown.Appearance == ThemeAppearances.System
            ? _choices.Shown.WithModeTheme(theme.Id)
            : _choices.Shown.WithFixedTheme(theme.Id));
    }

    private void ThemeFilter_QueryChanged(object? sender, EventArgs e) => ThemePicker.Query = ThemeFilter.Query;

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
        // The page sits on the selected tab's color (SessionPageBrush), so contrast is measured there.
        var page = palette.ActiveTab;
        var primary = EnsureContrast(palette.TreeForeground, page, 4.5);
        var secondary = EnsureContrast(palette.TreeMutedForeground, page, 4.5);
        var accentText = EnsureContrast(palette.Accent, page, 4.5);
        var accentForeground = ColorMath.PrefersBlackText(RgbColor.ToRgb(palette.Accent)) ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        var previewBorder = EnsureContrast(palette.Frame, page, 3.0);

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

    private static Color EnsureContrast(Color foreground, Color background, double minimum) =>
        RgbColor.ToColor(ColorMath.Readable(RgbColor.ToRgb(foreground), RgbColor.ToRgb(background), minimum));

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
