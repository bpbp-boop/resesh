using System.Text;

namespace Resesh.Terminal.Native;

/// <summary>
/// One shared string per grapheme cluster text. The renderer keys its emoji and cluster caches
/// by text and asks for it on every frame a cluster is on screen; looking a span up here instead
/// of building a string keeps those frames free of allocation. Thread-safe: the render threads
/// and the UI thread (link detection) both use it.
/// </summary>
internal static class GraphemeText
{
    // Distinct clusters on screens and in scrollback are few; past this the set starts over,
    // which only costs the next lookups a fresh string each.
    private const int MaxEntries = 4096;

    private static readonly HashSet<string> s_strings = new(StringComparer.Ordinal);
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> s_lookup =
        s_strings.GetAlternateLookup<ReadOnlySpan<char>>();

    public static string Intern(ReadOnlySpan<char> text)
    {
        lock (s_strings)
        {
            if (s_lookup.TryGetValue(text, out var hit))
                return hit;
            if (s_strings.Count >= MaxEntries)
                s_strings.Clear();
            var created = text.ToString();
            s_strings.Add(created);
            return created;
        }
    }

    /// <summary>The text of <paramref name="codepoints"/>, shared.</summary>
    public static string Intern(ReadOnlySpan<uint> codepoints)
    {
        Span<char> chars = stackalloc char[codepoints.Length * 2];
        var length = 0;
        foreach (var codepoint in codepoints)
        {
            if (!Rune.TryCreate(codepoint, out var rune))
                rune = Rune.ReplacementChar;
            length += rune.EncodeToUtf16(chars[length..]);
        }
        return Intern(chars[..length]);
    }

    /// <summary>A single codepoint's text, shared.</summary>
    public static string Intern(uint codepoint) => Intern([codepoint]);
}
