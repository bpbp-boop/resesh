using Resesh.App.ViewModels;

namespace Resesh.App.Controls;

/// <summary>Widths of the two divider segments under the tab strip, which leave a gap under
/// the active tab. <see cref="RetryAfterLayout"/>: the active tab has no bounds yet.</summary>
internal readonly record struct TabDividerWidths(double Left, double Right, bool RetryAfterLayout);

/// <summary>Where the divider segments start and end while the gap slides with the active tab.</summary>
internal readonly record struct TabDividerSlide(double FromLeft, double ToLeft, double FromRight, double ToRight)
{
    /// <summary>Either segment moves far enough to be seen; otherwise set exact widths.</summary>
    public bool Moves => Math.Abs(FromLeft - ToLeft) >= 0.5 || Math.Abs(FromRight - ToRight) >= 0.5;
}

/// <summary>A divider segment drawn at <see cref="Width"/> and scaled from one fraction of it to another.</summary>
internal readonly record struct TabDividerScale(double Width, double From, double To);

/// <summary>Sizing decisions for a tab strip, kept free of WinUI so they can be tested headless.</summary>
internal static class TabStripSizing
{
    /// <summary>Equals TabViewItemMinWidth in TabGroupView.xaml.</summary>
    public const double MinimumTabWidth = 100;

    /// <summary>Equals TabViewItemMaxWidth in TabGroupView.xaml.</summary>
    public const double MaximumTabWidth = 220;

    /// <summary>What the action buttons are assumed to need before they have been measured.</summary>
    public const double ExpandedTabActionsFallbackWidth = 224;

    /// <summary>Space kept between the measured action buttons and the tabs.</summary>
    public const double ExpandedTabActionsPadding = 6;

    /// <summary>Any realized tab (width above zero) is narrower than the maximum. Full-width
    /// tabs skip the full refresh: its SizeToContent pass would flash content widths.</summary>
    public static bool HasShrunkTabs(IEnumerable<double> widths, double maxWidth = MaximumTabWidth) =>
        widths.Any(width => width > 0 && width < maxWidth - 0.5);

    /// <summary>The widest the action buttons have been measured, so the overflow decision
    /// does not flip back while they are collapsed.</summary>
    public static double ReserveActionsWidth(double current, bool visible, double actualWidth) =>
        visible && actualWidth > 0
            ? Math.Max(current, actualWidth + ExpandedTabActionsPadding)
            : current;

    /// <summary>The action buttons fold into an overflow menu once minimum-width tabs and the
    /// expanded buttons no longer fit, before the tabs would have to scroll.</summary>
    public static bool UseOverflow(
        double stripWidth, int tabCount, double actionsWidth, double minimumTabWidth = MinimumTabWidth) =>
        stripWidth > 0
        && tabCount > 0
        && stripWidth < tabCount * minimumTabWidth + actionsWidth;

    /// <summary>The divider segments either side of a tab spanning <paramref name="left"/>
    /// to <paramref name="left"/> + <paramref name="width"/>, clipped to the strip.</summary>
    public static (double Left, double Right) DividerAround(double stripWidth, double left, double width) =>
        (Math.Clamp(left, 0, stripWidth), stripWidth - Math.Clamp(left + width, 0, stripWidth));

    /// <summary>
    /// No selection draws no divider. While a drag preview is up the headers move apart
    /// from their slots, so the gap closes until they settle. A selected tab whose
    /// container has no bounds yet (just opened) hides the divider and asks for another
    /// try after layout, rather than drawing it under the new tab.
    /// </summary>
    public static TabDividerWidths DividerWidths(
        double stripWidth, bool hasSelection, bool dragPreviewVisible, (double Left, double Width)? activeTab)
    {
        if (!hasSelection)
            return new(0, 0, RetryAfterLayout: false);
        if (dragPreviewVisible)
            return new(stripWidth, 0, RetryAfterLayout: false);
        if (activeTab is { Width: > 0 } active)
        {
            var (left, right) = DividerAround(stripWidth, active.Left, active.Width);
            return new(left, right, RetryAfterLayout: false);
        }
        return new(0, 0, RetryAfterLayout: true);
    }

    /// <summary>The divider segments from where the active tab was drawn to its new slot.</summary>
    public static TabDividerSlide DividerSlide(
        double stripWidth, (double Left, double Width) origin, double layoutLeft, double width)
    {
        var (toLeft, toRight) = DividerAround(stripWidth, layoutLeft, width);
        var (fromLeft, fromRight) = DividerAround(stripWidth, origin.Left, origin.Width);
        return new(fromLeft, toLeft, fromRight, toRight);
    }

    /// <summary>A segment is drawn at the larger width and scaled, so it never has to grow
    /// past its laid-out size. Zero width: nothing to animate.</summary>
    public static TabDividerScale DividerScale(double from, double to)
    {
        var width = Math.Max(from, to);
        return width <= 0 ? new(width, 1, 1) : new(width, from / width, to / width);
    }
}

/// <summary>What the strip should do to its tab widths after a change.</summary>
internal enum TabWidthAction
{
    None,
    /// <summary>Keep the current widths until the pointer leaves the strip.</summary>
    Hold,
    /// <summary>Recalculate the equal width from scratch.</summary>
    RefreshFull,
    /// <summary>Snapshot tab positions for motion, then recalculate the equal width.</summary>
    CaptureAndRefreshFull,
}

