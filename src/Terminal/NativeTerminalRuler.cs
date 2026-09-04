using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Resesh.Terminal;

/// <summary>WinUI scrolling input plus an overview layer for the composition terminal.</summary>
internal sealed class NativeTerminalRuler : Grid
{
    private const double GutterWidth = 4;
    private const double ScrollBarWidth = 14;
    private const double RulerWidth = GutterWidth + ScrollBarWidth;
    private const double RailInset = 16;
    private const double TickHeight = 3;

    private readonly ScrollBar _scrollBar = new();
    private readonly Canvas _annotations = new() { IsHitTestVisible = false };
    private readonly Canvas _markTipLayer = new() { IsHitTestVisible = false };
    private readonly Border _markTipAnchor = new()
    {
        Width = GutterWidth,
        Height = TickHeight,
        Background = new SolidColorBrush(Colors.Transparent),
        IsHitTestVisible = false,
    };
    private readonly Border _annotationInput = new()
    {
        Width = GutterWidth,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Stretch,
        Background = new SolidColorBrush(Colors.Transparent),
    };
    private readonly TeachingTip _markTip = new()
    {
        PreferredPlacement = TeachingTipPlacementMode.Left,
        IsLightDismissEnabled = false,
        ShouldConstrainToRootBounds = true,
    };
    private readonly TextBlock _markTitle = new()
    {
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 320,
    };
    private readonly TextBlock _markMetadata = new()
    {
        Opacity = 0.7,
        Margin = new Thickness(0, 4, 0, 0),
    };
    private readonly Button _jumpButton = new() { Content = "Jump", Margin = new Thickness(0, 8, 8, 0) };
    private readonly Button _copyButton = new() { Content = "Copy output", Margin = new Thickness(0, 8, 0, 0) };
    private readonly Queue<(int CorrelationId, int Target)> _pendingScrolls = new();
    private readonly HashSet<(int Lane, int Bucket, uint Color)> _paintBuckets = [];
    private IReadOnlyList<NativeTerminalApi.MarkRecord> _marks = [];
    private IReadOnlyList<int> _searchRows = [];
    private IReadOnlyList<NativeTerminalApi.HighlightRowRecord> _highlightRows = [];
    private Func<ulong, string>? _markLabel;
    private ulong _activeMarkId;
    private ulong _pendingMarkTipId;
    private int _viewTop;
    private int _viewportHeight = 1;
    private int _bufferHeight = 1;
    private int _nextCorrelationId;
    private bool _isSplit;
    private bool _isGroupFocused = true;
    private bool _paintPending;
    private bool _updatingFromTerminal;
    private bool _markTipClosing;
    private double _wheelDelta;

    internal event Action<int>? ScrollRequested;
    internal event Action<ulong>? MarkRequested;
    internal event Func<ulong, bool>? CopyRequested;

