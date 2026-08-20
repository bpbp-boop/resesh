using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sessions.Core.Storage;

namespace Sessions.App.Dialogs;

public enum GlobalSettingsTab
{
    General,
    Highlighting,
    Agents,
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
        Action<AppSettings> applyPreview,
        GlobalSettingsTab initialTab = GlobalSettingsTab.General)
    {
        var theme = new ComboBox
        {
            Header = "Theme",
            ItemsSource = ThemeCatalog.All,
            SelectedItem = ThemeCatalog.Find(current.Theme),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AppSettings PreviewSettings() => current with
        {
            Theme = (theme.SelectedItem as ThemeChoice)?.Id ?? current.Theme,
        };
        theme.SelectionChanged += (_, _) => applyPreview(PreviewSettings());
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

        var agentIcons = new ToggleSwitch
        {
            Header = WrappingHeader("Show agent icons on tabs"),
            IsOn = current.ShowAgentIcons,
        };
        var agentFlash = new ToggleSwitch
        {
            Header = WrappingHeader("Flash the taskbar when a background agent needs you"),
            IsOn = current.AgentAlertFlash,
        };
        var agentSound = new ToggleSwitch
        {
            Header = WrappingHeader("Play the notification sound"),
            IsOn = current.AgentAlertSound,
        };

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
                Description("These settings apply throughout Sessions. A saved session can override supported terminal and highlighting defaults."),
                generalColumns,
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
        var highlightingEditor = HighlightEditorPanel.Create(() => applyPreview(PreviewSettings()));
        var highlightingCaption = Caption("Highlighting changes apply immediately and push to open terminals. Save below applies to the other tabs.");
        Grid.SetRow(highlightingDesc, 0);
        Grid.SetRow((FrameworkElement)highlightingEditor, 1);
        Grid.SetRow(highlightingCaption, 2);
        highlightingTab.Children.Add(highlightingDesc);
        highlightingTab.Children.Add(highlightingEditor);
        highlightingTab.Children.Add(highlightingCaption);

        // ---- Agents ----

        var agentToggles = new Grid { ColumnSpacing = 16 };
        for (var i = 0; i < 3; i++)
            agentToggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (toggle, column) in new[] { (agentIcons, 0), (agentFlash, 1), (agentSound, 2) })
        {
            toggle.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(toggle, column);
            agentToggles.Children.Add(toggle);
        }

        var agentsTab = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Description("Show agent identity and attention in tabs. Adapters add reported agent status."),
                agentToggles,
                new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128)),
                    Margin = new Thickness(0, 4, 0, 4),
                },
                AgentAdapterPanel.Create(),
            },
        };

        // ---- tab host: fixed height so the dialog doesn't resize between tabs. Content is
        // swapped (not visibility-toggled) so each tab gets a fresh measure — a TextBox
        // measured while collapsed keeps a stale one-line text layout when merely unhidden. ----

        var tabPanels = new UIElement[] { generalTab, highlightingTab, agentsTab };
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
        bar.SelectedItem = barItems[(int)initialTab];
        ShowTab((int)initialTab);

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
        dialog.Resources["ContentDialogMaxWidth"] = DialogWidth + 48;
        dialog.Resources["ContentDialogMaxHeight"] = 960d;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            applyPreview(current);
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
