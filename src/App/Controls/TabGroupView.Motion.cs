using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Resesh.App.ViewModels;

namespace Resesh.App.Controls;

// Tab strip motion. WinUI's item container transitions stay off: they run the add,
// delete and reorder phases one after another (a new tab's slot sits empty until the
// others finish moving) and they replay whenever a group view is re-parented. Instead,
// every change that moves tabs snapshots where each tab is drawn, holds the tabs there
// while layout settles, then slides them all to their new slots at once. New tabs fade
// in and the divider gap travels with the active tab. A closed tab goes at once: the
// list recycles its container immediately (unloaded, parked offscreen), so there is
// nothing left to fade. Layout rebuilds (splits, workspaces) snap instead of animating.
public sealed partial class TabGroupView
{
    private static readonly TimeSpan TabMoveDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan TabFadeInDuration = TimeSpan.FromMilliseconds(150);
    private static readonly Windows.UI.ViewManagement.UISettings MotionSettings = new();

    private readonly Dictionary<TabViewModel, (double Left, double Width)> _motionOrigins = [];
    private readonly Dictionary<UIElement, TabSlide> _tabSlides = [];
    private readonly HashSet<UIElement> _fadedInTabs = [];
    private readonly List<double> _dragItemOffsets = [];
    private bool _motionPending;
    private DispatcherQueueTimer? _overflowTimer;
    private UIElement? _raisedTab;
    private bool _motionSuppressed;
    private TabViewModel? _dividerSlideTab;
    private int _dividerSlideVersion;
    private bool _pointerInTabStrip;
    private bool _tabWidthHoldPending;
    private FrameworkElement? _tabContainerGrid;

    // A translation offset that decays to zero over TabMoveDuration, or a held one.
    private readonly record struct TabSlide(float From, long Start, bool Moving);

    private static bool MotionEnabled => MotionSettings.AnimationsEnabled;

    private void DisableTabContainerTransitions()
    {
        if (FindDescendant(Tabs, "TabListView") is ListViewBase list)
            list.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
    }

    /// <summary>Snaps the strip for a layout rebuild; changes in this dispatcher turn do not animate.</summary>
    public void SuppressTabMotion()
    {
        CancelTabMotion();
        if (_motionSuppressed)
            return;
        _motionSuppressed = true;
        DispatcherQueue.TryEnqueue(() => _motionSuppressed = false);
    }

