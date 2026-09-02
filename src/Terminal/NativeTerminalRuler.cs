using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Resesh.Terminal;

/// <summary>WinUI scrolling input plus a non-hit-test overview layer for a native terminal HWND.</summary>
internal sealed class NativeTerminalRuler : Grid
{
    private const double RulerWidth = 24;
    private const double RailInset = 16;
    private const double TickHeight = 3;

    private readonly AnnotatedScrollBar _scrollBar = new();
    private readonly Canvas _annotations = new() { IsHitTestVisible = false };
    private readonly Border _viewport = new() { IsHitTestVisible = false, Opacity = 0.18 };
    private readonly IScrollController _controller;
    private readonly Queue<(int CorrelationId, int Target)> _pendingScrolls = new();
    private readonly HashSet<(int Lane, int Bucket, uint Color)> _paintBuckets = [];
    private IReadOnlyList<NativeTerminalApi.MarkRecord> _marks = [];
    private IReadOnlyList<int> _searchRows = [];
    private Func<ulong, string>? _markLabel;
    private int _viewTop;
    private int _viewportHeight = 1;
    private int _bufferHeight = 1;
    private int _nextCorrelationId;
    private bool _isSplit;
    private bool _isGroupFocused = true;
    private bool _paintPending;

    internal event Action<int>? ScrollRequested;
    internal event Action<ulong>? MarkRequested;

    internal NativeTerminalRuler()
    {
        Width = RulerWidth;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;

        AutomationProperties.SetAutomationId(_scrollBar, "NativeTerminalRuler");
        AutomationProperties.SetName(_scrollBar, "Terminal overview");
        _scrollBar.SmallChange = 1;
        _scrollBar.DetailLabelRequested += OnDetailLabelRequested;

        _controller = _scrollBar.ScrollController;
        _controller.ScrollToRequested += (_, args) =>
            args.CorrelationId = QueueScroll(args.Offset);
        _controller.ScrollByRequested += (_, args) =>
            args.CorrelationId = QueueScroll(_viewTop + args.OffsetDelta);
        _controller.AddScrollVelocityRequested += (_, args) =>
        {
            var rows = Math.Max(1, (int)Math.Round(Math.Abs(args.OffsetVelocity) / 12));
            args.CorrelationId = QueueScroll(_viewTop + Math.Sign(args.OffsetVelocity) * rows);
        };

        Children.Add(_scrollBar);
        Children.Add(_annotations);
        _annotations.Children.Add(_viewport);
        SizeChanged += (_, _) => QueuePaint();
    }

    internal void UpdateViewport(int viewTop, int viewportHeight, int bufferHeight, bool alternateBuffer)
    {
        _viewTop = Math.Max(0, viewTop);
        _viewportHeight = Math.Max(1, viewportHeight);
        _bufferHeight = Math.Max(_viewportHeight, bufferHeight);
        Visibility = alternateBuffer ? Visibility.Collapsed : Visibility.Visible;

        var maximum = Math.Max(0, _bufferHeight - _viewportHeight);
        _controller.SetValues(0, maximum, Math.Min(_viewTop, maximum), _viewportHeight);
        _controller.SetIsScrollable(maximum > 0 && !alternateBuffer);

        if (_pendingScrolls.Any(scroll => scroll.Target == _viewTop))
        {
            while (_pendingScrolls.Count > 0)
            {
                var pending = _pendingScrolls.Dequeue();
                _controller.NotifyRequestedScrollCompleted(pending.CorrelationId);
                if (pending.Target == _viewTop)
                    break;
            }
        }
        QueuePaint();
    }

    internal void UpdateAnnotations(
        IReadOnlyList<NativeTerminalApi.MarkRecord> marks,
        IReadOnlyList<int> searchRows,
        Func<ulong, string> markLabel)
    {
        _marks = marks;
        _searchRows = searchRows;
        _markLabel = markLabel;
        QueuePaint();
    }

    internal void SetPresentation(bool isSplit, bool isGroupFocused)
    {
        _isSplit = isSplit;
        _isGroupFocused = isGroupFocused;
        QueuePaint();
    }

    internal bool JumpCommand(bool previous)
    {
        NativeTerminalApi.MarkRecord target = default;
        foreach (var mark in _marks)
        {
            if (mark.Kind is not (NativeTerminalApi.MarkKind.ExactCommand or NativeTerminalApi.MarkKind.ApplicationCommand))
                continue;
            if (previous)
            {
                if (mark.Row < _viewTop && (target.Id == 0 || mark.Row > target.Row))
                    target = mark;
            }
            else if (mark.Row > _viewTop && (target.Id == 0 || mark.Row < target.Row))
            {
                target = mark;
            }
        }
        if (target.Id == 0)
            return false;
        MarkRequested?.Invoke(target.Id);
        return true;
    }

    internal NativeTerminalApi.MarkRecord? BookmarkAtRow(int row) =>
        _marks.FirstOrDefault(mark => mark.Kind == NativeTerminalApi.MarkKind.Bookmark && mark.Row == row) is var mark
            && mark.Id != 0 ? mark : null;

