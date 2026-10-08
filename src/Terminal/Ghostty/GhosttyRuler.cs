using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Resesh.Terminal.Ghostty;

/// <summary>
/// The annotation lane beside the scroll bar, positioned by absolute line over the whole
/// scrollback: command marks (colored by exit status) on the left half, keyword-highlight and
/// find-match ticks on the right half (find on top, the current match in amber), bookmarks
/// across the full width. Clicking near a command tick jumps to it; hovering shows a card with
/// the command and Jump / Copy output actions (terminal.html's ruler popover). Ticks are painted
/// into one bitmap: the lane repaints with every batch of output, too often for an element per tick.
/// </summary>
internal sealed class GhosttyRuler : Grid
{
    internal const double LaneWidth = 8;
    private const double TickHeight = 3;
    private const double SnapPixels = 8;

    private readonly Image _ticks = new() { IsHitTestVisible = false, Stretch = Stretch.Fill };
    private WriteableBitmap? _bitmap;
    private uint[] _pixels = [];
    private uint[] _shown = [];
    private readonly Border _card = new();
    private readonly TextBlock _cardCommand = new();
    private readonly TextBlock _cardMeta = new();
    private readonly Ellipse _cardDot = new() { Width = 7, Height = 7 };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideTimer;
    private IReadOnlyList<CommandMarkInfo> _marks = [];
    private IReadOnlyList<int> _bookmarks = [];
    private IReadOnlyList<(int Line, uint Color)> _highlights = [];
    private IReadOnlyList<int> _searchLines = [];
    private int _currentSearchLine = -1;
    private Color _match, _activeMatch;
    private long _total = 1;
    private CommandMarkInfo? _cardMark;
    private Color _ok, _fail, _unknown, _bookmark;
    private uint _laneBackground = 0x0C0C0C, _laneBorder = 0x333333;
    private bool _split, _groupFocused = true, _pointerOver;

    internal event Action<long>? JumpRequested;
    internal event Action<long>? CopyRequested;
    /// <summary>Scroll to a line: (line, true when snapped to a bookmark, match or highlight).</summary>
    internal event Action<int, bool>? LineRequested;

    /// <summary>Line text and arrival time for the card away from command marks.</summary>
    internal Func<int, string?>? LineText { get; set; }
    internal Func<int, long?>? LineTime { get; set; }