    private void CancelTabMotion()
    {
        if (_motionPending)
        {
            Tabs.LayoutUpdated -= Tabs_MotionLayoutUpdated;
            _motionPending = false;
        }
        _motionOrigins.Clear();
        LetTabsDrawPastStrip(false);
        LowerRaisedTab();
        foreach (var element in _tabSlides.Keys.ToList())
            SetTranslation(element, 0);
        foreach (var element in _fadedInTabs)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
        }
        _fadedInTabs.Clear();
        CancelDividerSlide();
    }

    // ---- snapshot ----

    /// <summary>Records where every tab is drawn before a change moves them.</summary>
    private void CaptureTabMotion()
    {
        // The first snapshot of a burst is what is on screen; later ones would record
        // positions the tabs are only being held at.
        if (_motionPending || _motionSuppressed || !MotionEnabled || !Tabs.IsLoaded)
            return;
        foreach (var tab in Group.Tabs)
        {
            if (DrawnTabLeft(tab) is { } left && DrawnTabWidth(tab) is { } width)
                _motionOrigins[tab] = (left, width);
        }
        if (_motionOrigins.Count == 0)
            return;
        _motionPending = true;
        LetTabsDrawPastStrip(true);
        Tabs.LayoutUpdated += Tabs_MotionLayoutUpdated;
        // Some changes (a cancelled drag) move nothing in layout; still get a pass.
        FindDescendant(Tabs, "TabsItemsPresenter")?.InvalidateMeasure();
        HoldTabsAtOrigins();
    }

    private double? DrawnTabLeft(TabViewModel tab)
    {
        if (Tabs.ContainerFromItem(tab) is not FrameworkElement { ActualWidth: > 0 } container)
            return null;
        if (DragPreviewFor(container) is { } preview)
            return ((TranslateTransform)preview.RenderTransform).X + InFlightOffset(preview);
        return LayoutLeft(container) + InFlightOffset(container);
    }

    private double? DrawnTabWidth(TabViewModel tab) =>
        Tabs.ContainerFromItem(tab) is FrameworkElement { ActualWidth: > 0 } container
            ? container.ActualWidth
            : null;

    private Image? DragPreviewFor(UIElement container)
    {
        if (_tabDragPreview.Visibility != Visibility.Visible)
            return null;
        foreach (var entry in _dragItems)
        {
            if (ReferenceEquals(entry.Item, container))
                return entry.Preview;
        }
        return null;
    }

    // The container's layout position in strip coordinates, ignoring its composition
    // translation (TransformToVisual on the container itself would depend on how that
    // translation is applied).
    private double LayoutLeft(FrameworkElement container) =>
        VisualTreeHelper.GetParent(container) is UIElement panel
            ? panel.TransformToVisual(TabStripHost).TransformPoint(default).X
                + LayoutInformation.GetLayoutSlot(container).X
            : container.TransformToVisual(TabStripHost).TransformPoint(default).X;

    private void HoldTabsAtOrigins()
    {
        RaiseActiveTab();
        foreach (var tab in Group.Tabs)
        {
            if (Tabs.ContainerFromItem(tab) is not FrameworkElement { ActualWidth: > 0 } container)
                continue;
            if (_motionOrigins.TryGetValue(tab, out var origin))
            {
                SetTranslation(container, (float)(origin.Left - LayoutLeft(container)));
            }
            else
            {
                // New since the snapshot: keep it hidden until it can fade in.
                ElementCompositionPreview.GetElementVisual(container).Opacity = 0;
                _fadedInTabs.Add(container);
            }
        }
    }

    // ---- playback ----

    private void Tabs_MotionLayoutUpdated(object? sender, object e)
    {
        // Wait for the queued width passes so every tab moves once, to its final slot.
        if (_tabWidthRefreshQueued || _fullTabWidthRefreshQueued)
        {
            HoldTabsAtOrigins();
            return;
        }
        Tabs.LayoutUpdated -= Tabs_MotionLayoutUpdated;
        _motionPending = false;
        RaiseActiveTab();

        foreach (var tab in Group.Tabs)
        {
            if (Tabs.ContainerFromItem(tab) is not FrameworkElement { ActualWidth: > 0 } container)
                continue;
            if (_motionOrigins.TryGetValue(tab, out var origin))
            {
                var offset = (float)(origin.Left - LayoutLeft(container));
                if (Math.Abs(offset) >= 0.5f)
                    SlideElement(container, offset);
                else
                    SetTranslation(container, 0);
            }
            else
            {
                SetTranslation(container, 0);
                FadeInTab(container);
            }
        }
        SlideDividerWithActiveTab();
        _motionOrigins.Clear();
        ReleaseTabOverflowAfterMotion();
    }

    // A held tab slides in from beyond the strip's already-shrunk viewport; let it draw
    // there for the length of the slide. Off otherwise, so overflowing tabs never paint
    // over the scroll buttons.
    private void LetTabsDrawPastStrip(bool allow)
    {
        _overflowTimer?.Stop();
        if (_tabScrollViewer is { } viewer)
            viewer.CanContentRenderOutsideBounds = allow;
    }

    private void ReleaseTabOverflowAfterMotion()
    {
        _overflowTimer ??= DispatcherQueue.CreateTimer();
        _overflowTimer.Stop();
        _overflowTimer.Interval = TabMoveDuration + TimeSpan.FromMilliseconds(20);
        _overflowTimer.IsRepeating = false;
        _overflowTimer.Tick -= OverflowTimer_Tick;
        _overflowTimer.Tick += OverflowTimer_Tick;
        _overflowTimer.Start();
    }

    private void OverflowTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_motionPending)
            return;
        LetTabsDrawPastStrip(false);
        LowerRaisedTab();
    }

    // Later tabs draw over earlier ones, and inactive headers are transparent. While tabs
    // pass each other, keep the (opaque) active tab on top so it slides over neighbours.
    private void RaiseActiveTab()
    {
        var active = Group.SelectedTab is { } tab ? Tabs.ContainerFromItem(tab) as UIElement : null;
        if (ReferenceEquals(active, _raisedTab))
            return;
        LowerRaisedTab();
        if (active is null)
            return;
        Canvas.SetZIndex(active, 1);
        _raisedTab = active;
    }

    private void LowerRaisedTab()
    {
        if (_raisedTab is not null)
            Canvas.SetZIndex(_raisedTab, 0);
        _raisedTab = null;
    }

    private float InFlightOffset(UIElement element)
    {
        if (!_tabSlides.TryGetValue(element, out var slide))
            return 0;
        if (!slide.Moving)
            return slide.From;
        var progress = Stopwatch.GetElapsedTime(slide.Start) / TabMoveDuration;
        if (progress >= 1)
        {
            _tabSlides.Remove(element);
            return 0;
        }
        // Matches the power-3 ease-out used by SlideElement.
        var remaining = 1 - progress;
        return (float)(slide.From * remaining * remaining * remaining);
    }

    private void SetTranslation(UIElement element, float offset)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", new Vector3(offset, 0, 0));
        if (offset == 0)
            _tabSlides.Remove(element);
        else
            _tabSlides[element] = new TabSlide(offset, 0, Moving: false);
    }

    private void SlideElement(UIElement element, float fromOffset)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(fromOffset, 0, 0));
        slide.InsertKeyFrame(1f, Vector3.Zero, EaseOut(compositor));
        slide.Duration = TabMoveDuration;
        visual.StartAnimation("Translation", slide);
        _tabSlides[element] = new TabSlide(fromOffset, Stopwatch.GetTimestamp(), Moving: true);
    }

    private static CompositionEasingFunction EaseOut(Compositor compositor) =>
        CompositionEasingFunction.CreatePowerEasingFunction(compositor, CompositionEasingFunctionMode.Out, 3f);

    private void FadeInTab(UIElement container)
    {
        var visual = ElementCompositionPreview.GetElementVisual(container);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, EaseOut(visual.Compositor));
        fade.Duration = TabFadeInDuration;
        visual.StartAnimation("Opacity", fade);
        _fadedInTabs.Remove(container);
    }

    // ---- divider gap ----

    // The divider is two segments whose widths leave a gap under the active tab. While
    // that tab slides, scale the segments from their anchored ends so the gap stays
    // under it; then hand back to UpdateTabStripDivider for exact widths. The gap
    // follows whichever tab is active now from where that tab was drawn, so closing the
    // active tab moves the gap with its neighbour as it slides in. A new tab has no
    // earlier position and gets the gap directly.
    private void SlideDividerWithActiveTab()
    {
        if (Group.SelectedTab is not { } active
            || !_motionOrigins.TryGetValue(active, out var origin)
            || Tabs.ContainerFromItem(active) is not FrameworkElement { ActualWidth: > 0 } container)
        {
            UpdateTabStripDivider();
            return;
        }
        var span = (Left: origin.Left, Right: origin.Left + origin.Width);

        var stripWidth = TabStripHost.ActualWidth;
        var left = LayoutLeft(container);
        var toLeft = Math.Clamp(left, 0, stripWidth);
        var toRight = stripWidth - Math.Clamp(left + container.ActualWidth, 0, stripWidth);
        var fromLeft = Math.Clamp(span.Left, 0, stripWidth);
        var fromRight = stripWidth - Math.Clamp(span.Right, 0, stripWidth);
        if (Math.Abs(fromLeft - toLeft) < 0.5 && Math.Abs(fromRight - toRight) < 0.5)
        {
            UpdateTabStripDivider();
            return;
        }

        CancelDividerSlide();
        _dividerSlideTab = active;
        var version = ++_dividerSlideVersion;
        var compositor = ElementCompositionPreview.GetElementVisual(LeftTabStripDivider).Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        ScaleDivider(LeftTabStripDivider, fromLeft, toLeft, anchorRight: false);
        ScaleDivider(RightTabStripDivider, fromRight, toRight, anchorRight: true);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _dividerSlideVersion)
                return;
            CancelDividerSlide();
            UpdateTabStripDivider();
        });
    }

    private void ScaleDivider(Rectangle segment, double from, double to, bool anchorRight)
    {
        var width = Math.Max(from, to);
        segment.Width = width;
        if (width <= 0)
            return;
        var visual = ElementCompositionPreview.GetElementVisual(segment);
        visual.CenterPoint = new Vector3(anchorRight ? (float)width : 0, 0, 0);
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3((float)(from / width), 1, 1));
        scale.InsertKeyFrame(1f, new Vector3((float)(to / width), 1, 1), EaseOut(visual.Compositor));
        scale.Duration = TabMoveDuration;
        visual.StartAnimation("Scale", scale);
    }

    private void CancelDividerSlide()
    {
        if (_dividerSlideTab is null)
            return;
        _dividerSlideTab = null;
        _dividerSlideVersion++;
        foreach (var segment in new[] { LeftTabStripDivider, RightTabStripDivider })
        {
            var visual = ElementCompositionPreview.GetElementVisual(segment);
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
        }
    }

    /// <summary>True while tab motion owns the divider; UpdateTabStripDivider leaves it alone.</summary>
    private bool TabMotionOwnsDivider()
    {
        // Holding the old gap until the tabs start moving avoids it jumping ahead of them.
        if (_motionPending)
            return true;
        if (_dividerSlideTab is null)
            return false;
        if (ReferenceEquals(Group.SelectedTab, _dividerSlideTab) && _tabDragPreview.Visibility != Visibility.Visible)
            return true;
        CancelDividerSlide();
        return false;
    }

    // ---- drag previews ----

    /// <summary>Slides a neighbour's drag preview when the gap for the dragged tab moves past it.</summary>
    private void ShiftDragPreview(int index, double offset)
    {
        var previous = _dragItemOffsets[index];
        _dragItemOffsets[index] = offset;
        var preview = _dragItems[index].Preview;
        if (previous != offset && MotionEnabled && preview.Visibility == Visibility.Visible)
            SlideElement(preview, (float)(previous - offset + InFlightOffset(preview)));
    }

    // ---- browser-style width hold ----

    private void TrackTabStripPointer()
    {
        // The same element WinUI watches to release its held tab widths.
        if (FindDescendant(Tabs, "TabContainerGrid") is not { } grid || ReferenceEquals(grid, _tabContainerGrid))
            return;
        if (_tabContainerGrid is not null)
        {
            _tabContainerGrid.PointerEntered -= TabContainerGrid_PointerEntered;
            _tabContainerGrid.PointerExited -= TabContainerGrid_PointerExited;
        }
        _tabContainerGrid = grid;
        grid.PointerEntered += TabContainerGrid_PointerEntered;
        grid.PointerExited += TabContainerGrid_PointerExited;
    }

    private void TabContainerGrid_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        _pointerInTabStrip = true;

    private void TabContainerGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInTabStrip = false;
        if (!_tabWidthHoldPending)
            return;
        _tabWidthHoldPending = false;
        CaptureTabMotion();
        QueueFullTabWidthRefresh();
    }
}