/// <summary>
/// Browser-style width hold. A close under the pointer keeps the current widths so the next
/// tab's close button lands where the pointer already is; the tabs grow once the pointer
/// leaves the strip, or the strip leaves the screen.
/// </summary>
internal sealed class TabWidthHold
{
    public bool PointerInStrip { get; private set; }
    public bool HoldPending { get; private set; }

    public void PointerEntered() => PointerInStrip = true;

    /// <summary>A tab was removed at <paramref name="index"/>, leaving <paramref name="remaining"/>.
    /// Removing the last tab leaves no close button under the pointer to keep in place.</summary>
    public TabWidthAction TabRemoved(bool anyShrunk, int index, int remaining)
    {
        if (!anyShrunk)
            return TabWidthAction.None;
        if (PointerInStrip && index < remaining)
        {
            HoldPending = true;
            return TabWidthAction.Hold;
        }
        return TabWidthAction.RefreshFull;
    }

    /// <summary>The tabs slide to their grown widths.</summary>
    public TabWidthAction PointerExited()
    {
        PointerInStrip = false;
        return TakeHold() ? TabWidthAction.CaptureAndRefreshFull : TabWidthAction.None;
    }

    /// <summary>The strip left the screen: a held width would stay stuck until it returns.</summary>
    public TabWidthAction Release()
    {
        PointerInStrip = false;
        return TakeHold() ? TabWidthAction.RefreshFull : TabWidthAction.None;
    }

    private bool TakeHold()
    {
        if (!HoldPending)
            return false;
        HoldPending = false;
        return true;
    }
}

internal enum TabMotionKind
{
    /// <summary>Slide from <see cref="TabMotionStep.Offset"/> back to the tab's slot.</summary>
    Slide,
    /// <summary>Drawn where it was (or close enough): clear any translation.</summary>
    Snap,
    /// <summary>New since the snapshot: fade in at its slot.</summary>
    FadeIn,
}

internal readonly record struct TabMotionStep(TabMotionKind Kind, float Offset);

/// <summary>Whether tab motion or the strip's own layout sets the divider widths.</summary>
internal enum TabDividerOwner
{
    Layout,
    Motion,
    /// <summary>A divider slide no longer matches the strip: cancel it, then lay out.</summary>
    CancelSlide,
}

/// <summary>The numbers behind tab strip motion; the view drives Composition with them.</summary>
internal static class TabStripMotion
{
    /// <summary>How a tab moves once layout settles: <paramref name="drawnOffset"/> is where it
    /// was drawn before the change less its new layout position, or null if it was not there.</summary>
    public static TabMotionStep Plan(double? drawnOffset)
    {
        if (drawnOffset is not { } drawn)
            return new(TabMotionKind.FadeIn, 0);
        var offset = (float)drawn;
        return Math.Abs(offset) >= 0.5f
            ? new(TabMotionKind.Slide, offset)
            : new(TabMotionKind.Snap, offset);
    }

    /// <summary>What remains of a slide that started at <paramref name="from"/>, at
    /// <paramref name="progress"/> (0 to 1) through it. Matches the power-3 ease-out the
    /// Composition animation uses.</summary>
    public static float EasedOffset(float from, double progress)
    {
        if (progress >= 1)
            return 0;
        var remaining = 1 - progress;
        return (float)(from * remaining * remaining * remaining);
    }

    /// <summary>
    /// Tab motion holds the old gap until the tabs start moving, so it does not jump ahead
    /// of them, then keeps it while the gap slides under the tab that is still selected. A
    /// new selection or a drag preview hands the divider back.
    /// </summary>
    public static TabDividerOwner DividerOwner(
        bool motionPending, bool dividerSliding, bool slidingTabSelected, bool dragPreviewVisible)
    {
        if (motionPending)
            return TabDividerOwner.Motion;
        if (!dividerSliding)
            return TabDividerOwner.Layout;
        return slidingTabSelected && !dragPreviewVisible
            ? TabDividerOwner.Motion
            : TabDividerOwner.CancelSlide;
    }
}

/// <summary>Labels and states of the tab-bar action buttons.</summary>
internal static class TabStripActionState
{
    /// <summary>A group that is not focused dims its buttons, like its tabs.</summary>
    public static double Opacity(bool? isGroupFocused) => isGroupFocused == false ? 0.55 : 1.0;

    /// <summary>The terminal folder is only known on a live, unlocked connection with files.</summary>
    public static bool CurrentFolderEnabled(TabViewModel? tab) =>
        tab?.Capabilities.FilePane == true && tab.AcceptsCommands;

    public static string FilePaneLabel(bool isOpen) => isOpen ? "Hide File Pane" : "Show File Pane";
    public static string CommandsLabel(bool isOpen) => isOpen ? "Hide commands" : "Show commands";
    public static string RecordingLabel(bool recording) => recording ? "Stop recording" : "Start recording";
    public static string RewindLabel(bool rewinding) => rewinding ? "Return to live terminal" : "Instant rewind";
}
