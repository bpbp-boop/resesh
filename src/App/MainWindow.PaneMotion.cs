using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Resesh.App.Controls;
using Resesh.App.ViewModels;
using Windows.Foundation;

namespace Resesh.App;

// Pane motion for layout changes (split, collapse, zoom, unzoom). Before a change, every
// pane's rectangle is recorded; once the rebuilt layout settles, each pane animates from
// where it was drawn to its new slot:
//   - a growing pane's frame travels from the old rectangle to the new one with its content
//     pinned to the top-left, so text is never stretched;
//   - a shrinking pane snaps, and its neighbours cover the space it gave up;
//   - a split's new pane steps aside: the tab that moved starts where it was shown and
//     slides over into its new slot, uncovering the pane it left;
//   - other new panes (an unzoom) slide in from the side away from the panes that stayed;
//   - a tab moved into a pane that was already open slides across from the pane it left.
// Panes are never resized mid-animation: the terminal reflows once, at its final size, and
// composition only moves and clips what is already drawn. Nothing fades: a terminal's swap
// chain composes near-black under partial opacity.
public sealed partial class MainWindow
{
    private static readonly TimeSpan PaneMoveDuration = TimeSpan.FromMilliseconds(170);
    private const int PaneSettleLimit = 6;

    private static readonly Windows.UI.ViewManagement.UISettings PaneMotionSettings = new();

    private readonly Dictionary<TabGroupView, Rect> _paneOrigins = [];
    // Panes whose visuals motion has touched. Others are left alone: enabling Translation or
    // writing to a pane's visual makes its terminal's swap chain show blank until it next
    // presents, which flashed black on panes that only snap.
    private readonly HashSet<TabGroupView> _movedPanes = [];
    // A split's new pane, and the pane its tab was shown in before the split.
    private readonly Dictionary<TabGroupView, TabGroupView> _steppingAside = [];
    // A tab sliding into a pane that was already open, and what was raised to draw it on top.
    private UIElement? _slidingTab;
    private readonly List<(UIElement Element, int ZIndex)> _slideRaised = [];
    private bool _paneMotionCaptured;
    private bool _paneMotionWaiting;
    private int _paneSettlePasses;

    private static bool PaneMotionEnabled => PaneMotionSettings.AnimationsEnabled;

