using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Storage;

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
/// Edits settings that apply to the whole app, as one tabbed dialog (General /
/// Highlighting / Agents) — no child dialogs. The Highlighting tab hosts the rule editor
/// inline and its changes persist immediately (same model as the tab toggles); everything
/// else is a draft returned on Save, null on Cancel. The tab host has a fixed height so
/// the dialog doesn't resize when switching tabs.
/// </summary>
public static class GlobalSettingsDialog
{
    private const double DialogWidth = 760;
    // Tall enough that the Highlighting tab's rule form (the tallest view) fits without
    // scrolling; the host ScrollViewer only kicks in when the window itself is too short.
    private const double TabContentHeight = 660;

    public static async Task<AppSettings?> ShowAsync(
        XamlRoot xamlRoot,
        AppSettings current,
        Action<string> applyThemePreview,
        Action applyHighlightChanges,
        GlobalSettingsTarget initialTarget = GlobalSettingsTarget.General)
    {
        var theme = new ComboBox
        {
            Header = "Theme",
            ItemsSource = ThemeCatalog.All,
            SelectedItem = ThemeCatalog.Find(current.Theme),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        string PreviewTheme() => (theme.SelectedItem as ThemeChoice)?.Id ?? current.Theme;
        theme.SelectionChanged += (_, _) => applyThemePreview(PreviewTheme());
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

        void SyncAgentAlertControls()
        {
            agentFlash.IsEnabled = agentIcons.IsOn;
            agentSound.IsEnabled = agentIcons.IsOn;
        }
        agentIcons.Toggled += (_, _) => SyncAgentAlertControls();
        SyncAgentAlertControls();

        // ---- General ----

        var numberGrid = new Grid { ColumnSpacing = 12 };
        numberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        numberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(fontSize, 0);
        Grid.SetColumn(scrollback, 1);
        numberGrid.Children.Add(fontSize);
        numberGrid.Children.Add(scrollback);

        var generalColumns = new Grid { ColumnSpacing = 16 };
        generalColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        generalColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var appearanceCard = SectionCard("Appearance", "Set the default look for every terminal.", theme, fontFamily, numberGrid);
        var interactionCard = SectionCard("Terminal interaction", null, copyOnSelect, rightClickPaste);
        appearanceCard.VerticalAlignment = VerticalAlignment.Top;
        interactionCard.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(appearanceCard, 0);
        Grid.SetColumn(interactionCard, 1);
        generalColumns.Children.Add(appearanceCard);
        generalColumns.Children.Add(interactionCard);

        var generalTab = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Description("These settings apply throughout Resesh. A saved session can override supported terminal and highlighting defaults."),
                generalColumns,
                SectionCard(
                    "Startup",
                    "The current ordered tab groups are saved on clean exit. Reopening adopts each saved session into its previous group.",
                    reopenLastLayout),
            },
        };

        // ---- Recording ----

        var rewindGrid = new Grid { ColumnSpacing = 12 };
        rewindGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rewindGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(rewindMinutes, 0);
        Grid.SetColumn(rewindMegabytes, 1);
        rewindGrid.Children.Add(rewindMinutes);
        rewindGrid.Children.Add(rewindMegabytes);

        var recordingTab = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Description("Record terminal output to disk, or keep bounded in-memory history for instant rewind."),
                SectionCard(
                    "Disk recording",
                    "Each recording writes an asciicast .cast file and a timestamped .log rendered from committed terminal lines. Both can include secrets that a server prints.",
                    recordingDirectory,
                    alwaysRecord),
                SectionCard(
                    "Instant rewind",
                    "Rewind data stays in memory and is deleted when the tab closes.",
                    rewindGrid),
            },
        };

        // ---- Highlighting ----

        // Fixed-height grid (not a stack): the editor's rules list takes the star row so it
        // expands to fill the tab, keeping the preview section pinned above the caption.
        var highlightingTab = new Grid { Height = TabContentHeight, RowSpacing = 12 };
        highlightingTab.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        highlightingTab.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        highlightingTab.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var highlightingDesc = Description("Enable the built-in network rules, and create or edit custom regular-expression rules.");
        var highlightingEditor = HighlightEditorPanel.Create(applyHighlightChanges);
        var highlightingCaption = Caption("Highlighting changes apply immediately and push to open terminals. Save below applies to the other tabs.");
        Grid.SetRow(highlightingDesc, 0);
        Grid.SetRow((FrameworkElement)highlightingEditor, 1);
        Grid.SetRow(highlightingCaption, 2);
        highlightingTab.Children.Add(highlightingDesc);
        highlightingTab.Children.Add(highlightingEditor);
        highlightingTab.Children.Add(highlightingCaption);

        // ---- Agents ----

        var agentColumns = new Grid { ColumnSpacing = 16 };
        agentColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        agentColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tabStatusCard = SectionCard(
            "Tab display",
            "Replace a session icon while Resesh recognizes a supported agent in that tab.",
            agentIcons);
        var alertCard = SectionCard(
            "Background alerts",
            "Get your attention when an agent waits for a response. Turn on agent icons to use alerts.",
            agentFlash,
            agentSound);
        tabStatusCard.VerticalAlignment = VerticalAlignment.Stretch;
        alertCard.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(tabStatusCard, 0);
        Grid.SetColumn(alertCard, 1);
        agentColumns.Children.Add(tabStatusCard);
        agentColumns.Children.Add(alertCard);

        var agentsTab = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Description("Track supported coding agents in each terminal tab. Resesh can notify you when an agent needs a response."),
                agentColumns,
                SectionCard(
                    "Agent adapters",
                    "Resesh identifies supported agents automatically. Add an adapter only for exact working, waiting, and finished states.",
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
        var host = new ScrollViewer
        {
            Width = DialogWidth,
            Height = TabContentHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var bar = new SelectorBar();
        var barItems = new[]
        {
            new SelectorBarItem { Text = "General" },
            new SelectorBarItem { Text = "Recording" },
            new SelectorBarItem { Text = "Highlighting" },
            new SelectorBarItem { Text = "Agents" },
        };
        foreach (var item in barItems)
            bar.Items.Add(item);

        void ShowTab(int index) => host.Content = tabPanels[index];

        bar.SelectionChanged += (s, _) =>
        {
            var index = Array.IndexOf(barItems, s.SelectedItem);
            if (index >= 0)
                ShowTab(index);
        };
        bar.SelectedItem = barItems[initialTab];
        ShowTab(initialTab);

        var content = new StackPanel
        {
            Spacing = 12,
            Children = { bar, host },
        };

        // DefaultButton stays None on purpose: Enter while typing in the highlighting rule
        // form must not save-and-close the whole dialog.
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = xamlRoot,
        };
        dialog.Opened += (_, _) =>
            initialFocus?.DispatcherQueue.TryEnqueue(() => initialFocus.Focus(FocusState.Programmatic));
        dialog.Resources["ContentDialogMaxWidth"] = DialogWidth + 48;
        dialog.Resources["ContentDialogMaxHeight"] = 960d;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            applyThemePreview(current.Theme);
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

    private static Border SectionCard(string title, string? description, params UIElement[] controls)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        if (description is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72,
            });
        }
        foreach (var control in controls)
            panel.Children.Add(control);

        return new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(48, 128, 128, 128)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 128, 128, 128)),
            Child = panel,
        };
    }
}
