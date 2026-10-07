using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Resesh.Terminal.Ghostty;

/// <summary>
/// The annotation lane beside the scroll bar: one tick per command mark (colored by exit
/// status) and per bookmark, positioned by absolute line over the whole scrollback. Clicking
/// near a tick jumps to its command; hovering shows a card with the command and Jump / Copy
/// output actions (terminal.html's ruler popover).
/// </summary>
internal sealed class GhosttyRuler : Grid
{
    internal const double LaneWidth = 6;
    private const double TickHeight = 3;
    private const double SnapPixels = 8;

    private readonly Canvas _ticks = new() { IsHitTestVisible = false };
    private readonly Border _card = new();
    private readonly TextBlock _cardCommand = new();
    private readonly TextBlock _cardMeta = new();
    private readonly Ellipse _cardDot = new() { Width = 7, Height = 7 };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideTimer;
    private IReadOnlyList<CommandMarkInfo> _marks = [];
    private IReadOnlyList<int> _bookmarks = [];
    private long _total = 1;
    private CommandMarkInfo? _cardMark;
    private Color _ok, _fail, _unknown, _bookmark;

    internal event Action<long>? JumpRequested;
    internal event Action<long>? CopyRequested;

    /// <summary>The card lives in the terminal area (it is wider than the lane); the surface
    /// adds it to its own grid.</summary>
    internal FrameworkElement Card => _card;

    internal GhosttyRuler()
    {
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(250);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => _card.Visibility = Visibility.Collapsed;
        Width = LaneWidth;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // hit-testable lane
        Children.Add(_ticks);
        PointerMoved += (_, e) => ShowCardNear(e.GetCurrentPoint(this).Position.Y);
        PointerExited += (_, _) => _hideTimer.Start();
        PointerPressed += (_, e) =>
        {
            if (Nearest(e.GetCurrentPoint(this).Position.Y) is { } mark)
            {
                JumpRequested?.Invoke(mark.Id);
                e.Handled = true;
            }
        };
        SizeChanged += (_, _) => Paint();
        BuildCard();
        SetTheme(dark: true);
    }

    internal void SetTheme(bool dark)
    {
        // terminal.html's DARK_RULER / LIGHT_RULER command and bookmark colors.
        _ok = dark ? Rgb(0x2EA043) : Rgb(0x50A14F);
        _fail = dark ? Rgb(0xFF5555) : Rgb(0xE45649);
        _unknown = dark ? Rgb(0x9E9E9E) : Rgb(0x6A6A6A);
        _bookmark = dark ? Rgb(0x61D6D6) : Rgb(0x0997B3);
        _card.Background = new SolidColorBrush(dark ? Rgb(0x1E1E2B) : Rgb(0xF7F7FA));
        _card.BorderBrush = new SolidColorBrush(dark ? Rgb(0x3A3A50) : Rgb(0xC8C8D2));
        _cardCommand.Foreground = new SolidColorBrush(dark ? Rgb(0xE6EDF3) : Rgb(0x24242B));
        _cardMeta.Foreground = new SolidColorBrush(dark ? Rgb(0xA7A7B5) : Rgb(0x666675));
        Paint();
    }

    internal void Update(IReadOnlyList<CommandMarkInfo> marks, IReadOnlyList<int> bookmarks, long totalLines)
    {
        _marks = marks;
        _bookmarks = bookmarks;
        _total = Math.Max(1, totalLines);
        Paint();
    }

    private double YForLine(int line) => line / (double)_total * ActualHeight;

    private void Paint()
    {
        _ticks.Children.Clear();
        if (ActualHeight <= 0)
            return;
        var used = new HashSet<(int Bucket, int Lane)>();
        void Tick(int line, Color color, int lane)
        {
            var bucket = (int)(YForLine(line) / TickHeight);
            if (!used.Add((bucket, lane)))
                return; // same pixel row already painted; later marks win nothing
            var rect = new Rectangle
            {
                Width = lane == 0 ? LaneWidth : LaneWidth / 2,
                Height = TickHeight - 1,
                Fill = new SolidColorBrush(color),
            };
            Canvas.SetLeft(rect, lane == 0 ? 0 : LaneWidth / 2);
            Canvas.SetTop(rect, Math.Min(ActualHeight - TickHeight, bucket * TickHeight));
            _ticks.Children.Add(rect);
        }
        foreach (var line in _bookmarks)
            Tick(line, _bookmark, 1);
        foreach (var mark in _marks)
            Tick(mark.Line, mark.Exit switch { 0 => _ok, not null => _fail, null => _unknown }, 0);
    }

    private CommandMarkInfo? Nearest(double y)
    {
        CommandMarkInfo? best = null;
        var bestDistance = SnapPixels;
        foreach (var mark in _marks)
        {
            var distance = Math.Abs(YForLine(mark.Line) - y);
            if (distance <= bestDistance)
            {
                best = mark;
                bestDistance = distance;
            }
        }
        return best;
    }

    private void ShowCardNear(double y)
    {
        if (Nearest(y) is not { } mark)
        {
            _hideTimer.Start();
            return;
        }
        _hideTimer.Stop();
        _cardMark = mark;
        _cardCommand.Text = string.IsNullOrWhiteSpace(mark.Text) ? $"Line {mark.Line + 1}" : mark.Text;
        var status = mark.Exit switch { 0 => "succeeded", { } code => $"exit {code}", null => "status unknown" };
        _cardMeta.Text = $"{DateTimeOffset.FromUnixTimeMilliseconds(mark.UnixMs).ToLocalTime():HH:mm:ss} · {status}"
            + (mark.Exact ? "" : " · detected");
        _cardDot.Fill = new SolidColorBrush(mark.Exit switch { 0 => _ok, not null => _fail, null => _unknown });
        _card.Margin = new Thickness(0, Math.Max(0, YForLine(mark.Line) - 24), 6, 0);
        _card.Visibility = Visibility.Visible;
    }

    private void BuildCard()
    {
        _card.Visibility = Visibility.Collapsed;
        _card.HorizontalAlignment = HorizontalAlignment.Right;
        _card.VerticalAlignment = VerticalAlignment.Top;
        _card.MaxWidth = 460;
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = new CornerRadius(6);
        _card.Padding = new Thickness(10, 6, 6, 6);
        _card.PointerEntered += (_, _) => _hideTimer.Stop();
        _card.PointerExited += (_, _) => _hideTimer.Start();

        _cardCommand.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _cardCommand.FontSize = 12;
        _cardCommand.TextTrimming = TextTrimming.CharacterEllipsis;
        _cardMeta.FontSize = 11;
        _cardMeta.Margin = new Thickness(0, 2, 0, 0);
        _cardDot.Margin = new Thickness(0, 5, 8, 0);
        _cardDot.VerticalAlignment = VerticalAlignment.Top;

        var text = new StackPanel();
        text.Children.Add(_cardCommand);
        text.Children.Add(_cardMeta);
        Button Action(string label, Action<long> run)
        {
            var button = new Button
            {
                Content = label,
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Click += (_, _) =>
            {
                if (_cardMark is { } mark)
                    run(mark.Id);
                _card.Visibility = Visibility.Collapsed;
            };
            return button;
        }
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_cardDot);
        SetColumn(text, 1);
        row.Children.Add(text);
        var jump = Action("Jump to", id => JumpRequested?.Invoke(id));
        SetColumn(jump, 2);
        row.Children.Add(jump);
        var copy = Action("Copy output", id => CopyRequested?.Invoke(id));
        SetColumn(copy, 3);
        row.Children.Add(copy);
        _card.Child = row;
    }

    private static Color Rgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
