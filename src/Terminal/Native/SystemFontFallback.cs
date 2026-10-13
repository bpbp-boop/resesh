using System.Globalization;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.DirectWrite;

namespace Resesh.Terminal.Native;

/// <summary>
/// Windows' own font fallback, for characters the terminal's font chain has no glyph for. The
/// chain is a short fixed list; this covers every other script an installed font can draw.
/// </summary>
internal static class SystemFontFallback
{
    /// <summary>The installed family DirectWrite would draw <paramref name="codepoint"/> with
    /// next to <paramref name="baseFamily"/>, or null when no installed font has it (private-use
    /// icons without their font, unassigned codepoints). The caller disposes the family.</summary>
    public static IDWriteFontFamily? Map(IDWriteFactory2 factory, uint codepoint, string baseFamily)
    {
        if (codepoint > 0x10FFFF || codepoint is >= 0xD800 and <= 0xDFFF)
            return null;
        try
        {
            using var fallback = factory.SystemFontFallback;
            using var collection = factory.GetSystemFontCollection(false);
            using var source = new Source(char.ConvertFromUtf32((int)codepoint));
            fallback.MapCharacters(source, 0, source.Length, collection, baseFamily, FontWeight.Normal,
                FontStyle.Normal, FontStretch.Normal, 0, out var font, out _);
            if (font is null)
                return null;
            using (font)
                return font.HasCharacter(codepoint) ? font.FontFamily : null;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    /// <summary>The family's English name where it has one, so it compares with configured names.</summary>
    public static string NameOf(IDWriteFontFamily family)
    {
        using var names = family.FamilyNames;
        return names.GetString(names.FindLocaleName("en-us", out var index) ? index : 0);
    }

    /// <summary>The one character being mapped, as the text source DirectWrite reads from. The
    /// locale is the user's, which is what picks between the CJK fonts for shared ideographs.</summary>
    private sealed class Source : CallbackBase, IDWriteTextAnalysisSource
    {
        private readonly IntPtr _text;
        private readonly IntPtr _locale;

        public uint Length { get; }

        public Source(string text)
        {
            _text = Marshal.StringToHGlobalUni(text);
            _locale = Marshal.StringToHGlobalUni(CultureInfo.CurrentUICulture.Name);
            Length = (uint)text.Length;
        }

        public uint GetTextAtPosition(uint textPosition, IntPtr textString)
        {
            var inside = textPosition < Length;
            Marshal.WriteIntPtr(textString, inside ? _text + (int)textPosition * sizeof(char) : IntPtr.Zero);
            return inside ? Length - textPosition : 0;
        }

        public uint GetTextBeforePosition(uint textPosition, IntPtr textString)
        {
            var inside = textPosition > 0 && textPosition <= Length;
            Marshal.WriteIntPtr(textString, inside ? _text : IntPtr.Zero);
            return inside ? textPosition : 0;
        }

        public ReadingDirection GetParagraphReadingDirection() => ReadingDirection.LeftToRight;

        public uint GetLocaleName(uint textPosition, IntPtr localeName)
        {
            Marshal.WriteIntPtr(localeName, _locale);
            return textPosition < Length ? Length - textPosition : 0;
        }

        public void GetNumberSubstitution(uint textPosition, out uint textLength, out IDWriteNumberSubstitution numberSubstitution)
        {
            textLength = textPosition < Length ? Length - textPosition : 0;
            numberSubstitution = null!;
        }

        protected override void DisposeCore(bool disposing)
        {
            Marshal.FreeHGlobal(_text);
            Marshal.FreeHGlobal(_locale);
            base.DisposeCore(disposing);
        }
    }
}
