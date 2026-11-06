using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.ViewModels;
using Resesh.Core.Input;
using Resesh.Core.Storage;

namespace Resesh.App.Controls;

/// <summary>
/// Settings, hosted as an app page in a tab. Every change applies as it is made; there is
/// no Save or Cancel. Highlighting rules commit after each saved, toggled, or deleted rule.
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    private readonly HighlightsStore _highlightDraft = App.Highlights.CreateDraft();

    public SettingsViewModel ViewModel { get; }

    /// <summary>The Sessions section, for the window to connect to its view model and editors.</summary>
    public SessionsManagerView SessionsView => SessionsManager;

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ThemeFilter.ShowModes = !ViewModel.IsFollowingWindows;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SettingsViewModel.IsFollowingWindows))
                ShowThemeFilter();
        };
        NumberBoxRanges.Limit(FontSizeBox, SettingRanges.FontSize);
        NumberBoxRanges.Limit(ScrollbackBox, SettingRanges.Scrollback);
        NumberBoxRanges.Limit(HistoryDaysBox, SettingRanges.CommandHistoryDays);
        HistoryDescription.Text =
            "Save each finished command with its output, folder, and result on this computer, and search it with "
            + $"{AppShortcuts.Label(ShortcutIds.CommandHistory)}. Output can include secrets that a server prints. "
            + "A saved session can turn history off in its options.";
        var highlightEditor = new HighlightRulesEditor(_highlightDraft);
        highlightEditor.Changed += CommitHighlights;
        HighlightEditorHost.Content = highlightEditor;
        HookIdentityChanges();
        SshAlgorithmsHost.Content = BuildSshAlgorithms();
        Show(SettingsSection.General);
    }

    /// <summary>Shows the section holding <paramref name="target"/>, scrolls to it, and focuses it.</summary>
    public void Navigate(GlobalSettingsTarget target)
    {
        Show(SettingsCatalog.Of(target).Section);
        if (target == GlobalSettingsTarget.Sessions)
            DispatcherQueue.TryPost(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SessionsManager.FocusFilter);
        if (FieldOf(target) is not var (card, control))
            return;
        // The section was collapsed until now; place and focus after its first layout pass.
        DispatcherQueue.TryPost(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            card.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3, AnimationDesired = false });
            control.Focus(FocusState.Programmatic);
        });
    }

    private (FrameworkElement Card, Control Control)? FieldOf(GlobalSettingsTarget target) => target switch
    {
        GlobalSettingsTarget.Appearance => (AppearanceCard, AppearanceBox),
        GlobalSettingsTarget.ThemeColors => (ThemeColorsCard, ThemeColorsBox),
        // Under Theme there is one gallery; System has one for each Windows mode.
        GlobalSettingsTarget.LightTheme when !ViewModel.IsFollowingWindows => (FixedThemeHeader, FixedGallery.FocusTarget),
        GlobalSettingsTarget.DarkTheme when !ViewModel.IsFollowingWindows => (FixedThemeHeader, FixedGallery.FocusTarget),
        GlobalSettingsTarget.LightTheme => (LightThemeHeader, LightGallery.FocusTarget),
        GlobalSettingsTarget.DarkTheme => (DarkThemeHeader, DarkGallery.FocusTarget),
        GlobalSettingsTarget.FontFamily => (FontFamilyCard, FontFamilyBox.FocusTarget),
        GlobalSettingsTarget.FallbackFonts => (FallbackFontsCard, FallbackFontsBox),
        GlobalSettingsTarget.FontSize => (FontSizeCard, FontSizeBox),
        GlobalSettingsTarget.Scrollback => (ScrollbackCard, ScrollbackBox),
        GlobalSettingsTarget.CopyOnSelect => (CopyOnSelectCard, CopyOnSelectSwitch),
        GlobalSettingsTarget.RightClickPaste => (RightClickPasteCard, RightClickPasteSwitch),
        GlobalSettingsTarget.MouseCapture => (MouseCaptureCard, MouseCaptureBox),
        GlobalSettingsTarget.ConfirmMultilinePaste => (ConfirmMultilinePasteCard, ConfirmMultilinePasteSwitch),
        GlobalSettingsTarget.ShowStatusBar => (ShowStatusBarCard, ShowStatusBarSwitch),
        GlobalSettingsTarget.SessionsPaneLayout => (SessionsPaneLayoutCard, SessionsPaneLayoutBox),
        GlobalSettingsTarget.ReopenLastLayout => (ReopenLastLayoutCard, ReopenLastLayoutSwitch),
        GlobalSettingsTarget.LaunchAtSignIn => (LaunchAtSignInCard, LaunchAtSignInSwitch),
        GlobalSettingsTarget.ConfirmCloseActiveSessions => (ConfirmCloseCard, ConfirmCloseSwitch),
        GlobalSettingsTarget.WriteCrashReports => (CrashReportsCard, CrashReportsSwitch),
        GlobalSettingsTarget.RecordingDirectory => (RecordingDirectoryCard, RecordingDirectoryBox),
        GlobalSettingsTarget.AlwaysRecord => (AlwaysRecordCard, AlwaysRecordSwitch),
        GlobalSettingsTarget.RewindMinutes => (RewindMinutesCard, RewindMinutesBox),
        GlobalSettingsTarget.RewindMegabytes => (RewindMegabytesCard, RewindMegabytesBox),
        GlobalSettingsTarget.CommandHistory => (KeepHistoryCard, KeepHistorySwitch),
        GlobalSettingsTarget.CommandHistoryDays => (HistoryDaysCard, HistoryDaysBox),
        GlobalSettingsTarget.ShowAgentIcons => (AgentIconsCard, AgentIconsSwitch),
        GlobalSettingsTarget.AgentAlertFlash => (AgentFlashCard, AgentFlashSwitch),
        GlobalSettingsTarget.AgentAlertSound => (AgentSoundCard, AgentSoundSwitch),
        GlobalSettingsTarget.SshAlgorithms => (SshAlgorithmsHeader, (Control)SshAlgorithmsHost.Content),
        _ => null,
    };

    /// <summary>Saves each change that leaves every kind with something on; until then the
    /// picker shows what is wrong and the saved setting stays as it was. The picker is not a
    /// binding, so it follows the view model's change notifications itself: Settings in
    /// another window can change the setting, and a save here writes only the kinds changed
    /// here, so it never puts back what that window turned off.</summary>
    private SshAlgorithmPicker BuildSshAlgorithms()
    {
        var shown = ViewModel.SshAlgorithms;
        var picker = new SshAlgorithmPicker(followsApp: false) { Value = shown };
        picker.SetAppDefaults(null);
        picker.Changed += () =>
        {
            if (picker.Error is null)
            {
                ViewModel.ChangeSshAlgorithms(shown, picker.Value);
                shown = ViewModel.SshAlgorithms;
            }
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SettingsViewModel.SshAlgorithms))
                picker.Value = shown = ViewModel.SshAlgorithms;
        };
        return picker;
    }

    private void Show(SettingsSection section)
    {
        GeneralSection.Visibility = PresentationValues.Visible(section == SettingsSection.General);
        ThemeSection.Visibility = PresentationValues.Visible(section == SettingsSection.Theme);
        SessionsSection.Visibility = PresentationValues.Visible(section == SettingsSection.Sessions);
        IdentitiesSection.Visibility = PresentationValues.Visible(section == SettingsSection.Identities);
        RecordingSection.Visibility = PresentationValues.Visible(section == SettingsSection.Recording);
        HighlightingSection.Visibility = PresentationValues.Visible(section == SettingsSection.Highlighting);
        AgentsSection.Visibility = PresentationValues.Visible(section == SettingsSection.Agents);
        ShortcutsSection.Visibility = PresentationValues.Visible(section == SettingsSection.Shortcuts);
        AboutSection.Visibility = PresentationValues.Visible(section == SettingsSection.About);
        var item = section switch
        {
            SettingsSection.Theme => ThemeItem,
            SettingsSection.Sessions => SessionsItem,
            SettingsSection.Identities => IdentitiesItem,
            SettingsSection.Recording => RecordingItem,
            SettingsSection.Highlighting => HighlightingItem,
            SettingsSection.Agents => AgentsItem,
            SettingsSection.Shortcuts => ShortcutsItem,
            SettingsSection.About => AboutItem,
            _ => GeneralItem,
        };
        if (!ReferenceEquals(Sections.SelectedItem, item))
            Sections.SelectedItem = item;
        if (section == SettingsSection.Recording)
            ViewModel.RefreshHistoryUsage();
        if (section == SettingsSection.Identities)
            RefreshIdentities();
        if (section == SettingsSection.Sessions)
            SessionsManager.Refresh();
        if (section == SettingsSection.Theme)
            DispatcherQueue.TryPost(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                FixedGallery.ScrollToSelection();
                LightGallery.ScrollToSelection();
                DarkGallery.ScrollToSelection();
            });
    }

    private void Sections_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag && Enum.TryParse<SettingsSection>(tag, out var section))
            Show(section);
    }

    /// <summary>Light and dark are a filter only in Theme's one gallery of every theme.</summary>
    private void ShowThemeFilter()
    {
        ThemeFilter.ShowModes = !ViewModel.IsFollowingWindows;
        FixedGallery.ScrollToSelection();
    }

    private void ThemeFilter_QueryChanged(object? sender, EventArgs args)
    {
        FixedGallery.Query = ThemeFilter.Query;
        LightGallery.Query = ThemeFilter.Query;
        DarkGallery.Query = ThemeFilter.Query;
    }

    private void SettingsPage_ActualThemeChanged(FrameworkElement sender, object args) =>
        ViewModel.ThemeModeChanged();

    // Measured on the page, not the window: Settings can sit in a narrow split.
    private void Sections_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var mode = e.NewSize.Width switch
        {
            >= 760 => NavigationViewPaneDisplayMode.Left,
            >= 520 => NavigationViewPaneDisplayMode.LeftCompact,
            _ => NavigationViewPaneDisplayMode.Top,
        };
        if (Sections.PaneDisplayMode != mode)
            Sections.PaneDisplayMode = mode;
        // Switching to the compact rail keeps an already-open pane open; close it with the change.
        Sections.IsPaneOpen = mode == NavigationViewPaneDisplayMode.Left;
    }

    private void CommitHighlights()
    {
        try
        {
            App.Highlights.CommitDraft(_highlightDraft);
            HighlightSaveError.IsOpen = false; // every window's terminals follow the store
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The draft keeps the change; the next edit commits it again.
            HighlightSaveError.IsOpen = true;
            App.ReportRecoverableError(exception);
        }
    }

    private void ConfirmClearHistory_Click(object sender, RoutedEventArgs e)
    {
        ClearHistoryFlyout.Hide();
        ViewModel.ClearHistoryCommand.Execute(null);
    }
}
