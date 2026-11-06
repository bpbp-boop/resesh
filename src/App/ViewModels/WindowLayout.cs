using Resesh.Core.Storage;

namespace Resesh.App.ViewModels;

/// <summary>A window rectangle in physical pixels, as the window manager reports it.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// The rules behind a window's saved layout: where it reopens, when the sessions pane floats
/// over the tabs, and how wide the pane may be. The window measures and applies; the
/// decisions live here so they are tested without one.
/// </summary>
public static class WindowLayout
{
    /// <summary>A window smaller than this is not worth saving or restoring: it is most
    /// likely a mid-minimize or broken placement, not a size anyone chose.</summary>
    public const int MinimumSavedWidth = 320;
    public const int MinimumSavedHeight = 240;

    /// <summary>The sessions pane is never narrower than this, docked or saved.</summary>
    public const double MinimumPaneWidth = 180;
    public const double MaximumPaneWidth = 800;
    public const double DefaultPaneWidth = 280;

    /// <summary>A floating pane stays within these, leaving the tabs in view beside it.</summary>
    public const double MinimumFloatingWidth = 200;
    public const double MaximumFloatingWidth = 360;
    public const double FloatingTabsMargin = 96;

    /// <summary>Docking the pane always leaves the tabs this much room.</summary>
    public const double MinimumTabAreaWidth = 320;

    /// <summary>With the Automatic layout, the pane floats below this window width and docks
    /// again only above the wider bound, so resizing near the edge doesn't flicker.</summary>
    public const double FloatBelowWidth = 1000;
    public const double DockAboveWidth = 1060;

    /// <summary>The rectangle a saved placement asks for, or null when it is missing or too small.</summary>
    public static PixelRect? Requested(WindowPlacement? placement) =>
        placement is { Width: >= MinimumSavedWidth, Height: >= MinimumSavedHeight }
            ? new PixelRect(placement.X, placement.Y, placement.Width, placement.Height)
            : null;

    /// <summary>The requested rectangle moved and shrunk to lie wholly inside the work area
    /// of the display it is on, so a window saved on a monitor since unplugged or resized
    /// still opens in reach.</summary>
    public static PixelRect FitInto(PixelRect requested, PixelRect workArea)
    {
        var width = Math.Min(requested.Width, workArea.Width);
        var height = Math.Min(requested.Height, workArea.Height);
        var x = Math.Clamp(requested.X, workArea.X, workArea.X + workArea.Width - width);
        var y = Math.Clamp(requested.Y, workArea.Y, workArea.Y + workArea.Height - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>Where a window opens for a tab dropped outside every window: placed so the
    /// pointer holds the tab where it held it in the strip it left, (<paramref name="grabX"/>,
    /// <paramref name="grabY"/>) from the window's corner, then kept on the work area of the
    /// display it was dropped on.</summary>
    public static PixelRect TornOff(int pointerX, int pointerY, int grabX, int grabY, int width, int height, PixelRect workArea) =>
        FitInto(new PixelRect(pointerX - grabX, pointerY - grabY, width, height), workArea);

    /// <summary>What to save for a window last restored to <paramref name="normalBounds"/>,
    /// or null when that is too small to be worth reopening at.</summary>
    public static WindowPlacement? ToSave(PixelRect normalBounds, bool maximized) =>
        normalBounds is { Width: >= MinimumSavedWidth, Height: >= MinimumSavedHeight }
            ? new WindowPlacement(normalBounds.X, normalBounds.Y, normalBounds.Width, normalBounds.Height, maximized)
            : null;

    /// <summary>A saved rail tab this version knows, or "sessions".</summary>
    public static string NormalizeRailTab(string? tab) => tab switch
    {
        "workspaces" => "workspaces",
        "recent" => "recent",
        "recordings" => "recordings",
        _ => "sessions",
    };

    /// <summary>The saved sessions pane width, kept to what the pane can be.</summary>
    public static double PaneWidth(double? saved) =>
        Math.Clamp(saved ?? DefaultPaneWidth, MinimumPaneWidth, MaximumPaneWidth);

    /// <summary>Whether the sessions pane floats over the tabs for this layout setting and
    /// window width. <paramref name="floatingNow"/> gives Automatic its hysteresis.</summary>
    public static bool Floats(string? layout, double windowWidth, bool floatingNow) =>
        SessionsPaneLayouts.Normalize(layout) switch
        {
            SessionsPaneLayouts.Float => true,
            SessionsPaneLayouts.Dock => false,
            _ => windowWidth < (floatingNow ? DockAboveWidth : FloatBelowWidth),
        };

    /// <summary>The saved width, capped so the floating pane leaves the tabs in view.</summary>
    public static double FloatingWidth(double paneWidth, double windowWidth, double railWidth) =>
        Math.Max(MinimumFloatingWidth, Math.Min(Math.Min(paneWidth, MaximumFloatingWidth),
            windowWidth - railWidth - FloatingTabsMargin));

    /// <summary>How wide the docked pane may be: whatever leaves the tabs their minimum.</summary>
    public static double DockedMaxWidth(double windowWidth, double fixedColumnsWidth) =>
        Math.Max(MinimumPaneWidth, windowWidth - fixedColumnsWidth - MinimumTabAreaWidth);

    /// <summary>Whether the docked pane's width is one the user chose, rather than one a
    /// narrow window is squeezing it to; only a chosen width is worth remembering.</summary>
    public static bool IsChosenWidth(double actualWidth, double maxWidth) =>
        actualWidth >= MinimumPaneWidth && actualWidth < maxWidth - 0.5;
}