    internal NativeTerminalRuler()
    {
        _markTip.Target = _markTipAnchor;
        _markTip.Content = new StackPanel
        {
            Children =
            {
                _markTitle,
                _markMetadata,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { _jumpButton, _copyButton },
                },
            },
        };
        AutomationProperties.SetName(_jumpButton, "Jump to terminal mark");
        AutomationProperties.SetName(_copyButton, "Copy terminal output");
        _jumpButton.Click += (_, _) =>
        {
            if (_activeMarkId != 0)
                MarkRequested?.Invoke(_activeMarkId);
            CloseMarkPreview();
        };
        _copyButton.Click += (_, _) =>
        {
            if (_activeMarkId != 0)
                CopyRequested?.Invoke(_activeMarkId);
            CloseMarkPreview();
        };
        _markTip.Closed += (_, _) =>
        {
            _markTipClosing = false;
            QueueOpenMarkPreview();
        };
        Width = RulerWidth;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
        BorderThickness = new Thickness(1, 0, 0, 0);
        BorderBrush = GetThemeBrush("DividerStrokeColorDefaultBrush", new SolidColorBrush(Color.FromArgb(255, 60, 60, 60)));
        Background = GetThemeBrush("LayerFillColorDefaultBrush", new SolidColorBrush(Color.FromArgb(255, 20, 20, 20)));

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ScrollBarWidth) });

        AutomationProperties.SetAutomationId(_scrollBar, "NativeTerminalRuler");
        AutomationProperties.SetName(_scrollBar, "Terminal overview");
        _scrollBar.Orientation = Orientation.Vertical;
        _scrollBar.Width = ScrollBarWidth;
        _scrollBar.HorizontalAlignment = HorizontalAlignment.Right;
        _scrollBar.VerticalAlignment = VerticalAlignment.Stretch;
        _scrollBar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
        _scrollBar.IsTabStop = false;
        _scrollBar.SmallChange = 1;
        _scrollBar.Scroll += OnScrollBarScroll;
        Grid.SetColumn(_scrollBar, 1);

        _annotations.Width = GutterWidth;
        _annotations.HorizontalAlignment = HorizontalAlignment.Left;
        _annotations.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(_annotations, 0);
        _markTipLayer.Width = GutterWidth;
        _markTipLayer.HorizontalAlignment = HorizontalAlignment.Left;
        _markTipLayer.VerticalAlignment = VerticalAlignment.Stretch;
        _markTipLayer.Children.Add(_markTipAnchor);
        Grid.SetColumn(_markTipLayer, 0);
        Grid.SetColumn(_annotationInput, 0);
        AutomationProperties.SetAutomationId(_annotationInput, "NativeTerminalAnnotations");
        AutomationProperties.SetName(_annotationInput, "Terminal annotations");
        AutomationProperties.SetAutomationId(_markTipAnchor, "NativeTerminalMarkTipAnchor");

        _annotationInput.AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        _annotationInput.AddHandler(PointerExitedEvent, new PointerEventHandler(OnPointerExited), handledEventsToo: true);
        _annotationInput.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), handledEventsToo: true);
        Unloaded += (_, _) => CloseMarkPreview();

        Children.Add(_annotations);
        Children.Add(_annotationInput);
        Children.Add(_scrollBar);
        Children.Add(_markTipLayer);
        Children.Add(_markTip);
        SizeChanged += (_, _) => QueuePaint();
    }

    internal void UpdateViewport(int viewTop, int viewportHeight, int bufferHeight, bool alternateBuffer)
    {
        _viewTop = Math.Max(0, viewTop);
        _viewportHeight = Math.Max(1, viewportHeight);
        _bufferHeight = Math.Max(_viewportHeight, bufferHeight);
        Visibility = alternateBuffer ? Visibility.Collapsed : Visibility.Visible;
        if (alternateBuffer)
            CloseMarkPreview();

        if (_pendingScrolls.Any(scroll => scroll.Target == _viewTop))
        {
            while (_pendingScrolls.Count > 0)
            {
                var pending = _pendingScrolls.Dequeue();
                if (pending.Target == _viewTop)
                    break;
            }
        }

        var maximum = Math.Max(0, _bufferHeight - _viewportHeight);
        var displayedTop = _pendingScrolls.Count > 0 ? _pendingScrolls.Last().Target : _viewTop;
        _updatingFromTerminal = true;
        try
        {
            _scrollBar.Minimum = 0;
            _scrollBar.Maximum = maximum;
            _scrollBar.Value = Math.Min(displayedTop, maximum);
            _scrollBar.ViewportSize = _viewportHeight;
            _scrollBar.LargeChange = Math.Max(1, _viewportHeight);
            _scrollBar.IsEnabled = maximum > 0 && !alternateBuffer;
        }
        finally
        {
            _updatingFromTerminal = false;
        }

        QueuePaint();
    }

    internal void UpdateAnnotations(
        IReadOnlyList<NativeTerminalApi.MarkRecord> marks,
        IReadOnlyList<int> searchRows,
        Func<ulong, string> markLabel) =>
        UpdateAnnotations(marks, searchRows, [], markLabel);

    internal void UpdateAnnotations(
        IReadOnlyList<NativeTerminalApi.MarkRecord> marks,
        IReadOnlyList<int> searchRows,
        IReadOnlyList<NativeTerminalApi.HighlightRowRecord> highlightRows,
        Func<ulong, string> markLabel)
    {
        _marks = marks;
        _searchRows = searchRows;
        _highlightRows = highlightRows;
        _markLabel = markLabel;
        if (_activeMarkId != 0 && !_marks.Any(mark => mark.Id == _activeMarkId))
            CloseMarkPreview();
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

    internal bool ScrollByWheelDelta(int delta)
    {
        if (!_scrollBar.IsEnabled || delta == 0)
            return false;

        _wheelDelta += delta;
        var wheelSteps = (int)(_wheelDelta / 120);
        if (wheelSteps == 0)
            return true;

        _wheelDelta -= wheelSteps * 120;
        var current = _pendingScrolls.Count > 0 ? _pendingScrolls.Last().Target : _viewTop;
        QueueScroll(current - wheelSteps * 3);
        return true;
    }

    internal void DismissMarkPreview() => CloseMarkPreview();

    private int QueueScroll(double requestedOffset)
    {
        var maximum = Math.Max(0, _bufferHeight - _viewportHeight);
        var target = Math.Clamp((int)Math.Round(requestedOffset), 0, maximum);
        var currentTarget = _pendingScrolls.Count > 0 ? _pendingScrolls.Last().Target : _viewTop;
        if (target == currentTarget)
            return -1;
        _nextCorrelationId = _nextCorrelationId == int.MaxValue ? 0 : _nextCorrelationId + 1;
        var correlationId = _nextCorrelationId;
        _pendingScrolls.Enqueue((correlationId, target));
        _updatingFromTerminal = true;
        try
        {
            _scrollBar.Value = target;
        }
        finally
        {
            _updatingFromTerminal = false;
        }
        ScrollRequested?.Invoke(target);
        return correlationId;
    }

    private void OnScrollBarScroll(object sender, ScrollEventArgs e)
    {
        if (_updatingFromTerminal)
            return;
        QueueScroll(e.NewValue);
    }
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        if (point.X > GutterWidth || !TryGetRow(point.Y, out var row, out var rowTolerance))
            return;

        if (TryFindNearestMark(row, rowTolerance, out var nearest))
        {
            ShowMarkPreview(nearest);
        }
        else
        {
            CloseMarkPreview();
            QueueScroll(row);
        }
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        if (point.X > GutterWidth
            || !TryGetRow(point.Y, out var row, out var rowTolerance)
            || !TryFindNearestMark(row, rowTolerance, out var nearest))
        {
            ScheduleCloseMarkPreview();
            return;
        }

        ShowMarkPreview(nearest);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Keep the preview open so its actions remain reachable. A terminal click closes it.
    }

    private bool TryGetRow(double pointerY, out int row, out int rowTolerance)
    {
        var railHeight = ActualHeight - RailInset * 2;
        if (railHeight <= 0 || _bufferHeight <= 0)
        {
            row = 0;
            rowTolerance = 0;
            return false;
        }

        row = Math.Clamp(
            (int)Math.Round((pointerY - RailInset) / railHeight * _bufferHeight),
            0,
            Math.Max(0, _bufferHeight - 1));
        rowTolerance = Math.Max(2, (int)Math.Round(12.0 / railHeight * _bufferHeight));
        return true;
    }

    private bool TryFindNearestMark(
        int row,
        int rowTolerance,
        out NativeTerminalApi.MarkRecord nearest)
    {
        nearest = default;
        var nearestDistance = int.MaxValue;
        foreach (var mark in _marks)
        {
            var distance = Math.Abs(mark.Row - row);
            if (distance < nearestDistance && distance <= rowTolerance)
            {
                nearest = mark;
                nearestDistance = distance;
            }
        }
        return nearest.Id != 0;
    }

    private (string Title, string Metadata, bool CanCopy) DescribeMark(NativeTerminalApi.MarkRecord mark)
    {
        var text = _markLabel?.Invoke(mark.Id);
        var status = mark.Kind == NativeTerminalApi.MarkKind.Bookmark
            ? "Bookmark"
            : mark.ExitCode is { } code
                ? (code == 0 ? "Exit 0 (Success)" : $"Exit {code}")
                : "Command";
        return (
            string.IsNullOrWhiteSpace(text) ? status : text,
            $"{status} · Line {mark.Row + 1}",
            mark.Kind is NativeTerminalApi.MarkKind.ExactCommand or NativeTerminalApi.MarkKind.ApplicationCommand);
    }

    private void ShowMarkPreview(NativeTerminalApi.MarkRecord mark)
    {
        if (_activeMarkId == mark.Id && (_markTip.IsOpen || _pendingMarkTipId == mark.Id))
            return;

        _activeMarkId = mark.Id;
        var railHeight = ActualHeight - RailInset * 2;
        if (railHeight > 0)
            Canvas.SetTop(_markTipAnchor, MarkTop(mark.Row, railHeight));
        var (title, metadata, canCopy) = DescribeMark(mark);
        _markTitle.Text = title;
        _markMetadata.Text = metadata;
        _copyButton.Visibility = canCopy ? Visibility.Visible : Visibility.Collapsed;
        _pendingMarkTipId = mark.Id;

        if (_markTip.IsOpen)
        {
            _markTipClosing = true;
            _markTip.IsOpen = false;
            return;
        }
        if (_markTipClosing)
            return;

        QueueOpenMarkPreview();
    }

    private void QueueOpenMarkPreview()
    {
        var markId = _pendingMarkTipId;
        if (markId == 0 || _markTipClosing)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activeMarkId != markId || _pendingMarkTipId != markId || _markTipClosing)
                return;
            _markTipLayer.UpdateLayout();
            _markTip.Target = _markTipAnchor;
            _pendingMarkTipId = 0;
            _markTip.IsOpen = true;
        });
    }

    private void ScheduleCloseMarkPreview()
    {
        if (!_markTip.IsOpen)
            _activeMarkId = 0;
    }

    private void CloseMarkPreview()
    {
        _pendingMarkTipId = 0;
        _activeMarkId = 0;
        if (_markTip.IsOpen)
        {
            _markTipClosing = true;
            _markTip.IsOpen = false;
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

        foreach (var highlight in _highlightRows)
            AddTick(highlight.Row, lane: 2, highlight.Color, _isSplit ? Math.Max(calmOpacity * 0.7, 0.4) : 0.75, _paintBuckets, railHeight);

        var searchColor = GetThemeColor("AccentFillColorDefaultBrush", Color.FromArgb(255, 242, 204, 96));
        foreach (var row in _searchRows)
            AddTick(row, lane: 1, ToColorRef(searchColor), _isSplit ? Math.Max(calmOpacity, 0.65) : 1, _paintBuckets, railHeight);
    }

    private void AddTick(
        int row,
        int lane,
        uint colorRef,
        double opacity,
        HashSet<(int Lane, int Bucket, uint Color)> buckets,
        double railHeight)
    {
        var bucket = MarkBucket(row, railHeight);
        if (!buckets.Add((lane, bucket, colorRef)))
            return;

        var rectangle = new Rectangle
        {
            Width = GutterWidth,
            Height = TickHeight,
            Fill = new SolidColorBrush(FromColorRef(colorRef)),
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rectangle, 0);
        Canvas.SetTop(rectangle, RailInset + bucket * TickHeight);
        _annotations.Children.Add(rectangle);
    }

    private int MarkBucket(int row, double railHeight) => Math.Clamp(
        (int)Math.Round(row / (double)Math.Max(1, _bufferHeight) * railHeight / TickHeight),
        0,
        Math.Max(0, (int)(railHeight / TickHeight) - 1));

    private double MarkTop(int row, double railHeight) => RailInset + MarkBucket(row, railHeight) * TickHeight;

    private static Brush GetThemeBrush(string key, Brush? fallback = null) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : fallback ?? new SolidColorBrush(Colors.Transparent);
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
