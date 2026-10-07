using System.Text;
using System.Text.RegularExpressions;

namespace Resesh.Terminal.Ghostty;

/// <summary>
/// Keyword highlighting for the ghostty surface (terminal.html's addon-highlight.js): each
/// rule's regex runs over a row's text, and matched cells take the rule color; "bold" adds a
/// translucent tint of that color behind the text and "underline" underlines it. Later rules
/// paint over earlier ones, at most 40 matches per row. Rows are matched as the renderer reads
/// them, so only what is on screen is ever scanned.
/// </summary>
internal sealed class GhosttyHighlighter
{
    private const int MaxMatchesPerRow = 40;
    private const double TintAlpha = 0.22;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    internal sealed record Rule(string Id, Regex Pattern, uint Color, bool Tint, bool Underline, bool ShowInOverview);

    private IReadOnlyList<Rule> _rules = [];
    private readonly StringBuilder _text = new();
    private readonly List<int> _starts = [];
    private readonly List<int> _ends = [];

    public IReadOnlyList<Rule> Rules => _rules;
    public bool HasRules => _rules.Count > 0;

    /// <summary>Replaces the rule set from the host payload (anonymous objects with pattern,
    /// color, bold, underline, matchCase, showInOverview). Patterns this engine rejects are
    /// skipped, as the page skips patterns its RegExp rejects.</summary>
    public void SetRules(IReadOnlyList<object>? payload)
    {
        var rules = new List<Rule>();
        foreach (var item in payload ?? [])
        {
            if (item is null)
                continue;
            var pattern = Read<string>(item, "pattern");
            if (string.IsNullOrEmpty(pattern) || ParseColor(Read<string>(item, "color") ?? "#ffffff") is not { } color)
                continue;
            if (Compile(pattern, Read<bool?>(item, "matchCase") ?? false) is not { } regex)
                continue;
            rules.Add(new Rule(Read<string>(item, "id") ?? pattern, regex, color, Read<bool?>(item, "bold") ?? false,
                Read<bool?>(item, "underline") ?? false, Read<bool?>(item, "showInOverview") ?? false));
        }
        _rules = rules;
    }

    private static T? Read<T>(object item, string name)
    {
        var property = item.GetType().GetProperty(name)
            ?? item.GetType().GetProperty(char.ToUpperInvariant(name[0]) + name[1..]);
        return property?.GetValue(item) is T value ? value : default;
    }

    /// <summary>Output is untrusted, so prefer the non-backtracking engine; patterns it cannot
    /// run (lookarounds, backreferences) fall back to the backtracking engine with a timeout.</summary>
    internal static Regex? Compile(string pattern, bool matchCase)
    {
        var options = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(pattern, options | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
        }
        catch (ArgumentException)
        {
            return null;
        }
        try
        {
            return new Regex(pattern, options, MatchTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static uint? ParseColor(string color) =>
        color.Length == 7 && color[0] == '#' && uint.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? rgb
            : null;

    /// <summary>Text of a row with a column map: every UTF-16 code unit knows the cells it
    /// covers, so wide characters and grapheme clusters map to their full cells. Trailing
    /// blanks are trimmed, as xterm's translateToString(true) does.</summary>
    internal string RowText(ReadOnlySpan<GhosttyCell> row, Func<int, string?>? grapheme)
    {
        _text.Clear();
        _starts.Clear();
        _ends.Clear();
        for (var x = 0; x < row.Length; x++)
        {
            ref readonly var cell = ref row[x];
            if (cell.Wide == GhosttyCell.SpacerTail)
                continue;
            var width = cell.Wide == GhosttyCell.WideChar ? 2 : 1;
            var chars = cell.Codepoint == 0 ? " "
                : cell.GraphemeLength > 1 ? grapheme?.Invoke(x) ?? char.ConvertFromUtf32((int)cell.Codepoint)
                : char.ConvertFromUtf32((int)cell.Codepoint);
            foreach (var c in chars)
            {
                _text.Append(c);
                _starts.Add(x);
                _ends.Add(x + width);
            }
        }
        var length = _text.Length;
        while (length > 0 && char.IsWhiteSpace(_text[length - 1]))
            length--;
        _text.Length = length;
        return _text.ToString();
    }

    /// <summary>Applies every rule to one freshly read row (never to an already highlighted
    /// row: the tint blends with the cell's current background).</summary>
    public void ApplyRow(Span<GhosttyCell> row, Func<int, string?>? grapheme)
    {
        if (_rules.Count == 0)
            return;
        var text = RowText(row, grapheme);
        if (text.Length == 0)
            return;
        var budget = MaxMatchesPerRow;
        foreach (var rule in _rules)
        {
            if (budget == 0)
                break;
            Match match;
            try
            {
                match = rule.Pattern.Match(text);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }
            for (; match.Success && budget > 0; match = NextMatch(match))
            {
                if (match.Length == 0)
                    continue;
                var start = _starts[match.Index];
                var end = _ends[match.Index + match.Length - 1];
                for (var x = start; x < end && x < row.Length; x++)
                {
                    ref var cell = ref row[x];
                    cell.Foreground = rule.Color;
                    if (rule.Tint)
                    {
                        cell.Background = Blend(cell.Background, rule.Color, TintAlpha);
                        cell.Flags &= unchecked((ushort)~GhosttyCell.DefaultBackground);
                    }
                    if (rule.Underline)
                        cell.Flags |= GhosttyCell.Underline;
                }
                budget--;
            }
        }
    }

    private static Match NextMatch(Match match)
    {
        try
        {
            return match.NextMatch();
        }
        catch (RegexMatchTimeoutException)
        {
            return Match.Empty;
        }
    }

    /// <summary>Lines whose text matches an overview rule, as a bitmask of rule indexes (the
    /// ruler's highlight lane). 0 when nothing matches.</summary>
    public uint OverviewMask(string text)
    {
        uint mask = 0;
        for (var i = 0; i < _rules.Count && i < 32; i++)
        {
            var rule = _rules[i];
            if (!rule.ShowInOverview)
                continue;
            try
            {
                if (rule.Pattern.IsMatch(text))
                    mask |= 1u << i;
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }
        return mask;
    }

    public bool HasOverviewRules => _rules.Any(r => r.ShowInOverview);

    internal static uint Blend(uint background, uint color, double alpha)
    {
        static uint Channel(uint b, uint c, double a) => (uint)Math.Round(b + (c - (double)b) * a);
        return (Channel((background >> 16) & 0xFF, (color >> 16) & 0xFF, alpha) << 16)
            | (Channel((background >> 8) & 0xFF, (color >> 8) & 0xFF, alpha) << 8)
            | Channel(background & 0xFF, color & 0xFF, alpha);
    }
}
