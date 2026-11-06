using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Resesh.App.Icons;
using Resesh.App.ViewModels;
using Resesh.Core.Agents;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App;

/// <summary>
/// Converts view-model data into WinUI presentation values. Keeping these conversions in
/// the view layer prevents view models from owning brushes, images, visibility, or theme
/// resources. Brushes that change live are stable application resources or XAML-owned
/// brushes whose Color binding updates in place.
/// </summary>
public static class PresentationValues
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => Visible(!value);

    // Tab progress strip. WinUI's ProgressBar supplies the theme colours: accent, then
    // critical red for ShowError and caution amber for ShowPaused.
    public static Visibility ProgressVisibility(TerminalProgress progress) => Visible(progress.IsActive);

    public static double ProgressValue(TerminalProgress progress) => progress.Value;

    public static bool ProgressIndeterminate(TerminalProgress progress) =>
        progress.State == TerminalProgressState.Indeterminate;

    public static bool ProgressShowError(TerminalProgress progress) => progress.State == TerminalProgressState.Error;

    public static bool ProgressShowPaused(TerminalProgress progress) => progress.State == TerminalProgressState.Paused;

    public static Visibility TreeFolderVisibility(bool isFolder, bool isLocalRoot) =>
        Visible(isFolder && !isLocalRoot);

    public static Visibility TreeSessionVisibility(bool isFolder) => Visible(!isFolder);

    public static Visibility TreeHostVisibility(bool isSelected, bool isPointerOver) =>
        Visible(isSelected || isPointerOver);

    public static Visibility FocusedAccentVisibility(bool isActive, bool isGroupFocused) =>
        Visible(isActive && isGroupFocused);

    public static Visibility InactiveUnderlineVisibility(bool isActive, bool isGroupFocused) =>
        Visible(isActive && !isGroupFocused);

    public static Brush? TreeSelectionBackground(bool isSelected) =>
        isSelected ? ResourceBrush("SessionTreeSelectionBrush") : null;

    public static string TreeHighlightQuery(string query, bool isSelected) => isSelected ? "" : query;

    public static Brush TreeForeground(bool isSelected) =>
        ResourceBrush(isSelected ? "SessionTreeSelectionForegroundBrush" : "SessionTreeForegroundBrush");

    public static Brush TreeMutedForeground(bool isSelected) =>
        ResourceBrush(isSelected ? "SessionTreeSelectionForegroundBrush" : "SessionTreeMutedForegroundBrush");

    public static Windows.UI.Color ColorTag(string? value) => ParseColor(value);

    public static Uri? TreeIconUri(string? key) => App.Icons.GetTreeUri(key);

    /// <summary>A session's icon in a list: a built-in mark drawn one-colour in the pane's
    /// colour, or a custom icon file as it is.</summary>
    public static ImageSource? SessionIcon(string? key) =>
        key is not null && SessionIcons.IsBuiltIn(key)
            ? App.Brands.Get(key, SessionIconCatalog.ListIconSize, ((SolidColorBrush)ResourceBrush("SessionTreeForegroundBrush")).Color)
            : App.Icons.GetImage(key, SessionIconCatalog.ListIconSize);

    public static ImageSource? AgentIcon(string? key) =>
        App.Icons.GetAgentImage(key, SessionIconCatalog.ListIconSize);

    public static Visibility TreeIconVisibility(string? key) => Visible(TreeIconUri(key) is not null);

    public static Visibility DefaultTreeIconVisibility(string? key) => Visible(TreeIconUri(key) is null);

    public static Visibility SessionIconVisibility(string? key) => Visible(SessionIcon(key) is not null);

    public static Visibility DefaultSessionIconVisibility(string? key) => Visible(SessionIcon(key) is null);

    public static Visibility AgentIconVisibility(string? key) => Visible(AgentIcon(key) is not null);

    /// <summary>The tab's status slot: an agent icon, or a program's badge on its own. It
    /// stands in for the connection dot, so it shows only while the tab is connected; in any
    /// other state the connection is the news and the dot takes the slot back.</summary>
    public static Visibility AgentSlotVisibility(string? key, bool programStatus, TabConnectionState state) =>
        Visible(ShowsAgentSlot(key, programStatus, state));

    public static Visibility StateDotVisibility(string? key, bool programStatus, TabConnectionState state) =>
        Visible(!ShowsAgentSlot(key, programStatus, state));

    /// <summary>The dots' container, collapsed with its margin when it holds neither dot.</summary>
    public static Visibility StateDotsVisibility(
        string? key, bool programStatus, TabConnectionState state, bool isRecording) =>
        Visible(isRecording || !ShowsAgentSlot(key, programStatus, state));

    private static bool ShowsAgentSlot(string? key, bool programStatus, TabConnectionState state) =>
        state == TabConnectionState.Connected && (programStatus || AgentIcon(key) is not null);

    public static Visibility AgentBadgeVisibility(string? key, AgentAttention attention, bool programStatus) =>
        Visible(programStatus
            || (AgentIcon(key) is not null && attention is not (AgentAttention.None or AgentAttention.Idle)));

    // The badge hangs off the corner of an agent icon and sits centered when it is alone.
    public static HorizontalAlignment AgentBadgeHorizontalAlignment(bool programStatus) =>
        programStatus ? HorizontalAlignment.Center : HorizontalAlignment.Right;

    public static VerticalAlignment AgentBadgeVerticalAlignment(bool programStatus) =>
        programStatus ? VerticalAlignment.Center : VerticalAlignment.Bottom;

    /// <summary>How far the badge hangs past the icon's corner: a symbol badge is centred
    /// near the corner so it hides as little of the icon as it can.</summary>
    public static Thickness AgentBadgeMargin(bool programStatus, double size) =>
        programStatus ? new Thickness(0) : size > 8 ? new Thickness(0, 0, -6, -6) : new Thickness(0, 0, -2, -2);

    /// <summary>Only a visible working dot pulses, so a hidden one costs no frames.</summary>
    public static bool AgentBadgePulses(string? key, AgentAttention attention) =>
        attention == AgentAttention.Working && AgentIcon(key) is not null;

    public static Windows.UI.Color TabHeaderBackgroundColor(
        string appTheme,
        bool isActive,
        bool isPointerOver)
    {
        var palette = ThemeVisualPalette.For(appTheme);
        return isActive ? palette.ActiveTab : isPointerOver ? palette.HoverTab : palette.InactiveTab;
    }

    public static Windows.UI.Color TabHeaderBorderColor(string appTheme) =>
        ThemeVisualPalette.For(appTheme).Divider;

    public static Windows.UI.Color TabHeaderForegroundColor(
        string appTheme,
        bool isActive,
        bool isGroupFocused,
        bool isPointerOver)
    {
        var palette = ThemeVisualPalette.For(appTheme);
        if (palette.IsHighContrast)
            return !isActive && isPointerOver ? palette.TreeSelectionForeground : palette.TreeForeground;

        var isDark = !ThemeCatalog.IsLight(appTheme);
        return isActive
            ? isGroupFocused
                ? isDark ? Rgb(0xFFFFFF) : Rgb(0x333333)
                : isDark ? Rgb(0xC0C0C0) : Rgb(0x555555)
            : isGroupFocused
                ? isDark ? Rgb(0x9D9D9D) : Rgb(0x616161)
                : isDark ? Rgb(0x727272) : Rgb(0x8A8A8A);
    }

    public static Windows.UI.Text.FontWeight TabHeaderFontWeight(bool hasUnseenOutput) =>
        hasUnseenOutput ? FontWeights.SemiBold : FontWeights.Normal;

    public static Windows.UI.Color StateColor(TabConnectionState state, bool hasUnseenOutput) =>
        StatusColor(state == TabConnectionState.Connected && hasUnseenOutput
            ? Status.Info
            : state switch
            {
                TabConnectionState.Connected => Status.Success,
                TabConnectionState.Connecting => Status.Caution,
                TabConnectionState.Exited => Status.Neutral,
                TabConnectionState.Playback or TabConnectionState.Page => Status.Info,
                _ => Status.Critical,
            });

    /// <summary>The badge's disc, in the app's status colours: blue while working, amber
    /// while it waits for you, green when done, red when failed, grey for a bell.</summary>
    public static Windows.UI.Color AgentBadgeColor(AgentAttention attention) => StatusColor(attention switch
    {
        AgentAttention.Working => Status.Info,
        AgentAttention.NeedsApproval or AgentAttention.NeedsAnswer => Status.Caution,
        AgentAttention.Complete => Status.Success,
        AgentAttention.Failed => Status.Critical,
        _ => Status.Neutral,
    });

    public static Windows.UI.Color FocusedTabBorderColor(string appTheme, string? colorTag)
    {
        var palette = ThemeVisualPalette.For(appTheme);
        if (palette.IsHighContrast)
            return palette.TreeSelection;

        var parsed = ParseColor(colorTag);
        return parsed.A > 0 ? parsed : palette.Accent;
    }

    /// <summary>Height of the active tab's accent bar. Themes whose accent is the only
    /// saturated color on screen ask for a thicker bar so it reads as a marker.</summary>
    public static double FocusedTabAccentThickness(string appTheme) =>
        ThemeVisualPalette.For(appTheme).AccentBarThickness;

    // ---- workspace rail buttons ----

    /// <summary>A workspace's brand mark for the rail: drawn sharp at 16 px in the rail's
    /// foreground colour; null for "".</summary>
    public static ImageSource? WorkspaceBrandImage(string brandKey) =>
        App.Brands.Get(brandKey, 16, ((SolidColorBrush)ResourceBrush("SessionTreeForegroundBrush")).Color);

    /// <summary>A workspace icon drawn as a path; null for "" so nothing is drawn.</summary>
    public static Geometry? IconPathGeometry(string path) =>
        path.Length == 0
            ? null
            : (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), path);

    // NavigationViewSelectionIndicatorHeight: the marker is the native selection pill's shape.
    private const double RailMarkerFullHeight = 16;

    /// <summary>The workspace marker is drawn at full height and scaled, so a change of state
    /// animates through its ScaleTransition instead of snapping.</summary>
    public static System.Numerics.Vector3 RailMarkerScale(double height) =>
        new(1, (float)(height / RailMarkerFullHeight), 1);

    /// <summary>Matches the workspace slide; zero when Windows animation effects are off.</summary>
    public static TimeSpan RailMarkerDuration =>
        AppMotion.Enabled ? TimeSpan.FromMilliseconds(170) : TimeSpan.Zero;

    /// <summary>The rail's alert badge speaks the tab strip's language: the blocked badge
    /// when an agent waits, the failed one for a dropped session, the working dot's blue
    /// for unseen output.</summary>
    public static AgentAttention WorkspaceAlertBadge(WorkspaceAlert alert) => alert switch
    {
        WorkspaceAlert.Attention => AgentAttention.NeedsApproval,
        WorkspaceAlert.Disconnected => AgentAttention.Failed,
        _ => AgentAttention.Working,
    };

    public static Windows.UI.Color WorkspaceAlertColor(WorkspaceAlert alert) => AgentBadgeColor(WorkspaceAlertBadge(alert));

    public static double WorkspaceAlertSize(WorkspaceAlert alert) => alert == WorkspaceAlert.Activity ? 6 : 12;

    // Only for the Session* brushes ThemeVisualPalette recolours in place: one shared instance.
    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.Resources[key];

    /// <summary>The meanings a status mark can have. Each is one brush in App.xaml's theme
    /// dictionaries, with a High Contrast variant in system colors.</summary>
    private enum Status { Info, Success, Caution, Critical, Neutral }

    // Read on each call rather than cached, so a change to High Contrast is picked up.
    // The status brushes are the same in Light and Dark; only High Contrast differs.
    private static Windows.UI.Color StatusColor(Status status) =>
        ((SolidColorBrush)ThemeResources.ContrastBrush(status switch
        {
            Status.Info => "SessionStatusInfoBrush",
            Status.Success => "SessionStatusSuccessBrush",
            Status.Caution => "SessionStatusCautionBrush",
            Status.Critical => "SessionStatusCriticalBrush",
            _ => "SessionStatusNeutralBrush",
        })!).Color;

    /// <summary>A stored <c>#rrggbb</c>, read as the terminal reads highlight colours;
    /// transparent when it is not one.</summary>
    private static Windows.UI.Color ParseColor(string? value) =>
        Resesh.Terminal.HexColor.Parse(value) is { } rgb ? Rgb(rgb) : Windows.UI.Color.FromArgb(0, 0, 0, 0);

    private static Windows.UI.Color Rgb(uint rgb) => Windows.UI.Color.FromArgb(
        255,
        (byte)(rgb >> 16),
        (byte)(rgb >> 8),
        (byte)rgb);
}
