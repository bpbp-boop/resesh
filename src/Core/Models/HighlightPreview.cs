using System.Globalization;
using System.Text.RegularExpressions;

namespace Resesh.Core.Models;

/// <summary>One stretch of a previewed line: drawn in <see cref="Color"/> (0xRRGGBB, or the
/// theme's text color when null), with the rule color tinted behind it when <see cref="Tint"/>.</summary>
public readonly record struct HighlightSpan(int Start, int Length, uint? Color, bool Tint);

/// <summary>
/// Highlighting rules applied to sample text the way the terminal applies them to a row
/// (Resesh.Terminal's Highlighter): each rule's matches take its color, later rules paint over
/// earlier ones, and a bold rule tints the background. For previews of a theme, which show
/// output with the highlighting the user has on.
/// </summary>
public static class HighlightPreview
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary><paramref name="text"/> split into spans covering all of it, in order.</summary>
    public static IReadOnlyList<HighlightSpan> Paint(string text, IEnumerable<HighlightRule> rules)
    {
        var color = new uint?[text.Length];
        var tint = new bool[text.Length];
        foreach (var rule in rules)
        {
            if (!rule.Enabled || ParseColor(rule.Color) is not { } ruleColor || Compile(rule) is not { } regex)
                continue;
            try
            {
                for (var match = regex.Match(text); match.Success; match = match.NextMatch())
                {
                    for (var i = match.Index; i < match.Index + match.Length; i++)
                    {
                        color[i] = ruleColor;
                        tint[i] |= rule.Bold;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        var spans = new List<HighlightSpan>();
        for (var start = 0; start < text.Length;)
        {
            var end = start + 1;
            while (end < text.Length && color[end] == color[start] && tint[end] == tint[start])
                end++;
            spans.Add(new HighlightSpan(start, end - start, color[start], tint[start]));
            start = end;
        }
        return spans;
    }

    private static Regex? Compile(HighlightRule rule)
    {
        var options = RegexOptions.CultureInvariant | (rule.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(rule.Pattern, options, MatchTimeout);
        }
        catch (ArgumentException)
        {
            return null; // the terminal skips a pattern it can't compile too
        }
    }

    /// <summary>As the terminal reads it (Resesh.Terminal.HexColor.ParseHighlight): exactly
    /// #rrggbb, with no color at all painting white.</summary>
    private static uint? ParseColor(string? hex)
    {
        hex ??= "#ffffff";
        return hex.Length == 7 && hex[0] == '#' && !hex.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF")
            ? uint.Parse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : null;
    }
}