    /// <summary>Records where every pane is drawn. Call before anything moves panes; the
    /// first capture in a dispatcher turn wins, so nested changes keep the on-screen state.</summary>
    private void CapturePaneMotion()
    {
        if (_paneMotionCaptured || !PaneMotionEnabled || !GroupArea.IsLoaded)
            return;
        foreach (var view in _groupViews.Values)
        {
            if (PaneRect(view) is { } rect)
                _paneOrigins[view] = rect;
        }
        if (_paneOrigins.Count == 0)
            return;
        _paneMotionCaptured = true;
        // A capture nothing plays (a change that turned out not to rebuild) must not
        // linger into the next one.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_paneMotionWaiting)
                ResetPaneMotionCapture();
        });
    }

    /// <summary>Marks a split's new pane to start where its tab was shown, in
    /// <paramref name="source"/>, instead of sliding in from the edge.</summary>
    private void StepPaneAside(TabGroupView pane, TabGroupView source)
    {
        if (_paneMotionCaptured)
            _steppingAside[pane] = source;
    }

    /// <summary>Animates panes from the captured rectangles once the rebuilt layout settles.</summary>
    private void PlayPaneMotion()
    {
        if (!_paneMotionCaptured || _paneMotionWaiting)
            return;
        _paneMotionWaiting = true;
        _paneSettlePasses = 0;
        GroupArea.LayoutUpdated += GroupArea_PaneMotionLayoutUpdated;
    }

    private void GroupArea_PaneMotionLayoutUpdated(object? sender, object e)
    {
        var views = _groupViews.Values.Where(view => view.Parent is not null).ToList();
        var rects = views.ToDictionary(view => view, PaneRect);
        // Panes built for the change measure over a pass or two; wait so each moves once.
        if (rects.Values.Any(rect => rect is null) && ++_paneSettlePasses < PaneSettleLimit)
            return;
        GroupArea.LayoutUpdated -= GroupArea_PaneMotionLayoutUpdated;

        var stayed = rects
            .Where(entry => entry.Value is not null && _paneOrigins.ContainsKey(entry.Key))
            .Select(entry => entry.Value!.Value)
            .ToList();
        _movedPanes.RemoveWhere(view => !_groupViews.ContainsValue(view));
        foreach (var (view, rect) in rects)
        {
            if (rect is not { } to)
                continue;
            StopPaneMotion(view);
            if (_steppingAside.TryGetValue(view, out var source) && _paneOrigins.TryGetValue(source, out var shownAt))
                StepAside(view, shownAt, to);
            else if (_paneOrigins.TryGetValue(view, out var from))
                MovePane(view, from, to);
            else
                EnterPane(view, to, stayed);
        }
        ResetPaneMotionCapture();
    }

    private void ResetPaneMotionCapture()
    {
        if (_paneMotionWaiting)
            GroupArea.LayoutUpdated -= GroupArea_PaneMotionLayoutUpdated;
        _paneMotionWaiting = false;
        _paneMotionCaptured = false;
        _paneOrigins.Clear();
        _steppingAside.Clear();
    }

    private Rect? PaneRect(TabGroupView view)
    {
        if (!view.IsLoaded || view.ActualWidth <= 0 || view.ActualHeight <= 0 || view.Parent is null)
            return null;
        try
        {
            return view.TransformToVisual(GroupArea)
                .TransformBounds(new Rect(0, 0, view.ActualWidth, view.ActualHeight));
        }
        catch (ArgumentException)
        {
            // Not in GroupArea's tree (yet).
            return null;
        }
    }

    // ---- surviving panes ----

    private void MovePane(TabGroupView view, Rect from, Rect to)
    {
        if (Near(from.X, to.X) && Near(from.Y, to.Y) && Near(from.Width, to.Width) && Near(from.Height, to.Height))
            return;
        RevealPane(view, from, to);
    }

    // The pane's visible frame travels from the old rectangle to the new one with the
    // content pinned to its top-left corner, so a growing pane opens out from where it was
    // and its tabs and prompt ride along with the edge. A pane that only shrinks snaps:
    // its neighbours cover the space it gave up.
    private void RevealPane(TabGroupView view, Rect from, Rect to)
    {
        if (from.Width >= to.Width - 0.5 && from.Height >= to.Height - 0.5
            && !(Near(from.Width, to.Width) && Near(from.Height, to.Height)))
            return;

        ElementCompositionPreview.SetIsTranslationEnabled(view, true);
        _movedPanes.Add(view);
        var visual = ElementCompositionPreview.GetElementVisual(view);
        var compositor = visual.Compositor;
        var offset = new Vector3((float)(from.X - to.X), (float)(from.Y - to.Y), 0);
        if (offset.LengthSquared() >= 0.25f)
        {
            var move = compositor.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0f, offset);
            move.InsertKeyFrame(1f, Vector3.Zero, PaneEaseOut(compositor));
            move.Duration = PaneMoveDuration;
            visual.StartAnimation("Translation", move);
        }

        var right = (float)Math.Max(0, to.Width - from.Width);
        var bottom = (float)Math.Max(0, to.Height - from.Height);
        if (right < 0.5f && bottom < 0.5f)
            return;
        var clip = compositor.CreateInsetClip(0, 0, right, bottom);
        visual.Clip = clip;
        foreach (var (property, start) in new[] { ("RightInset", right), ("BottomInset", bottom) })
        {
            if (start < 0.5f)
                continue;
            var open = compositor.CreateScalarKeyFrameAnimation();
            open.InsertKeyFrame(0f, start);
            open.InsertKeyFrame(1f, 0f, PaneEaseOut(compositor));
            open.Duration = PaneMoveDuration;
            clip.StartAnimation(property, open);
        }
    }

    // ---- new panes ----

    // The new pane starts at the top-left of where its tab was shown and slides into its
    // slot. It draws above the pane it came from while it passes over it.
    private void StepAside(TabGroupView view, Rect shownAt, Rect to)
    {
        var offset = new Vector3((float)(shownAt.X - to.X), (float)(shownAt.Y - to.Y), 0);
        if (offset.LengthSquared() < 0.25f)
            return;
        ElementCompositionPreview.SetIsTranslationEnabled(view, true);
        _movedPanes.Add(view);
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(view, 2);
        var visual = ElementCompositionPreview.GetElementVisual(view);
        var compositor = visual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0f, offset);
        move.InsertKeyFrame(1f, Vector3.Zero, PaneEaseOut(compositor));
        move.Duration = PaneMoveDuration;
        visual.StartAnimation("Translation", move);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(view, 0));
    }

    // A new pane slides in from its far side (the side facing away from the panes that
    // stayed: a split's new pane from the window edge, panes an unzoom brings back from
    // around the pane that was zoomed), clipped to its own slot so it never passes over a
    // neighbour. It never fades: a terminal's swap chain composes near-black under partial
    // opacity instead of dimmed.
    private void EnterPane(TabGroupView view, Rect to, IReadOnlyList<Rect> stayed)
    {
        var direction = EnterDirection(to, stayed);
        if (direction == Vector2.Zero)
            return;
        ElementCompositionPreview.SetIsTranslationEnabled(view, true);
        _movedPanes.Add(view);
        var visual = ElementCompositionPreview.GetElementVisual(view);
        var compositor = visual.Compositor;
        var offset = direction * new Vector2((float)to.Width, (float)to.Height);

        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0f, new Vector3(offset, 0));
        move.InsertKeyFrame(1f, Vector3.Zero, PaneEaseOut(compositor));
        move.Duration = PaneMoveDuration;
        visual.StartAnimation("Translation", move);

        // The clip is in the pane's own (translated) space: hide whatever has moved past
        // its slot. Same duration and easing as the slide, so the two stay in step.
        var (property, start) = direction.X > 0 ? ("RightInset", offset.X)
            : direction.X < 0 ? ("LeftInset", -offset.X)
            : direction.Y > 0 ? ("BottomInset", offset.Y)
            : ("TopInset", -offset.Y);
        var clip = compositor.CreateInsetClip();
        visual.Clip = clip;
        var open = compositor.CreateScalarKeyFrameAnimation();
        open.InsertKeyFrame(0f, start);
        open.InsertKeyFrame(1f, 0f, PaneEaseOut(compositor));
        open.Duration = PaneMoveDuration;
        clip.StartAnimation(property, open);
    }

    private static Vector2 EnterDirection(Rect to, IReadOnlyList<Rect> anchors)
    {
        var center = new Vector2((float)(to.X + to.Width / 2), (float)(to.Y + to.Height / 2));
        var direction = Vector2.Zero;
        foreach (var anchor in anchors)
            direction += center - new Vector2((float)(anchor.X + anchor.Width / 2), (float)(anchor.Y + anchor.Height / 2));
        if (direction.LengthSquared() < 1)
            return Vector2.Zero;
        // Slide along one axis only: diagonal entries read as drift, not as a split.
        return Math.Abs(direction.X) >= Math.Abs(direction.Y)
            ? new Vector2(Math.Sign(direction.X), 0)
            : new Vector2(0, Math.Sign(direction.Y));
    }

    // ---- a tab moving between panes ----

    /// <summary>Where a pane's terminals are drawn, or null when motion is off or the pane
    /// is not laid out.</summary>
    private Rect? TerminalRect(TabGroupView view)
    {
        var area = view.TerminalArea;
        if (!PaneMotionEnabled || !area.IsLoaded || area.ActualWidth <= 0 || area.ActualHeight <= 0)
            return null;
        try
        {
            return area.TransformToVisual(GroupArea)
                .TransformBounds(new Rect(0, 0, area.ActualWidth, area.ActualHeight));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // A tab moved into a pane that was already open: its content starts where it was shown
    // and slides over into its new pane, uncovering the tab its old pane shows next. The
    // layout does not change, so only the tab's own view moves, at its final size. Its pane
    // and that pane's ancestors draw above the rest of the layout until the slide ends.
    private void SlideTabAcross(UIElement content, TabGroupView target, Rect from, Rect to)
    {
        StopTabSlide();
        var offset = new Vector3((float)(from.X - to.X), (float)(from.Y - to.Y), 0);
        if (offset.LengthSquared() < 0.25f)
            return;

        for (var element = (DependencyObject)target;
            element is UIElement raised && !ReferenceEquals(element, GroupArea);
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            _slideRaised.Add((raised, Microsoft.UI.Xaml.Controls.Canvas.GetZIndex(raised)));
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(raised, 2);
        }

        ElementCompositionPreview.SetIsTranslationEnabled(content, true);
        _slidingTab = content;
        var visual = ElementCompositionPreview.GetElementVisual(content);
        var compositor = visual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0f, offset);
        move.InsertKeyFrame(1f, Vector3.Zero, PaneEaseOut(compositor));
        move.Duration = PaneMoveDuration;
        visual.StartAnimation("Translation", move);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            // A later move may have started its own slide in the meantime.
            if (ReferenceEquals(_slidingTab, content))
                StopTabSlide();
        });
    }

    /// <summary>Puts a sliding tab in its slot and the layout's draw order back.</summary>
    private void StopTabSlide()
    {
        if (_slidingTab is { } content)
        {
            var visual = ElementCompositionPreview.GetElementVisual(content);
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            _slidingTab = null;
        }
        foreach (var (element, zIndex) in _slideRaised)
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(element, zIndex);
        _slideRaised.Clear();
    }

    private void StopPaneMotion(TabGroupView view)
    {
        if (!_movedPanes.Remove(view))
            return;
        // Translation is only an animatable property once enabled; StopAnimation throws otherwise.
        ElementCompositionPreview.SetIsTranslationEnabled(view, true);
        var visual = ElementCompositionPreview.GetElementVisual(view);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.Clip = null;
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(view, 0);
    }

    private static CompositionEasingFunction PaneEaseOut(Compositor compositor) =>
        CompositionEasingFunction.CreatePowerEasingFunction(compositor, CompositionEasingFunctionMode.Out, 3f);

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.5;
}