    private readonly List<Button> _cardActions = [];

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
        PointerEntered += (_, _) => SetPointerOver(true);
        PointerExited += (_, _) =>
        {
            _hideTimer.Start();
            SetPointerOver(false);
        };
        PointerPressed += (_, e) =>
        {
            var y = e.GetCurrentPoint(this).Position.Y;
            e.Handled = true;
            if (Nearest(y) is { } mark)
                JumpRequested?.Invoke(mark.Id);
            else if (Snap(y) is { } snapped)
                LineRequested?.Invoke(snapped, true);
            else if (ActualHeight > 0)
                LineRequested?.Invoke(LineForY(y), false);
        };
        SizeChanged += (_, _) => Paint();
        BuildCard();
        SetTheme(dark: true, 0x0C0C0C, 0x333333);
    }

    /// <summary>terminal.html's getRuler: the command, bookmark and find palette by lightness,
    /// the lane background from the theme and its edge from the selection color.</summary>
    internal void SetTheme(bool dark, uint background, uint border)
    {
        _laneBackground = background;
        _laneBorder = border;
        // terminal.html's DARK_RULER / LIGHT_RULER command and bookmark colors.
        _ok = dark ? Rgb(0x2EA043) : Rgb(0x50A14F);
        _fail = dark ? Rgb(0xFF5555) : Rgb(0xE45649);
        _unknown = dark ? Rgb(0x9E9E9E) : Rgb(0x6A6A6A);
        _bookmark = dark ? Rgb(0x61D6D6) : Rgb(0x0997B3);
        _match = dark ? Rgb(0x7F8EA3) : Rgb(0x5A6B85);
        _activeMatch = dark ? Rgb(0xF2CC60) : Rgb(0xC18401);
        _card.Background = new SolidColorBrush(dark ? Rgb(0x1E1E2B) : Rgb(0xF7F7FA));
        _card.BorderBrush = new SolidColorBrush(dark ? Rgb(0x3A3A50) : Rgb(0xC8C8D2));
        _cardCommand.Foreground = new SolidColorBrush(dark ? Rgb(0xE6EDF3) : Rgb(0x24242B));
        _cardMeta.Foreground = new SolidColorBrush(dark ? Rgb(0xA7A7B5) : Rgb(0x666675));
        Paint();
    }

    /// <summary>Calm presentation in split view: routine marks recede (more so in an unfocused
    /// group), failures and bookmarks stay strong; hovering the lane restores full detail.</summary>
    internal void SetPresentation(bool isSplit, bool isGroupFocused)
    {
        if (isSplit == _split && isGroupFocused == _groupFocused)
            return;
        _split = isSplit;
        _groupFocused = isGroupFocused;
        Paint();
    }

    private void SetPointerOver(bool over)
    {
        if (over == _pointerOver)
            return;
        _pointerOver = over;
        if (_split)
            Paint();
    }

    internal void Update(IReadOnlyList<CommandMarkInfo> marks, IReadOnlyList<int> bookmarks, long totalLines,
        IReadOnlyList<(int Line, uint Color)> highlights, IReadOnlyList<int> searchLines, int currentSearchLine)
    {
        _marks = marks;
        _bookmarks = bookmarks;
        _highlights = highlights;
        _searchLines = searchLines;
        _currentSearchLine = currentSearchLine;
        _total = Math.Max(1, totalLines);
        Paint();
    }

    private double YForLine(int line) => line / (double)_total * ActualHeight;

    private int LineForY(double y) =>
        (int)Math.Clamp(Math.Floor(y / Math.Max(1, ActualHeight) * _total), 0, Math.Max(0, _total - 1));

    /// <summary>The nearest bookmark, find match or highlight line within snapping distance.</summary>
    private int? Snap(double y)
    {
        int? best = null;
        var bestDistance = SnapPixels;
        void Consider(int line)
        {
            var distance = Math.Abs(YForLine(line) - y);
            if (distance <= bestDistance)
            {
                best = line;
                bestDistance = distance;
            }
        }
        foreach (var line in _bookmarks) Consider(line);
        foreach (var line in _searchLines) Consider(line);
        foreach (var (line, _) in _highlights) Consider(line);
        return best;
    }

    private static string Age(long unixMs)
    {
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        return age.TotalSeconds < 60 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours}h ago"
            : $"{(int)age.TotalDays}d ago";
    }

    private static string Clock(long unixMs) =>
        $"{DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime():HH:mm:ss} ({Age(unixMs)})";

    private void Paint()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Max(2, (int)Math.Round(LaneWidth * scale));
        var height = (int)Math.Round(ActualHeight * scale);
        if (height <= 0)
        {
            _ticks.Source = _bitmap = null;
            return;
        }
        var fresh = false;
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height);
            _pixels = new uint[width * height];
            _shown = new uint[width * height];
            _ticks.Source = _bitmap;
            fresh = true;
        }
        // Opaque BGRA: the lane background with a one-pixel edge toward the terminal.
        var background = 0xFF000000 | _laneBackground;
        Array.Fill(_pixels, background);
        var edge = Math.Max(1, (int)Math.Round(scale));
        for (var y = 0; y < height; y++)
            _pixels.AsSpan(y * width, Math.Min(edge, width)).Fill(0xFF000000 | _laneBorder);

        var calm = _split && !_pointerOver;
        var ordinary = calm ? (_groupFocused ? 0.42 : 0.25) : 1;
        var important = calm ? (_groupFocused ? 0.90 : 0.62) : 1;
        var search = calm ? (_groupFocused ? 0.75 : 0.50) : 1;

        // Lanes: 0 commands (left half), 1 highlights and 2 find (right half), 3 bookmarks
        // (full width). Ticks in the same pixel row and lane are painted once unless forced;
        // lanes painted later sit on top.
        var used = new HashSet<(int Bucket, int Lane)>();
        void Tick(int line, Color color, int lane, double opacity = 1, bool force = false)
        {
            var bucket = (int)(YForLine(line) / TickHeight);
            if (!used.Add((bucket, lane)) && !force)
                return;
            var top = Math.Max(0, Math.Min(ActualHeight - TickHeight, bucket * TickHeight));
            var y0 = (int)Math.Round(top * scale);
            var y1 = Math.Min(height, Math.Max(y0 + 1, (int)Math.Round((top + TickHeight - 1) * scale)));
            var x0 = lane is 1 or 2 ? width / 2 : 0;
            var x1 = lane == 0 ? width / 2 : width;
            x0 = Math.Max(x0, edge);
            var pixel = Over(color, opacity, background);
            for (var y = y0; y < y1; y++)
                _pixels.AsSpan(y * width + x0, Math.Max(0, x1 - x0)).Fill(pixel);
        }
        foreach (var (line, color) in _highlights)
            Tick(line, Rgb(color), 1, opacity: 0.8 * ordinary); // slightly dim: find ticks stay dominant
        foreach (var line in _searchLines)
            if (line != _currentSearchLine)
                Tick(line, _match, 2, search);
        if (_currentSearchLine >= 0)
            Tick(_currentSearchLine, _activeMatch, 2, important, force: true);
        // Failures paint in a second pass so an overlapping success never buries a red tick;
        // calm mode keeps routine successes neutral.
        foreach (var mark in _marks)
            if (mark.Exit is null or 0)
                Tick(mark.Line, mark.Exit == 0 && !calm ? _ok : _unknown, 0, ordinary);
        foreach (var mark in _marks)
            if (mark.Exit is not (null or 0))
                Tick(mark.Line, _fail, 0, important, force: true);
        foreach (var line in _bookmarks)
            Tick(line, _bookmark, 3, important, force: true);

        if (!fresh && _pixels.AsSpan().SequenceEqual(_shown))
            return;
        _pixels.CopyTo(_shown, 0);
        using (var stream = _bitmap.PixelBuffer.AsStream())
            stream.Write(MemoryMarshal.AsBytes(_pixels.AsSpan()));
        _bitmap.Invalidate();
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

    /// <summary>Near a command mark: the command card (Jump to / Copy output). Elsewhere: the
    /// line under the pointer, when it arrived, and what the lane shows around it.</summary>
    private void ShowCardNear(double y)
    {
        if (ActualHeight <= 0 || _total <= 1)
        {
            _hideTimer.Start();
            return;
        }
        _hideTimer.Stop();
        if (Nearest(y) is { } mark)
        {
            _cardMark = mark;
            _cardCommand.Text = string.IsNullOrWhiteSpace(mark.Text) ? $"Line {mark.Line + 1}" : mark.Text;
            var status = mark.Exit switch { 0 => "succeeded", { } code => $"exit {code}", null => "status unknown" };
            _cardMeta.Text = $"{Clock(LineTime?.Invoke(mark.Line) ?? mark.UnixMs)} · {status}" + (mark.Exact ? "" : " · detected");
            _cardDot.Fill = new SolidColorBrush(mark.Exit switch { 0 => _ok, not null => _fail, null => _unknown });
            _cardDot.Visibility = Visibility.Visible;
            foreach (var action in _cardActions)
                action.Visibility = Visibility.Visible;
            _card.Margin = new Thickness(0, Math.Max(0, YForLine(mark.Line) - 24), 6, 0);
            _card.Visibility = Visibility.Visible;
            return;
        }

        _cardMark = null;
        var line = LineForY(y);
        var sample = "";
        for (var l = line; l < Math.Min(_total, line + 8) && sample.Length == 0; l++)
            sample = (LineText?.Invoke(l) ?? "").Trim();
        _cardCommand.Text = sample.Length > 0 ? sample : "(blank)";
        var parts = new List<string> { $"Line {line + 1}" };
        if (LineTime?.Invoke(line) is { } time)
            parts.Add(Clock(time));
        // What the lane shows within this pixel band.
        var lo = LineForY(y - TickHeight);
        var hi = LineForY(y + TickHeight);
        var matches = _searchLines.Count(l => l >= lo && l <= hi);
        var bookmarks = _bookmarks.Count(l => l >= lo && l <= hi);
        var highlights = _highlights.Count(h => h.Line >= lo && h.Line <= hi);
        if (matches > 0) parts.Add(matches == 1 ? "1 match" : $"{matches} matches");
        if (bookmarks > 0) parts.Add(bookmarks == 1 ? "bookmark" : $"{bookmarks} bookmarks");
        if (highlights > 0) parts.Add(highlights == 1 ? "1 highlight" : $"{highlights} highlights");
        _cardMeta.Text = string.Join(" · ", parts);
        _cardDot.Visibility = Visibility.Collapsed;
        foreach (var action in _cardActions)
            action.Visibility = Visibility.Collapsed;
        _card.Margin = new Thickness(0, Math.Max(0, y - 24), 6, 0);
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
            _cardActions.Add(button);
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

    private static uint Over(Color color, double alpha, uint background)
    {
        static uint Mix(uint c, uint b, double a) => (uint)Math.Round(c * a + b * (1 - a));
        return 0xFF000000
            | Mix(color.R, (background >> 16) & 0xFF, alpha) << 16
            | Mix(color.G, (background >> 8) & 0xFF, alpha) << 8
            | Mix(color.B, background & 0xFF, alpha);
    }

    private static Color Rgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
