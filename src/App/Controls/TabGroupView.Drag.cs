using Microsoft.UI.Xaml.Media;
using Resesh.App.ViewModels;

namespace Resesh.App.Controls;

// The in-process handoff for a tab dragged out of a strip, to its own strip, another
// group's, or a pane's content in any window, or to the desktop outside them.
public sealed partial class TabGroupView
{
    private enum TabDragPhase
    {
        /// <summary>The pointer is carrying the tab.</summary>
        Dragging,

        /// <summary>The source has seen TabDragCompleted; a target's drop may still be
        /// queued behind it, so the session lives to the end of the dispatcher turn.</summary>
        Completing,
    }

    /// <summary>One tab drag, from TabDragStarting until a drop lands, the drag is
    /// cancelled, or its source leaves the tree. Everything every view needs to know about
    /// the drag lives here, so ending it is one assignment rather than four statics each
    /// cleared on its own path.</summary>
    private sealed class TabDragSession(TabViewModel tab, TabGroupView source)
    {
        public TabViewModel Tab { get; } = tab;
        public TabGroupView Source { get; } = source;
        public TabDragPhase Phase { get; set; } = TabDragPhase.Dragging;

        /// <summary>The other group whose strip shows the insertion marker.</summary>
        public TabGroupView? StripTarget { get; set; }

        /// <summary>The group whose strip is scrolling under the pointer.</summary>
        public TabGroupView? ScrollTarget { get; set; }

        /// <summary>Where the pointer holds the tab, measured at the start: the strip's
        /// previews are gone by the time a drop outside every window is acted on.</summary>
        public Windows.Foundation.Point TearOffGrab { get; init; }

        /// <summary>Catches the tab outside every window; gone when the drag ends.</summary>
        public Interop.TearOffDropTarget? DesktopTarget { get; set; }

        /// <summary>A source parked with its workspace or removed with its pane is out of
        /// the tree. Its drag can't be completed, and its tab may already be gone.</summary>
        public bool IsSourceGone => VisualTreeHelper.GetParent(Source) is null;
    }

    private static TabDragSession? _drag;

    /// <summary>The drag in progress, ignoring one whose source has left the tree before its
    /// Unloaded handler could end it.</summary>
    private static TabDragSession? CurrentDrag => _drag is { IsSourceGone: false } drag ? drag : null;

    /// <summary>Ends the drag: its scroll and marker stop, its source's previews go, and
    /// every window hides its content drop targets.</summary>
    private void EndTabDrag()
    {
        var drag = _drag;
        drag?.ScrollTarget?.StopDragScroll();
        HideTabStripDropIndicator();
        // Cleared before the source resets, so nothing it triggers sees a half-ended drag.
        _drag = null;
        drag?.DesktopTarget?.Dispose();
        drag?.Source.ResetTabDragPreview();
        _host.SetTabContentDropTargetsVisible(false);
    }

    /// <summary>The view left the tree. A drag from it gets no TabDragCompleted, so would
    /// keep every window's drop targets up; held tab widths and pending slides would wait
    /// for a pointer exit or a layout pass that never come. A layout rebuild removes and
    /// re-adds the view in one turn, and Unloaded can arrive after the re-add, so this
    /// acts only if the view is still out of the tree a turn later.</summary>
    private void TabGroupView_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        StopDragScroll();
        DispatcherQueue.TryPost(() =>
        {
            if (VisualTreeHelper.GetParent(this) is not null)
                return;
            if (_drag?.Source == this)
                EndTabDrag();
            ResetTabDragPreview();
            ReleaseTabStripHolds();
        });
    }

    /// <summary>A press that loses its capture (another window took it, say) never reaches
    /// PointerReleased, which drops the preview prepared for a drag. Deferred a turn so a
    /// drag that starts from that press, and then loses capture to the system drag loop,
    /// keeps the preview it uses.</summary>
    private void Tabs_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        DispatcherQueue.TryPost(() =>
        {
            if (_drag?.Source != this)
                ResetTabDragPreview();
        });
    }

    /// <summary>Covers the desktop outside every window for the drag, so the tab can be
    /// dropped there. The drop arrives inside the system's drag loop; the tear-off waits for
    /// it to finish, and acts even if the completion ended the session first.</summary>
    private Interop.TearOffDropTarget? ShowDesktopDropTarget(TabDragSession drag)
    {
        try
        {
            return Interop.TearOffDropTarget.Show(App.WindowHandles, point => DispatcherQueue.TryPost(() =>
            {
                if (ReferenceEquals(_drag, drag))
                    EndTabDrag();
                _host.TearOffTab(drag.Tab, drag.TearOffGrab, point);
            }));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            // The drag still works between windows; only tearing off is lost.
            App.ReportRecoverableError(exception);
            return null;
        }
    }
}
