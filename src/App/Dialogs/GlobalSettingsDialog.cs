using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Storage;
using static Resesh.App.Dialogs.SettingsLayout;

namespace Resesh.App.Dialogs;

public enum GlobalSettingsTarget
{
    General,
    Theme,
    FontFamily,
    FontSize,
    Scrollback,
    CopyOnSelect,
    RightClickPaste,
    ShowStatusBar,
    ReopenLastLayout,
    Recording,
    RecordingDirectory,
    AlwaysRecord,
    RewindMinutes,
    RewindMegabytes,
    Highlighting,
    Agents,
    ShowAgentIcons,
    AgentAlertFlash,
    AgentAlertSound,
}

/// <summary>
/// Edits settings that apply to the whole app, as one tabbed dialog (General / Recording /
/// Highlighting / Agents). The Highlighting tab hosts the rule editor
/// inline. Theme and highlighting edits are reversible previews until Save; Cancel
/// discards the draft. The tab host has a fixed height so
/// the dialog doesn't resize when switching tabs.
/// </summary>
public static class GlobalSettingsDialog
{
    private const double PreferredDialogWidth = 920;
    // One content budget across tabs. Long pages and rule forms scroll within it.
    private const double PreferredTabContentHeight = 660;
    private const double DialogHorizontalChrome = 72;
    private const double DialogVerticalChrome = 180;
    private const double StackedRowThreshold = 620;

