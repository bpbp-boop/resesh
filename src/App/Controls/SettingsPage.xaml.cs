using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.Dialogs;
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
    private enum Section { General, Recording, Highlighting, Agents, Shortcuts }

    private readonly HighlightsStore _highlightDraft = App.Highlights.CreateDraft();

    public SettingsViewModel ViewModel { get; }

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        HistoryDescription.Text =
            "Save each finished command with its output, folder, and result on this computer, and search it with "
            + $"{AppShortcuts.Label(ShortcutIds.CommandHistory)}. Output can include secrets that a server prints. "
            + "A saved session can turn history off in its options.";
        HighlightEditorHost.Content = HighlightEditorPanel.Create(
            _highlightDraft,
            CommitHighlights,
            (label, field) => SettingsLayout.SettingRow(label, field, stacked: false),
            _ => HighlightingSection.ChangeView(null, 0, null, disableAnimation: true));
        AgentAdaptersHost.Content = AgentAdapterPanel.Create();
        Show(Section.General);
    }

    /// <summary>Shows the section holding <paramref name="target"/>, scrolls to it, and focuses it.</summary>
    public void Navigate(GlobalSettingsTarget target)
    {
        Show(SectionOf(target));
        if (FieldOf(target) is not var (card, control))
            return;
        // The section was collapsed until now; place and focus after its first layout pass.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            card.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3, AnimationDesired = false });
            control.Focus(FocusState.Programmatic);
        });
    }

    private static Section SectionOf(GlobalSettingsTarget target) => target switch
    {
        GlobalSettingsTarget.Recording or GlobalSettingsTarget.RecordingDirectory
            or GlobalSettingsTarget.AlwaysRecord or GlobalSettingsTarget.RewindMinutes
            or GlobalSettingsTarget.RewindMegabytes or GlobalSettingsTarget.CommandHistory
            or GlobalSettingsTarget.CommandHistoryDays => Section.Recording,
        GlobalSettingsTarget.Highlighting => Section.Highlighting,
        GlobalSettingsTarget.Agents or GlobalSettingsTarget.ShowAgentIcons
            or GlobalSettingsTarget.AgentAlertFlash or GlobalSettingsTarget.AgentAlertSound => Section.Agents,
        _ => Section.General,
    };

    private (FrameworkElement Card, Control Control)? FieldOf(GlobalSettingsTarget target) => target switch
    {
        GlobalSettingsTarget.Theme => (ThemeCard, ThemeBox),
        GlobalSettingsTarget.FontFamily => (FontFamilyCard, FontFamilyBox),
        GlobalSettingsTarget.FontSize => (FontSizeCard, FontSizeBox),
        GlobalSettingsTarget.Scrollback => (ScrollbackCard, ScrollbackBox),
        GlobalSettingsTarget.CopyOnSelect => (CopyOnSelectCard, CopyOnSelectSwitch),
        GlobalSettingsTarget.RightClickPaste => (RightClickPasteCard, RightClickPasteSwitch),
        GlobalSettingsTarget.ShowStatusBar => (ShowStatusBarCard, ShowStatusBarSwitch),
        GlobalSettingsTarget.ReopenLastLayout => (ReopenLastLayoutCard, ReopenLastLayoutSwitch),
        GlobalSettingsTarget.RecordingDirectory => (RecordingDirectoryCard, RecordingDirectoryBox),
        GlobalSettingsTarget.AlwaysRecord => (AlwaysRecordCard, AlwaysRecordSwitch),
        GlobalSettingsTarget.RewindMinutes => (RewindMinutesCard, RewindMinutesBox),
        GlobalSettingsTarget.RewindMegabytes => (RewindMegabytesCard, RewindMegabytesBox),
        GlobalSettingsTarget.CommandHistory => (KeepHistoryCard, KeepHistorySwitch),
        GlobalSettingsTarget.CommandHistoryDays => (HistoryDaysCard, HistoryDaysBox),
        GlobalSettingsTarget.ShowAgentIcons => (AgentIconsCard, AgentIconsSwitch),
        GlobalSettingsTarget.AgentAlertFlash => (AgentFlashCard, AgentFlashSwitch),
        GlobalSettingsTarget.AgentAlertSound => (AgentSoundCard, AgentSoundSwitch),
        _ => null,
    };

    private void Show(Section section)
    {
        GeneralSection.Visibility = Visible(section == Section.General);
        RecordingSection.Visibility = Visible(section == Section.Recording);
        HighlightingSection.Visibility = Visible(section == Section.Highlighting);
        AgentsSection.Visibility = Visible(section == Section.Agents);
        ShortcutsSection.Visibility = Visible(section == Section.Shortcuts);
        var item = section switch
        {
            Section.Recording => RecordingItem,
            Section.Highlighting => HighlightingItem,
            Section.Agents => AgentsItem,
            Section.Shortcuts => ShortcutsItem,
            _ => GeneralItem,
        };
        if (!ReferenceEquals(Sections.SelectedItem, item))
            Sections.SelectedItem = item;
        if (section == Section.Recording)
            ViewModel.RefreshHistoryUsage();
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void Sections_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag && Enum.TryParse<Section>(tag, out var section))
            Show(section);
    }

    private void CommitHighlights()
    {
        try
        {
            App.Highlights.CommitDraft(_highlightDraft);
            HighlightSaveError.IsOpen = false;
            App.RefreshHighlightsInAllWindows();
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