    private int QueueScroll(double requestedOffset)
    {
        var maximum = Math.Max(0, _bufferHeight - _viewportHeight);
        var target = Math.Clamp((int)Math.Round(requestedOffset), 0, maximum);
        if (target == _viewTop)
            return -1;
        _nextCorrelationId = _nextCorrelationId == int.MaxValue ? 0 : _nextCorrelationId + 1;
        var correlationId = _nextCorrelationId;
        _pendingScrolls.Enqueue((correlationId, target));
        ScrollRequested?.Invoke(target);
        return correlationId;
    }

    private void OnDetailLabelRequested(
        AnnotatedScrollBar sender,
        AnnotatedScrollBarDetailLabelRequestedEventArgs args)
    {
        var row = Math.Clamp((int)Math.Round(args.ScrollOffset), 0, Math.Max(0, _bufferHeight - 1));
        NativeTerminalApi.MarkRecord nearest = default;
        var nearestDistance = int.MaxValue;
        foreach (var mark in _marks)
        {
            if (mark.Kind == NativeTerminalApi.MarkKind.Bookmark)
                continue;
            var distance = Math.Abs(mark.Row - row);
            if (distance < nearestDistance)
            {
                nearest = mark;
                nearestDistance = distance;
            }
        }
        if (nearest.Id != 0 && nearestDistance <= Math.Max(1, _bufferHeight / Math.Max(1, (int)ActualHeight)))
        {
            var label = _markLabel?.Invoke(nearest.Id);
            args.Content = string.IsNullOrWhiteSpace(label)
                ? $"Line {nearest.Row + 1}"
                : $"Line {nearest.Row + 1}: {label}";
        }
        else
        {
            args.Content = $"Line {row + 1}";
        }
    }

    private void QueuePaint()
    {
        if (_paintPending)
            return;
        _paintPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _paintPending = false;
            Paint();
        });
    }

    private void Paint()
    {
        _annotations.Children.Clear();
        _annotations.Children.Add(_viewport);
        if (Visibility != Visibility.Visible || ActualHeight <= RailInset * 2 || _bufferHeight <= 0)
            return;

        var railHeight = ActualHeight - RailInset * 2;
        var calmOpacity = !_isSplit ? 1.0 : _isGroupFocused ? 0.55 : 0.32;
        _paintBuckets.Clear();
        foreach (var mark in _marks)
        {
            if (mark.Kind == NativeTerminalApi.MarkKind.Bookmark)
                AddTick(mark.Row, lane: 0, mark.Color, _isSplit ? Math.Max(calmOpacity, 0.75) : 1, _paintBuckets, railHeight);
            else
                AddTick(mark.Row, lane: 0, mark.Color, calmOpacity, _paintBuckets, railHeight);
        }

        var searchColor = GetThemeColor("AccentFillColorDefaultBrush", Color.FromArgb(255, 242, 204, 96));
        foreach (var row in _searchRows)
            AddTick(row, lane: 1, ToColorRef(searchColor), _isSplit ? Math.Max(calmOpacity, 0.65) : 1, _paintBuckets, railHeight);

        var maximum = Math.Max(1, _bufferHeight);
        var top = RailInset + _viewTop / (double)maximum * railHeight;
        var height = Math.Max(20, _viewportHeight / (double)maximum * railHeight);
        Canvas.SetTop(_viewport, Math.Min(top, Math.Max(RailInset, ActualHeight - RailInset - height)));
        Canvas.SetLeft(_viewport, 1);
        _viewport.Width = RulerWidth - 2;
        _viewport.Height = height;
        _viewport.Background = GetThemeBrush("ControlFillColorDefaultBrush");
    }

    private void AddTick(
        int row,
        int lane,
        uint colorRef,
        double opacity,
        HashSet<(int Lane, int Bucket, uint Color)> buckets,
        double railHeight)
    {
        var bucket = Math.Clamp(
            (int)Math.Round(row / (double)Math.Max(1, _bufferHeight) * railHeight / TickHeight),
            0,
            Math.Max(0, (int)(railHeight / TickHeight) - 1));
        if (!buckets.Add((lane, bucket, colorRef)))
            return;

        var rectangle = new Rectangle
        {
            Width = lane == 0 ? 5 : 10,
            Height = TickHeight,
            Fill = new SolidColorBrush(FromColorRef(colorRef)),
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rectangle, lane == 0 ? 1 : 8);
        Canvas.SetTop(rectangle, RailInset + bucket * TickHeight);
        _annotations.Children.Add(rectangle);
    }

    private static Brush GetThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Colors.Transparent);

    private static Color GetThemeColor(string key, Color fallback) =>
        GetThemeBrush(key) is SolidColorBrush brush ? brush.Color : fallback;

    internal static Color FromColorRef(uint value) => Color.FromArgb(
        255,
        (byte)(value & 0xff),
        (byte)((value >> 8) & 0xff),
        (byte)((value >> 16) & 0xff));

    private static uint ToColorRef(Color color) =>
        color.R | ((uint)color.G << 8) | ((uint)color.B << 16);
}
