namespace Resesh.Terminal;

/// <summary>The one reading of a stored <c>#rrggbb</c> colour, shared by the terminal's
/// highlighter and the app (session colour tags, the highlight rule editor) so that a value
/// one accepts the other never rejects. Exactly seven characters: '#' and six ASCII hex
/// digits; no whitespace, sign, alpha or trailing line break.</summary>
public static class HexColor
{
    /// <summary>What a highlight rule with no colour at all paints in.</summary>
    public const string DefaultHighlight = "#ffffff";

    /// <summary>The colour as 0xRRGGBB, or null when it is not exactly <c>#rrggbb</c>.</summary>
    public static uint? Parse(string? text)
    {
        if (text is not { Length: 7 } || text[0] != '#')
            return null;
        uint rgb = 0;
        for (var i = 1; i < 7; i++)
        {
            var digit = text[i];
            if (!char.IsAsciiHexDigit(digit))
                return null;
            rgb = (rgb << 4) | (uint)(digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10);
        }
        return rgb;
    }

    /// <summary>A highlight rule's colour: a missing one is <see cref="DefaultHighlight"/>,
    /// an unreadable one is null and the rule is not applied (the editor marks it).</summary>
    public static uint? ParseHighlight(string? text) => Parse(text ?? DefaultHighlight);
}
