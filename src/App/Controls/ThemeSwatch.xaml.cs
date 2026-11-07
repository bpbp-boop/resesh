using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Resesh.Core.Models;
using Resesh.Core.Storage;
using Resesh.Terminal;
using Windows.UI;

namespace Resesh.App.Controls;

public sealed partial class ThemeSwatch : UserControl
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(ThemeChoice), typeof(ThemeSwatch), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(ThemeSwatch), new PropertyMetadata(false, Changed));

    /// <summary>The preview's terminal: a prompt in the colors bash gives it (bold green
    /// user@host and bold blue path, which bold draws as bright ANSI 10 and 12), then plain
    /// syslog output. Each part's base color is an ANSI index, or <see cref="Text"/> for the
    /// theme's text color; the highlighting rules the user has on are laid over all of it, as
    /// they are in a real terminal, so the card shows what `cat` output will look like.</summary>
    private const int Text = -1;
    private static readonly (string Text, int Color)[][] SampleLines =
    [
        [("user@host", 10), (":", Text), ("~", 12), ("$ ssh user@server cat /var/log/messages", Text)],
        [("Mar 23 20:11:53 web1 sshd[812]: Failed password", Text)],
        [("Mar 23 20:11:58 web1 systemd[1]: Started nginx", Text)],
        [("Mar 23 20:12:03 web1 kernel: eth0 up 10.0.4.2/24", Text)],
    ];

    /// <summary>The terminal's bold-rule tint (Resesh.Terminal's Highlighter.TintAlpha).</summary>
    private const double TintAlpha = 0.22;

    // The rules' matches don't depend on the theme, so every card shares one painting,
    // redone after the rules change (the store can say so from any thread).
    private static int _rulesVersion;
    private static (int Version, IReadOnlyList<HighlightSpan>[] Spans)? _painted;

    static ThemeSwatch() => App.Highlights.Changed += () => Interlocked.Increment(ref _rulesVersion);

    // Recolored, not replaced, when a recycled container shows another theme.
    private readonly SolidColorBrush _background = new(), _foreground = new(), _cursor = new(), _outline = new();
    private readonly SolidColorBrush[] _ansi = new SolidColorBrush[16];
    private readonly Dictionary<uint, SolidColorBrush> _ruleBrushes = [];
    private readonly List<(SolidColorBrush Brush, uint Color)> _tints = [];
    private readonly TextBlock[] _lines = new TextBlock[SampleLines.Length];
    private int _linesVersion = -1;

    public ThemeSwatch()
    {
        InitializeComponent();
        for (var i = 0; i < _ansi.Length; i++)
            _ansi[i] = new SolidColorBrush();
        Preview.Background = _background;
        Sample.Foreground = _foreground;
        Cursor.Background = _cursor;
        var font = new FontFamily("Cascadia Mono, Consolas");
        for (var i = 0; i < _lines.Length; i++)
        {
            // Clipped, not wrapped, at the card's edge, as a narrow terminal would.
            _lines[i] = new TextBlock { FontFamily = font, FontSize = 9, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.Clip };
            Lines.Children.Add(_lines[i]);
        }
        for (var i = 0; i < 8; i++)
            Dots.Children.Add(new Ellipse { Width = 14, Height = 14, StrokeThickness = 1, Fill = _ansi[i], Stroke = _outline });
    }

    private static IReadOnlyList<HighlightSpan>[] Painted()
    {
        var version = Volatile.Read(ref _rulesVersion);
        if (_painted is { } painted && painted.Version == version)
            return painted.Spans;
        var rules = App.Highlights.ResolveForSession(null);
        var spans = SampleLines.Select(line => HighlightPreview.Paint(string.Concat(line.Select(part => part.Text)), rules)).ToArray();
        _painted = (version, spans);
        return spans;
    }

    /// <summary>Lays the sample lines out as runs: highlighted stretches in their rule's
    /// color, the rest in each part's base color, and bold rules' tints as highlighters.</summary>
    private void BuildLines()
    {
        var painted = Painted();
        _linesVersion = _painted!.Value.Version;
        _tints.Clear();
        for (var i = 0; i < _lines.Length; i++)
        {
            var block = _lines[i];
            block.Inlines.Clear();
            block.TextHighlighters.Clear();
            var text = string.Concat(SampleLines[i].Select(part => part.Text));
            var baseColors = SampleLines[i].SelectMany(part => Enumerable.Repeat(part.Color, part.Text.Length)).ToArray();
            foreach (var span in painted[i])
            {
                if (span.Color is { } ruleColor)
                    block.Inlines.Add(new Run { Text = text.Substring(span.Start, span.Length), Foreground = RuleBrush(ruleColor) });
                else
                {
                    for (var start = span.Start; start < span.Start + span.Length;)
                    {
                        var end = start + 1;
                        while (end < span.Start + span.Length && baseColors[end] == baseColors[start])
                            end++;
                        var color = baseColors[start];
                        block.Inlines.Add(new Run { Text = text[start..end], Foreground = color == Text ? _foreground : _ansi[color] });
                        start = end;
                    }
                }
                if (span.Tint && span.Color is { } tintColor)
                {
                    var tint = new SolidColorBrush();
                    _tints.Add((tint, tintColor));
                    var highlighter = new TextHighlighter { Background = tint, Foreground = RuleBrush(tintColor) };
                    highlighter.Ranges.Add(new TextRange { StartIndex = span.Start, Length = span.Length });
                    block.TextHighlighters.Add(highlighter);
                }
            }
        }
    }

    private SolidColorBrush RuleBrush(uint color)
    {
        if (!_ruleBrushes.TryGetValue(color, out var brush))
            _ruleBrushes[color] = brush = new SolidColorBrush(RgbColor.ToColor(color));
        return brush;
    }

    public ThemeChoice? Theme
    {
        get => (ThemeChoice?)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var swatch = (ThemeSwatch)sender;
        if (args.Property == ThemeProperty)
            swatch.DrawPalette();
        swatch.SelectionFrame.BorderBrush = swatch.IsSelected
            ? (Brush)Application.Current.Resources["SessionAccentBrush"]
            : null;
        swatch.ThemeName.FontWeight = swatch.IsSelected ? FontWeights.Bold : FontWeights.Normal;
    }

    private void DrawPalette()
    {
        if (Theme is not { } theme)
            return;
        var palette = theme.Palette;
        if (_linesVersion != Volatile.Read(ref _rulesVersion))
            BuildLines();
        _background.Color = RgbColor.ToColor(palette.Background);
        _foreground.Color = RgbColor.ToColor(palette.Foreground);
        _cursor.Color = RgbColor.ToColor(palette.Cursor);
        // A faint ring in the text color keeps a dot that matches the background visible.
        _outline.Color = Color.FromArgb(72,
            (byte)(palette.Foreground >> 16), (byte)(palette.Foreground >> 8), (byte)palette.Foreground);
        for (var i = 0; i < _ansi.Length; i++)
            _ansi[i].Color = RgbColor.ToColor(palette.Ansi[i]);
        foreach (var (brush, color) in _tints)
            brush.Color = RgbColor.ToColor(ColorMath.Mix(palette.Background, color, TintAlpha));
        ThemeName.Text = theme.Name;
        AutomationProperties.SetName(this, theme.Name);
    }
}
