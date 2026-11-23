using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.App.Controls;
using Resesh.App.ViewModels;
using Resesh.Core.Layout;

namespace Resesh.App;

// Pane splitters and the focused-pane outline drawn in GroupArea's overlay canvas.
public sealed partial class MainWindow
{
    private readonly Dictionary<CommunityToolkit.WinUI.Controls.GridSplitter, Border> _splitterLines = [];
    private readonly HashSet<Border> _paneGutters = [];
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _paneFocusBrush = new(Microsoft.UI.Colors.Transparent);
    private readonly HashSet<CommunityToolkit.WinUI.Controls.GridSplitter> _activeSplitters = [];

    private void ConfigureSplitter(CommunityToolkit.WinUI.Controls.GridSplitter splitter, Border line)
    {
        _splitterLines[splitter] = line;
        // Above the neighbouring panes, so the thicker highlighted line isn't drawn under them.
        Canvas.SetZIndex(line, 1);
        SplitterHighlight.Attach(splitter, active => SetSplitterActive(splitter, active));
    }

    private void SetSplitterActive(object sender, bool active)
    {
        if (sender is not CommunityToolkit.WinUI.Controls.GridSplitter splitter)
            return;
        if (!_splitterLines.TryGetValue(splitter, out var line))
        {
            // A splitter dropped by a layout rebuild reports going to rest when it unloads.
            // It must leave the active set even though its line is gone, or the focus frame
            // would stay hidden; it is never shown again, so it may not become active.
            if (!active && _activeSplitters.Remove(splitter))
                UpdatePaneFocusFrame();
            return;
        }

        if (active)
            _activeSplitters.Add(splitter);
        else
            _activeSplitters.Remove(splitter);
        line.Background = SplitterBrush(line, active);
        SplitterHighlight.SetLineThickness(
            line,
            columns: splitter.ResizeDirection == CommunityToolkit.WinUI.Controls.GridSplitter.GridResizeDirection.Columns,
            active);
        UpdatePaneFocusFrame();
    }

    /// <summary>Gutters stay neutral; pane focus is drawn by <see cref="UpdatePaneFocusFrame"/>.</summary>
    private Microsoft.UI.Xaml.Media.Brush? SplitterBrush(Border line, bool active)
    {
        if (active)
            return ThemeResources.Brush(line, "SessionSplitterHoverBrush");

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(
            _paneGutters.Contains(line) ? _themePalette.PaneBorder : _themePalette.Divider);
    }

    /// <summary>Outlines the focused pane (geometry: <see cref="PaneFrameGeometry"/>).
    /// Normally drawn only when split, on the gutters: a lone pane needs no focus mark.
    /// A session drawn in a theme other than the app's (a Crimson production session, say)
    /// is always outlined, split or not, on every side, in that theme's colour. A
    /// colour-tagged session is outlined the same way in its tag colour, wider. High
    /// Contrast keeps to system colours and draws no tag frame.</summary>
    private void UpdatePaneFocusFrame()
    {
        var sessionPalette = ViewModel.ActiveSessionTheme is { } theme
            ? ThemeVisualPalette.For(App.ResolveTheme(theme))
            : null;
        var tag = PresentationValues.ColorTag(ViewModel.ActiveColorTag);
        var tagged = tag.A > 0 && !_themePalette.IsHighContrast;
        // Palettes are records, so this compares colors: a theme override that matches the
        // app theme (or High Contrast, which overrides both) draws nothing special.
        var themed = (sessionPalette is not null && sessionPalette != _themePalette) || tagged;
        var focused = (ViewModel.IsSplit || themed)
            && _groupViews.TryGetValue(ViewModel.FocusedGroup, out var view)
                ? view
                : null;
        var color = tagged ? tag : (sessionPalette ?? _themePalette).PaneFocusBorder;
        var t = PaneFrameGeometry.Thickness(tagged);
        foreach (var groupView in _groupViews.Values)
        {
            groupView.SetFocusEdge(ReferenceEquals(groupView, focused) ? color : null, t);
            // Every pane keeps room for a frame whether it has one or not, so focus and tab
            // changes never resize or shift a terminal.
            if (PaneLayoutBounds(groupView) is { } laidOut)
            {
                var inset = PaneFrameGeometry.ReservedInset(
                    new FrameRect(laidOut.X, laidOut.Y, laidOut.Width, laidOut.Height),
                    GroupArea.ActualWidth, GroupArea.ActualHeight);
                groupView.SetContentInset(new Thickness(inset.Left, 0, inset.Right, inset.Bottom));
            }
        }

        _paneFocusBrush.Color = color;
        var bounds = focused is null ? null : PaneLayoutBounds(focused);
        // A hovered or dragged gutter shows its own highlight; the frame steps aside.
        var resizing = _activeSplitters.Any(splitter => splitter != TreeSplitter);
        if (bounds is not { } pane || resizing)
        {
            PaneFocusLeft.Visibility = PaneFocusRight.Visibility = PaneFocusBottom.Visibility = Visibility.Collapsed;
            return;
        }

        var frame = PaneFrameGeometry.For(
            new FrameRect(pane.X, pane.Y, pane.Width, pane.Height),
            GroupArea.ActualWidth, GroupArea.ActualHeight, focused!.ContentEdgeTop, t, allSides: themed);
        PlaceFrameEdge(PaneFocusLeft, frame.Left);
        PlaceFrameEdge(PaneFocusRight, frame.Right);
        PlaceFrameEdge(PaneFocusBottom, frame.Bottom);
    }

    private void PlaceFrameEdge(Microsoft.UI.Xaml.Shapes.Rectangle edge, FrameRect? rect)
    {
        edge.Visibility = rect is null ? Visibility.Collapsed : Visibility.Visible;
        if (rect is not { } shown)
            return;
        var (x, y, width, height) = shown;
        edge.Fill = _paneFocusBrush;
        Canvas.SetLeft(edge, x);
        Canvas.SetTop(edge, y);
        edge.Width = width;
        edge.Height = height;
    }

    /// <summary>A pane's laid-out rectangle in <see cref="GroupArea"/>. Layout offsets, not
    /// rendered bounds, so the frame lands where the pane settles while pane motion plays.</summary>
    private Windows.Foundation.Rect? PaneLayoutBounds(TabGroupView view)
    {
        if (!view.IsLoaded || view.ActualWidth <= 0 || view.ActualHeight <= 0)
            return null;
        double x = 0, y = 0;
        DependencyObject? node = view;
        while (node is UIElement element && !ReferenceEquals(node, GroupArea))
        {
            x += element.ActualOffset.X;
            y += element.ActualOffset.Y;
            node = VisualTreeHelper.GetParent(node);
        }
        return ReferenceEquals(node, GroupArea)
            ? new Windows.Foundation.Rect(x, y, view.ActualWidth, view.ActualHeight)
            : null;
    }
}