    public static async Task<AppSettings?> ShowAsync(
        XamlRoot xamlRoot,
        AppSettings current,
        Action<string> applyThemePreview,
        Action<HighlightsStore?> applyHighlightPreview,
        GlobalSettingsTarget initialTarget = GlobalSettingsTarget.General)
    {
        var (dialogWidth, tabContentHeight) = GetDialogContentSize(xamlRoot);
        var stackRows = dialogWidth < StackedRowThreshold;

        var theme = new ComboBox
        {
            Header = "Theme",
            ItemsSource = ThemeCatalog.All,
            SelectedItem = ThemeCatalog.Find(current.Theme),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        string PreviewTheme() => (theme.SelectedItem as ThemeChoice)?.Id ?? current.Theme;
        var fontFamily = new TextBox { Header = "Terminal font family", Text = current.FontFamily };
        var fontSize = new NumberBox
        {
            Header = "Font size",
            Value = current.FontSize,
            Minimum = 8,
            Maximum = 32,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var scrollback = new NumberBox
        {
            Header = "Scrollback lines",
            Value = current.Scrollback,
            Minimum = 1000,
            Maximum = 100000,
            SmallChange = 1000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var copyOnSelect = new ToggleSwitch { Header = "Copy selected text", IsOn = current.CopyOnSelect };
        var rightClickPaste = new ToggleSwitch { Header = "Paste with right-click", IsOn = current.RightClickPaste };
        var showStatusBar = new ToggleSwitch
        {
            Header = WrappingHeader("Show status bar"),
            IsOn = current.ShowStatusBar,
        };
        var reopenLastLayout = new ToggleSwitch
        {
            Header = WrappingHeader("Reopen last layout at startup"),
            IsOn = current.ReopenLastLayoutAtStartup,
        };

        var recordingDirectory = new TextBox
        {
            Header = "Recording directory",
            Text = current.RecordingDirectory,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var alwaysRecord = new ToggleSwitch
        {
            Header = WrappingHeader("Record new sessions automatically"),
            IsOn = current.AlwaysRecord,
        };
        var rewindMinutes = new NumberBox
        {
            Header = "Rewind history (minutes)",
            Value = current.RewindMinutes,
            Minimum = 1,
            Maximum = 1440,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var rewindMegabytes = new NumberBox
        {
            Header = "Memory limit per tab (MiB)",
            Value = current.RewindMegabytes,
            Minimum = 1,
            Maximum = 1024,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var agentIcons = new ToggleSwitch
        {
            Header = WrappingHeader("Show agent icons"),
            IsOn = current.ShowAgentIcons,
        };
        var agentFlash = new ToggleSwitch
        {
            Header = WrappingHeader("Flash the taskbar"),
            IsOn = current.AgentAlertFlash,
        };
        var agentSound = new ToggleSwitch
        {
            Header = WrappingHeader("Play the notification sound"),
            IsOn = current.AgentAlertSound,
        };

        SetAutomationId(theme, "SettingsTheme");
        SetAutomationId(fontFamily, "SettingsFontFamily");
        SetAutomationId(showStatusBar, "SettingsShowStatusBar");
        SetAutomationId(fontSize, "SettingsFontSize");
        SetAutomationId(scrollback, "SettingsScrollback");
        SetAutomationId(copyOnSelect, "SettingsCopyOnSelect");
        SetAutomationId(rightClickPaste, "SettingsRightClickPaste");
        SetAutomationId(reopenLastLayout, "SettingsReopenLastLayout");
        SetAutomationId(recordingDirectory, "SettingsRecordingDirectory");
        SetAutomationId(alwaysRecord, "SettingsAlwaysRecord");
        SetAutomationId(rewindMinutes, "SettingsRewindMinutes");
        SetAutomationId(rewindMegabytes, "SettingsRewindMegabytes");
        SetAutomationId(agentIcons, "SettingsShowAgentIcons");
        SetAutomationId(agentFlash, "SettingsAgentAlertFlash");
        SetAutomationId(agentSound, "SettingsAgentAlertSound");

        void SyncAgentAlertControls()
        {
            agentFlash.IsEnabled = agentIcons.IsOn;
            agentSound.IsEnabled = agentIcons.IsOn;
        }
        agentIcons.Toggled += (_, _) => SyncAgentAlertControls();
        SyncAgentAlertControls();

        // ---- General ----

        var settingRows = new List<Grid>();
        Grid Row(string label, FrameworkElement control)
        {
            var row = SettingRow(label, control, stackRows);
            settingRows.Add(row);
            return row;
        }
        var generalTab = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Description("These settings apply throughout resesh. Saved sessions can override terminal defaults."),
                SettingsGroup("Appearance",
                    Row("Theme", theme),
                    Row("Terminal font family", fontFamily),
                    Row("Font size", fontSize)),
                SettingsGroup("Terminal",
                    Row("Scrollback lines", scrollback),
                    Row("Copy selected text", copyOnSelect),
                    Row("Paste with right-click", rightClickPaste)),
                SettingsGroup("Startup and interface",
                    Row("Show status bar", showStatusBar),
                    Row("Reopen last layout at startup", reopenLastLayout)),
            },
        };

        // ---- Recording ----

        var recordingTab = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Description("Record terminal output to disk, or keep bounded in-memory history for instant rewind."),
                SettingsGroup("Disk recording",
                    Description("Each recording writes an asciicast .cast file and a timestamped .log rendered from committed terminal lines. Both can include secrets that a server prints."),
                    Row("Recording directory", recordingDirectory),
                    Row("Record new sessions automatically", alwaysRecord)),
                SettingsGroup("Instant rewind",
                    Description("Rewind data stays in memory and is deleted when the tab closes."),
                    Row("Rewind history (minutes)", rewindMinutes),
                    Row("Memory limit per tab (MiB)", rewindMegabytes)),
            },
        };

        var host = new ScrollViewer
        {
            Width = dialogWidth,
            // Keep tab content clear of the vertical scrollbar. Without this gutter,
            // full-width cards can render underneath the scrollbar and lose their right border.
            Padding = new Thickness(0, 0, 20, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // ---- Highlighting ----

        // Fixed-height grid (not a stack): the editor's rules list takes the star row so it
        // expands to fill the tab, keeping the preview section pinned above the caption.
        var highlightingTab = new Grid { Height = tabContentHeight, RowSpacing = 16 };
        highlightingTab.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        highlightingTab.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        highlightingTab.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var highlightingDesc = Description("Enable the built-in network rules, and create or edit custom regular-expression rules.");
        var highlightDraft = App.Highlights.CreateDraft();
        var editingHighlight = false;
        void UpdateHighlightingHeight() => highlightingTab.Height = editingHighlight
            ? double.NaN
            : Math.Max(400, host.ViewportHeight > 0 ? host.ViewportHeight : tabContentHeight);
        // Use the measured viewport: ScrollViewer chrome can make it smaller than Height.
        host.Loaded += (_, _) => UpdateHighlightingHeight();
        host.SizeChanged += (_, _) => UpdateHighlightingHeight();
        SetAutomationId(host, "SettingsTabContent");
        var highlightingEditor = HighlightEditorPanel.Create(highlightDraft, () => applyHighlightPreview(highlightDraft), Row,
            editing =>
            {
                editingHighlight = editing;
                UpdateHighlightingHeight();
                host.ChangeView(null, 0, null, disableAnimation: true);
            });
        var highlightingCaption = Caption("Changes preview in open terminals. Save keeps them; Cancel restores the saved rules.");
        Grid.SetRow(highlightingDesc, 0);
        Grid.SetRow((FrameworkElement)highlightingEditor, 1);
        Grid.SetRow(highlightingCaption, 2);
        highlightingTab.Children.Add(highlightingDesc);
        highlightingTab.Children.Add(highlightingEditor);
        highlightingTab.Children.Add(highlightingCaption);

        // ---- Agents ----

        var agentsTab = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Description("Track supported coding agents in each terminal tab. resesh can notify you when an agent needs a response."),
                SettingsGroup("Tab display",
                    Description("Replace a session icon while resesh recognizes a supported agent in that tab."),
                    Row("Show agent icons", agentIcons)),
                SettingsGroup("Background alerts",
                    Description("Get your attention when an agent waits for a response. Turn on agent icons to use alerts."),
                    Row("Flash the taskbar", agentFlash),
                    Row("Play the notification sound", agentSound)),
                SettingsGroup("Agent adapters",
                    Description("resesh identifies supported agents automatically. Add an adapter only for exact working, waiting, and finished states."),
                    AgentAdapterPanel.Create()),
            },
        };

        // ---- tab host: fixed height so the dialog doesn't resize between tabs. Content is
        // swapped (not visibility-toggled) so each tab gets a fresh measure — a TextBox
        // measured while collapsed keeps a stale one-line text layout when merely unhidden. ----

        var initialTab = initialTarget switch
        {
            GlobalSettingsTarget.Recording or GlobalSettingsTarget.RecordingDirectory
                or GlobalSettingsTarget.AlwaysRecord or GlobalSettingsTarget.RewindMinutes
                or GlobalSettingsTarget.RewindMegabytes => 1,
            GlobalSettingsTarget.Highlighting => 2,
            GlobalSettingsTarget.Agents or GlobalSettingsTarget.ShowAgentIcons
                or GlobalSettingsTarget.AgentAlertFlash or GlobalSettingsTarget.AgentAlertSound => 3,
            _ => 0,
        };
        Control? initialFocus = initialTarget switch
        {
            GlobalSettingsTarget.Theme => theme,
            GlobalSettingsTarget.FontFamily => fontFamily,
            GlobalSettingsTarget.ShowStatusBar => showStatusBar,
            GlobalSettingsTarget.FontSize => fontSize,
            GlobalSettingsTarget.Scrollback => scrollback,
            GlobalSettingsTarget.CopyOnSelect => copyOnSelect,
            GlobalSettingsTarget.RightClickPaste => rightClickPaste,
            GlobalSettingsTarget.ReopenLastLayout => reopenLastLayout,
            GlobalSettingsTarget.RecordingDirectory => recordingDirectory,
            GlobalSettingsTarget.AlwaysRecord => alwaysRecord,
            GlobalSettingsTarget.RewindMinutes => rewindMinutes,
            GlobalSettingsTarget.RewindMegabytes => rewindMegabytes,
            GlobalSettingsTarget.ShowAgentIcons => agentIcons,
            GlobalSettingsTarget.AgentAlertFlash => agentFlash,
            GlobalSettingsTarget.AgentAlertSound => agentSound,
            _ => null,
        };

        var tabPanels = new UIElement[] { generalTab, recordingTab, highlightingTab, agentsTab };
        var bar = new SelectorBar();
        var barItems = new[]
        {
            new SelectorBarItem { Text = "General" },
            new SelectorBarItem { Text = "Recording" },
            new SelectorBarItem { Text = "Highlighting" },
            new SelectorBarItem { Text = "Agents" },
        };
        SetAutomationId(bar, "SettingsSectionSelector");
        SetAutomationId(barItems[0], "SettingsGeneralTab");
        SetAutomationId(barItems[1], "SettingsRecordingTab");
        SetAutomationId(barItems[2], "SettingsHighlightingTab");
        SetAutomationId(barItems[3], "SettingsAgentsTab");
        foreach (var item in barItems)
            bar.Items.Add(item);

        void ShowTab(int index)
        {
            host.Content = tabPanels[index];
            host.ChangeView(null, 0, null, disableAnimation: true);
        }

        bar.SelectionChanged += (s, _) =>
        {
            var index = Array.IndexOf(barItems, s.SelectedItem);
            if (index >= 0)
                ShowTab(index);
        };
        bar.SelectedItem = barItems[initialTab];
        ShowTab(initialTab);

        var saveError = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
        // Let the tab viewport shrink within the content budget. A StackPanel measures
        // its children at infinite height, so ContentDialog can clip the host and footer.
        var content = new Grid
        {
            Height = tabContentHeight,
            RowSpacing = 12,
            Children = { saveError, bar, host },
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(bar, 1);
        Grid.SetRow(host, 2);

        // DefaultButton stays None on purpose: Enter while typing in the highlighting rule
        // form must not save-and-close the whole dialog.
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = xamlRoot,
            Background = (Brush)Application.Current.Resources["SessionShellBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SettingsCardBorderBrush"],
            Foreground = (Brush)Application.Current.Resources["SessionTreeForegroundBrush"],
        };
        SetAutomationId(dialog, "GlobalSettingsDialog");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { App.Highlights.CommitDraft(highlightDraft); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                saveError.Message = "Could not save highlighting rules. Try again or cancel.";
                saveError.IsOpen = true;
                args.Cancel = true;
                App.ReportRecoverableError(exception);
            }
        };
        DialogTheme.Apply(dialog, PreviewTheme());
        theme.SelectionChanged += (_, _) =>
        {
            var previewTheme = PreviewTheme();
            applyThemePreview(previewTheme);
            DialogTheme.SetRequestedTheme(dialog, previewTheme);
        };
        dialog.Opened += (_, _) =>
            initialFocus?.DispatcherQueue.TryEnqueue(() => initialFocus.Focus(FocusState.Programmatic));
        void UpdateDialogLayout()
        {
            (dialogWidth, tabContentHeight) = GetDialogContentSize(xamlRoot);
            var shouldStackRows = dialogWidth < StackedRowThreshold;
            if (shouldStackRows != stackRows)
            {
                stackRows = shouldStackRows;
                foreach (var row in settingRows)
                    ConfigureSettingRow(row, stackRows);
            }
            host.Width = dialogWidth;
            content.Height = tabContentHeight;
            UpdateHighlightingHeight();
            dialog.Resources["ContentDialogMaxWidth"] = Math.Min(
                PreferredDialogWidth + 48,
                Math.Max(280, xamlRoot.Size.Width - 24));
            dialog.Resources["ContentDialogMaxHeight"] = Math.Min(
                960d,
                Math.Max(280, xamlRoot.Size.Height - 24));
        }

        void XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateDialogLayout();
        UpdateDialogLayout();
        xamlRoot.Changed += XamlRootChanged;

        ContentDialogResult result = ContentDialogResult.None;
        try
        {
            result = await dialog.ShowModalAsync();
        }
        finally
        {
            xamlRoot.Changed -= XamlRootChanged;
            applyHighlightPreview(null);
            if (result != ContentDialogResult.Primary)
                applyThemePreview(current.Theme);
        }

        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return current with
        {
            Theme = (theme.SelectedItem as ThemeChoice)?.Id ?? "dark",
            FontFamily = string.IsNullOrWhiteSpace(fontFamily.Text) ? current.FontFamily : fontFamily.Text.Trim(),
            FontSize = double.IsNaN(fontSize.Value) ? current.FontSize : (int)fontSize.Value,
            Scrollback = double.IsNaN(scrollback.Value) ? current.Scrollback : (int)scrollback.Value,
            CopyOnSelect = copyOnSelect.IsOn,
            RightClickPaste = rightClickPaste.IsOn,
            ShowStatusBar = showStatusBar.IsOn,
            ReopenLastLayoutAtStartup = reopenLastLayout.IsOn,
            AlwaysRecord = alwaysRecord.IsOn,
            RecordingDirectory = string.IsNullOrWhiteSpace(recordingDirectory.Text)
                ? current.RecordingDirectory
                : recordingDirectory.Text.Trim(),
            RewindMinutes = double.IsNaN(rewindMinutes.Value) ? current.RewindMinutes : (int)rewindMinutes.Value,
            RewindMegabytes = double.IsNaN(rewindMegabytes.Value) ? current.RewindMegabytes : (int)rewindMegabytes.Value,
            ShowAgentIcons = agentIcons.IsOn,
            AgentAlertFlash = agentFlash.IsOn,
            AgentAlertSound = agentSound.IsOn,
        };
    }

    private static TextBlock Description(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75,
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Opacity = 0.55,
    };

    private static TextBlock WrappingHeader(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 300,
    };

    private static void SetAutomationId(DependencyObject element, string automationId) =>
        AutomationProperties.SetAutomationId(element, automationId);

    private static (double Width, double Height) GetDialogContentSize(XamlRoot xamlRoot) =>
        (Math.Min(PreferredDialogWidth, Math.Max(240, xamlRoot.Size.Width - DialogHorizontalChrome)),
         Math.Min(PreferredTabContentHeight, Math.Max(180, xamlRoot.Size.Height - DialogVerticalChrome)));

}
