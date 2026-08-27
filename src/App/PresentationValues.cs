using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Resesh.App.Icons;
using Resesh.App.ViewModels;
using Resesh.Core.Agents;
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

    public static Visibility FocusedAccentVisibility(bool isActive, bool isGroupFocused) =>
        Visible(isActive && isGroupFocused);

    public static Visibility InactiveUnderlineVisibility(bool isActive, bool isGroupFocused) =>
        Visible(isActive && !isGroupFocused);

    public static Brush? TreeSelectionBackground(bool isSelected) =>
        isSelected ? ResourceBrush("SessionTreeSelectionBrush") : null;

    public static Brush TreeForeground(bool isSelected) =>
        ResourceBrush(isSelected ? "SessionTreeSelectionForegroundBrush" : "SessionTreeForegroundBrush");

    public static Brush TreeMutedForeground(bool isSelected) =>
        ResourceBrush(isSelected ? "SessionTreeSelectionForegroundBrush" : "SessionTreeMutedForegroundBrush");

    public static Windows.UI.Color ColorTag(string? value) => ParseColor(value);

    public static Uri? TreeIconUri(string? key) => App.Icons.GetTreeUri(key);

    public static ImageSource? SessionIcon(string? key) =>
        App.Icons.GetImage(key, SessionIconCatalog.ListIconSize);

    public static ImageSource? AgentIcon(string? key) =>
        App.Icons.GetAgentImage(key, SessionIconCatalog.ListIconSize);

    public static Visibility TreeIconVisibility(string? key) => Visible(TreeIconUri(key) is not null);

    public static Visibility DefaultTreeIconVisibility(string? key) => Visible(TreeIconUri(key) is null);

    public static Visibility SessionIconVisibility(string? key) => Visible(SessionIcon(key) is not null);

    public static Visibility DefaultSessionIconVisibility(string? key) => Visible(SessionIcon(key) is null);

    public static Visibility AgentIconVisibility(string? key) => Visible(AgentIcon(key) is not null);

    public static Visibility AgentBadgeVisibility(string? key, AgentAttention attention) =>
        Visible(AgentIcon(key) is not null && attention is not (AgentAttention.None or AgentAttention.Idle));

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
        state == TabConnectionState.Connected && hasUnseenOutput
            ? Rgb(0x0078D4)
            : state switch
            {
                TabConnectionState.Connected => Rgb(0x16C60C),
                TabConnectionState.Connecting => Rgb(0xFFB900),
                TabConnectionState.Exited => Rgb(0x8A8A8A),
                TabConnectionState.Playback => Rgb(0x0078D4),
                _ => Rgb(0xE74856),
            };

    public static Windows.UI.Color AgentBadgeColor(AgentAttention attention) => attention switch
    {
        AgentAttention.Working => Rgb(0x0078D4),
        AgentAttention.NeedsApproval or AgentAttention.NeedsAnswer => Rgb(0xFFB900),
        AgentAttention.Complete => Rgb(0x16C60C),
        AgentAttention.Failed => Rgb(0xE74856),
        _ => Rgb(0x9E9E9E),
    };

    public static Windows.UI.Color FocusedTabBorderColor(string appTheme, string? colorTag)
    {
        var palette = ThemeVisualPalette.For(appTheme);
        if (palette.IsHighContrast)
            return palette.TreeSelection;

        var parsed = ParseColor(colorTag);
        return parsed.A > 0 ? parsed : Rgb(0x0078D4);
    }

    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.Resources[key];

    private static Windows.UI.Color ParseColor(string? value)
    {
        if (value is { Length: 7 } && value[0] == '#'
            && byte.TryParse(value.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var red)
            && byte.TryParse(value.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var green)
            && byte.TryParse(value.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
        {
            return Windows.UI.Color.FromArgb(255, red, green, blue);
        }

        return Windows.UI.Color.FromArgb(0, 0, 0, 0);
    }

    private static Windows.UI.Color Rgb(uint rgb) => Windows.UI.Color.FromArgb(
        255,
        (byte)(rgb >> 16),
        (byte)(rgb >> 8),
        (byte)rgb);
}
