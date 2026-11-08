using SharpGen.Runtime;
using Vortice.DirectWrite;

namespace Resesh.Terminal.Native;

/// <summary>
/// Programming-font ligatures (<c>-&gt;</c>, <c>!=</c>, <c>===</c>): runs of two or more ASCII
/// punctuation characters shaped with DirectWrite, then pinned back to the cell grid. That is
/// where these fonts put their ligatures, and the same few runs repeat on every line, so a
/// cache keyed by their text almost always hits; shaping whole words missed on every
/// timestamp and path in a log flood (~2.4 ms a full-screen frame). The price is the rare
/// ligature that involves letters or digits (Fira Code's <c>www</c> and <c>0x</c>). Fonts
/// without ligature features (Consolas) skip shaping altogether; Cascadia Mono has calt, so it
/// is shaped and its runs cache as unchanged.
/// </summary>
internal sealed unsafe partial class TerminalRenderer
{
    private const int MaxShapedWords = 8192;
    private const int MaxShapedLength = 64;

    /// <summary>A word's glyphs on the grid: each cluster's advances add up to its cells.</summary>
    private sealed class ShapedWord(ushort[] glyphs, float[] advances, GlyphOffset[] offsets)
    {
        public readonly ushort[] Glyphs = glyphs;
        public readonly float[] Advances = advances;
        public readonly GlyphOffset[] Offsets = offsets;
    }

    private IDWriteTextAnalyzer? _analyzer;
    private ScriptAnalysis _latin;
    private bool _latinKnown;
    // Per (style, face): whether the face has ligature features, and its shaped words. A null
    // word is one that shapes to the same glyphs as drawing each character alone.
    private readonly Dictionary<(int Style, int Face), bool> _ligatureFaces = [];
    private readonly Dictionary<(int Style, int Face), Dictionary<string, ShapedWord?>> _shapedWords = [];
    private int _shapedCount;
    private GlyphOffset[] _runOffsets = [];

    /// <summary>Drops shaping state: the font, its size or the device changed.</summary>
    private void ResetLigatures()
    {
        _ligatureFaces.Clear();
        _shapedWords.Clear();
        _shapedCount = 0;
        _analyzer?.Dispose();
        _analyzer = null;
        _latinKnown = false;
    }

    private static bool IsPunctuation(uint codepoint) =>
        codepoint is > ' ' and < 0x7F && !(codepoint is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    /// <summary>The shaped word, or null to draw its characters one by one: no ligature
    /// features, nothing to ligate, or shaping failed.</summary>
    private ShapedWord? ShapedWordFor(int style, int face, ReadOnlySpan<char> word)
    {
        if (!HasLigatures(style, face))
            return null;
        if (!_shapedWords.TryGetValue((style, face), out var words))
            _shapedWords[(style, face)] = words = new Dictionary<string, ShapedWord?>(StringComparer.Ordinal);
        var lookup = words.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(word, out var hit))
            return hit;
        if (_shapedCount >= MaxShapedWords)
        {
            foreach (var cache in _shapedWords.Values)
                cache.Clear();
            _shapedCount = 0;
        }
        var text = word.ToString();
        ShapedWord? shaped;
        try
        {
            shaped = Shape(style, face, text);
        }
        catch (SharpGenException)
        {
            shaped = null;
        }
        words[text] = shaped;
        _shapedCount++;
        return shaped;
    }

    private bool HasLigatures(int style, int face)
    {
        if (_ligatureFaces.TryGetValue((style, face), out var has))
            return has;
        has = false;
        try
        {
            if (EnsureLatin())
            {
                using var analyzer2 = _analyzer!.QueryInterfaceOrNull<IDWriteTextAnalyzer2>();
                if (analyzer2 is not null)
                {
                    var tags = new FontFeatureTag[256];
                    analyzer2.GetTypographicFeatures(_faces[style][face], _latin, "en-us", (uint)tags.Length, out var count, tags);
                    for (var i = 0; i < Math.Min(count, (uint)tags.Length); i++)
                        has |= tags[i] is FontFeatureTag.StandardLigatures or FontFeatureTag.ContextualAlternates
                            or FontFeatureTag.ContextualLigatures;
                }
            }
        }
        catch (SharpGenException)
        {
            has = false;
        }
        _ligatureFaces[(style, face)] = has;
        return has;
    }

    /// <summary>The script DirectWrite assigns ASCII text, for shaping words without analyzing
    /// each one.</summary>
    private bool EnsureLatin()
    {
        if (_latinKnown)
            return _analyzer is not null;
        _latinKnown = true;
        _analyzer = s_dwrite!.CreateTextAnalyzer();
        using var source = new ShapingSource("a");
        using var sink = new ScriptSink();
        _analyzer.AnalyzeScript(source, 0, 1, sink);
        if (!sink.Found)
        {
            _analyzer.Dispose();
            _analyzer = null;
            return false;
        }
        _latin = sink.Analysis;
        return true;
    }

