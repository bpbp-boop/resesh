using SharpGen.Runtime;
using Resesh.Terminal.Native;
using Vortice.DirectWrite;

namespace Resesh.Terminal;

/// <summary>
/// The installed font families a terminal can use, for the font pickers. Read from DirectWrite's
/// system collection off the UI thread; the last read is kept until a picker asks again.
/// </summary>
public static class InstalledFonts
{
    private static readonly object s_gate = new();
    private static Task<IReadOnlyList<string>>? s_monospace;

    /// <summary>Monospaced families by their English names (the names the renderer looks up),
    /// sorted. Proportional fonts are left out: the terminal draws on a fixed cell grid, so they
    /// render with gaps. A name typed by hand still works. <paramref name="refresh"/> rescans,
    /// picking up fonts installed since the app started (DirectWrite's system collection is a
    /// snapshot until asked to check for updates).</summary>
    public static Task<IReadOnlyList<string>> MonospaceFamiliesAsync(bool refresh = false)
    {
        lock (s_gate)
        {
            if (s_monospace is null || (refresh && s_monospace.IsCompleted))
            {
                var rescan = s_monospace is not null;
                s_monospace = Task.Run(() => ReadMonospaceFamilies(checkForUpdates: rescan));
            }
            return s_monospace;
        }
    }

    private static IReadOnlyList<string> ReadMonospaceFamilies(bool checkForUpdates)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var factory = DWrite.DWriteCreateFactory<IDWriteFactory2>(FactoryType.Shared);
            // The factory is shared, so an updated collection reaches the renderer too.
            using var collection = factory.GetSystemFontCollection(checkForUpdates);
            for (uint i = 0; i < collection.FontFamilyCount; i++)
            {
                try
                {
                    using var family = collection.GetFontFamily(i);
                    using var font = family.GetFirstMatchingFont(FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);
                    // Symbol fonts (Wingdings, Marlett) can be fixed-width but draw no text, and a
                    // font without Latin letters can't show a prompt.
                    if (!font.IsSymbolFont && font.HasCharacter('A') && IsMonospaced(font))
                        names.Add(SystemFontFallback.NameOf(family));
                }
                catch (SharpGenException)
                {
                    // A damaged or half-installed font; leave it out of the list.
                }
            }
        }
        catch (SharpGenException)
        {
            // No DirectWrite: the pickers fall back to typing a name.
        }
        return names.ToList();
    }

    /// <summary>Narrow, wide and punctuation glyphs whose advances differ in any proportional font.</summary>
    private static readonly uint[] s_probe = ['i', 'W', 'm', '.', '0', ' '];

    /// <summary>The font's monospaced flag, or failing that its measured advances. The flag
    /// alone misses real terminal fonts: Geist Mono, for one, leaves it unset.</summary>
    private static bool IsMonospaced(IDWriteFont font)
    {
        using (var font1 = font.QueryInterfaceOrNull<IDWriteFont1>())
        {
            if (font1 is { } withFlag && withFlag.IsMonospacedFont)
                return true;
        }
        using var face = font.CreateFontFace();
        var glyphs = face.GetGlyphIndices(s_probe);
        if (glyphs.Any(glyph => glyph == 0))
            return false;
        var metrics = face.GetDesignGlyphMetrics(glyphs, false);
        return metrics.All(m => m.AdvanceWidth == metrics[0].AdvanceWidth);
    }
}
