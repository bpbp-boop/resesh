using System.Text;
using System.Text.RegularExpressions;

namespace Resesh.Terminal.Ghostty;

/// <summary>
/// Keyword highlighting for the ghostty surface (terminal.html's addon-highlight.js): each
/// rule's regex runs over a row's text, and matched cells take the rule color; "bold" adds a
/// translucent tint of that color behind the text and "underline" underlines it. Later rules
/// paint over earlier ones, at most 40 matches per row. Rows are matched as the renderer reads
/// them, so only what is on screen is ever scanned, and matches are cached by row text: output
/// scrolling past re-reads the same rows at new positions every frame.
/// </summary>
internal sealed class GhosttyHighlighter
{
    private const int MaxMatchesPerRow = 40;
    private const int MaxCachedRows = 4096;
    private const double TintAlpha = 0.22;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    internal sealed record Rule(string Id, Regex Pattern, uint Color, bool Tint, bool Underline, bool ShowInOverview);

    private IReadOnlyList<Rule> _rules = [];
    private readonly StringBuilder _text = new();
    private readonly List<int> _starts = [];
    private readonly List<int> _ends = [];
    private readonly Dictionary<string, (int Index, int Length, int Rule)[]> _matches = new(StringComparer.Ordinal);

    public IReadOnlyList<Rule> Rules => _rules;

    /// <summary>The column map of the last <see cref="RowText"/>: the first cell and the cell
    /// after the last that each UTF-16 code unit covers.</summary>
    internal IReadOnlyList<int> ColumnStarts => _starts;
    internal IReadOnlyList<int> ColumnEnds => _ends;
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
        _matches.Clear();
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
            if (cell.GraphemeLength > 1 && grapheme?.Invoke(x) is { } cluster)
            {
                foreach (var c in cluster)
                    Append(c, x, width);
            }
            else if (cell.Codepoint == 0)
            {
                Append(' ', x, width);
            }
            else if (cell.Codepoint < 0x10000)
            {
                Append((char)cell.Codepoint, x, width);
            }
            else if (cell.Codepoint <= 0x10FFFF)
            {
                var v = cell.Codepoint - 0x10000;
                Append((char)(0xD800 + (v >> 10)), x, width);
                Append((char)(0xDC00 + (v & 0x3FF)), x, width);
            }
            else
            {
                Append('\uFFFD', x, width);
            }
        }
        var length = _text.Length;
        while (length > 0 && char.IsWhiteSpace(_text[length - 1]))
            length--;
        _text.Length = length;
        return _text.ToString();
    }

    private void Append(char c, int x, int width)
    {
        _text.Append(c);
        _starts.Add(x);
        _ends.Add(x + width);
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
        if (!_matches.TryGetValue(text, out var matches))
        {
            matches = FindMatches(text);
            if (_matches.Count >= MaxCachedRows)
                _matches.Clear();
            _matches[text] = matches;
        }
        foreach (var (index, length, ruleIndex) in matches)
        {
            var rule = _rules[ruleIndex];
            var start = _starts[index];
            var end = _ends[index + length - 1];
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
        }
    }

    /// <summary>Every rule's matches in a row's text, in paint order, at most 40.</summary>
    private (int Index, int Length, int Rule)[] FindMatches(string text)
    {
        List<(int, int, int)>? found = null;
        for (var i = 0; i < _rules.Count && (found?.Count ?? 0) < MaxMatchesPerRow; i++)
        {
            Match match;
            try
            {
                match = _rules[i].Pattern.Match(text);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }
            for (; match.Success && (found?.Count ?? 0) < MaxMatchesPerRow; match = NextMatch(match))
            {
                if (match.Length > 0)
                    (found ??= []).Add((match.Index, match.Length, i));
            }
        }
        return found is null ? [] : [.. found];
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