    private ShapedWord? Shape(int style, int face, string text)
    {
        var fontFace = _faces[style][face];
        var length = text.Length;
        var maxGlyphs = (uint)(length * 3 / 2 + 16);
        var clusterMap = new ushort[length];
        var textProps = new ShapingTextProperties[length];
        var glyphs = new ushort[maxGlyphs];
        var glyphProps = new ShapingGlyphProperties[maxGlyphs];
        _analyzer!.GetGlyphs(text, (uint)length, fontFace, false, false, _latin, "en-us", null!, null, null!, 0,
            maxGlyphs, clusterMap, textProps, glyphs, glyphProps, out var glyphCount);
        var count = (int)glyphCount;

        // Unchanged: the same glyphs drawing each character alone gives.
        if (count == length)
        {
            var same = true;
            for (var i = 0; i < length && same; i++)
                same = Lookup(text[i], style) == (face, glyphs[i]);
            if (same)
                return null;
        }

        var advances = new float[count];
        var offsets = new GlyphOffset[count];
        _analyzer.GetGlyphPlacements(text, clusterMap, textProps, (uint)length, glyphs, glyphProps, glyphCount, fontFace,
            _emSize, false, false, _latin, "en-us", null, null!, 0, advances, offsets);

        // Pin each cluster to its cells: its glyphs keep their own advances but the last takes
        // up the difference, so every cluster ends exactly on a cell boundary.
        for (var start = 0; start < length;)
        {
            var firstGlyph = clusterMap[start];
            var end = start + 1;
            while (end < length && clusterMap[end] == firstGlyph)
                end++;
            var lastGlyph = end < length ? clusterMap[end] : count;
            if (lastGlyph <= firstGlyph)
                return null; // not a left-to-right mapping; draw plainly
            var width = (end - start) * (float)CellWidth;
            for (var g = firstGlyph; g < lastGlyph - 1; g++)
                width -= advances[g];
            advances[lastGlyph - 1] = width;
            start = end;
        }
        return new ShapedWord(glyphs[..count], advances, offsets);
    }

    /// <summary>The word's text as DirectWrite reads it.</summary>
    private sealed partial class ShapingSource(string text) : CallbackBase, IDWriteTextAnalysisSource
    {
        private readonly IntPtr _text = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(text);
        private readonly IntPtr _locale = System.Runtime.InteropServices.Marshal.StringToHGlobalUni("en-us");
        private readonly uint _length = (uint)text.Length;

        public uint GetTextAtPosition(uint textPosition, IntPtr textString)
        {
            var inside = textPosition < _length;
            System.Runtime.InteropServices.Marshal.WriteIntPtr(textString, inside ? _text + (int)textPosition * sizeof(char) : IntPtr.Zero);
            return inside ? _length - textPosition : 0;
        }

        public uint GetTextBeforePosition(uint textPosition, IntPtr textString)
        {
            var inside = textPosition > 0 && textPosition <= _length;
            System.Runtime.InteropServices.Marshal.WriteIntPtr(textString, inside ? _text : IntPtr.Zero);
            return inside ? textPosition : 0;
        }

        public ReadingDirection GetParagraphReadingDirection() => ReadingDirection.LeftToRight;

        public uint GetLocaleName(uint textPosition, IntPtr localeName)
        {
            System.Runtime.InteropServices.Marshal.WriteIntPtr(localeName, _locale);
            return textPosition < _length ? _length - textPosition : 0;
        }

        public void GetNumberSubstitution(uint textPosition, out uint textLength, out IDWriteNumberSubstitution numberSubstitution)
        {
            textLength = textPosition < _length ? _length - textPosition : 0;
            numberSubstitution = null!;
        }

        protected override void DisposeCore(bool disposing)
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(_text);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(_locale);
            base.DisposeCore(disposing);
        }
    }

    /// <summary>Takes the script AnalyzeScript reports; the other analyses are not asked for.</summary>
    private sealed partial class ScriptSink : CallbackBase, IDWriteTextAnalysisSink
    {
        public bool Found { get; private set; }
        public ScriptAnalysis Analysis { get; private set; }

        public void SetScriptAnalysis(uint textPosition, uint textLength, ScriptAnalysis scriptAnalysis)
        {
            Analysis = scriptAnalysis;
            Found = true;
        }

        public void SetLineBreakpoints(uint textPosition, LineBreakpoint[] lineBreakpoints) { }

        public void SetBidiLevel(uint textPosition, uint textLength, byte explicitLevel, byte resolvedLevel) { }

        public void SetNumberSubstitution(uint textPosition, uint textLength, IDWriteNumberSubstitution numberSubstitution) { }
    }
}
