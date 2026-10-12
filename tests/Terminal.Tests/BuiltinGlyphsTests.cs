using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

/// <summary>Built-in glyph geometry: adjacent cells have to meet exactly at every cell size,
/// which is the whole reason these characters are not taken from the font.</summary>
public class BuiltinGlyphsTests
{
    /// <summary>Cell sizes across the zoom range, odd and even, with the line thicknesses the
    /// renderer allows at each (a fifth of the cell width at most).</summary>
    public static IEnumerable<object[]> Sizes()
    {
        for (var width = 5; width <= 22; width++)
            for (var height = width + 3; height <= 2 * width + 9; height += 3)
                for (var light = 1; light <= Math.Max(1, width / 5); light++)
                    yield return [width, height, light];
    }

    private static bool[,] Paint(uint codepoint, int width, int height, int light)
    {
        Span<BuiltinGlyphs.Box> boxes = stackalloc BuiltinGlyphs.Box[BuiltinGlyphs.MaxBoxes];
        var count = BuiltinGlyphs.Boxes(codepoint, width, height, light, boxes);
        var pixels = new bool[width, height];
        foreach (var box in boxes[..count])
        {
            Assert.True(box.Left >= 0 && box.Top >= 0 && box.Right <= width && box.Bottom <= height,
                $"U+{codepoint:X4} leaves a {width}x{height} cell: {box}");
            for (var x = box.Left; x < box.Right; x++)
                for (var y = box.Top; y < box.Bottom; y++)
                    pixels[x, y] = true;
        }
        return pixels;
    }

    private static string Column(bool[,] pixels, int x) =>
        string.Concat(Enumerable.Range(0, pixels.GetLength(1)).Select(y => pixels[x, y] ? '#' : '.'));

    private static string Row(bool[,] pixels, int y) =>
        string.Concat(Enumerable.Range(0, pixels.GetLength(0)).Select(x => pixels[x, y] ? '#' : '.'));

    private static bool Full(bool[,] a, bool[,]? b = null)
    {
        for (var x = 0; x < a.GetLength(0); x++)
            for (var y = 0; y < a.GetLength(1); y++)
                if (!a[x, y] && b?[x, y] != true)
                    return false;
        return true;
    }

    private static IEnumerable<uint> BoxGlyphs() =>
        Enumerable.Range(0x2500, 0x80).Select(c => (uint)c)
            .Concat(Enumerable.Range(0x2580, 0x20).Select(c => (uint)c))
            .Concat(Enumerable.Range(0x2800, 0x100).Select(c => (uint)c))
            .Concat(Enumerable.Range(0x1FB00, 0x3C).Select(c => (uint)c))
            .Where(c => !BuiltinGlyphs.IsVector(c));

    [Theory, MemberData(nameof(Sizes))]
    public void EveryGlyphStaysInsideItsCell(int width, int height, int light)
    {
        foreach (var codepoint in BoxGlyphs())
            Paint(codepoint, width, height, light);
    }

    [Theory, MemberData(nameof(Sizes))]
    public void BlocksTileWithoutSeams(int width, int height, int light)
    {
        Assert.True(Full(Paint('█', width, height, light)));
        Assert.True(Full(Paint('▀', width, height, light), Paint('▄', width, height, light)));
        Assert.True(Full(Paint('▌', width, height, light), Paint('▐', width, height, light)));
        Assert.True(Full(Paint('▛', width, height, light), Paint('▗', width, height, light)));
        Assert.True(Full(Paint('▜', width, height, light), Paint('▖', width, height, light)));
        Assert.True(Full(Paint('▚', width, height, light), Paint('▞', width, height, light)));
        // Partial blocks reach the edges they are named for.
        var lower = Paint('▂', width, height, light);
        Assert.Equal(new string('#', width), Row(lower, height - 1));
        Assert.Equal(new string('.', width), Row(lower, 0));
        var left = Paint('▎', width, height, light);
        Assert.Equal(new string('#', height), Column(left, 0));
        Assert.Equal(new string('.', height), Column(left, width - 1));
    }

    [Theory, MemberData(nameof(Sizes))]
    public void BoxLinesMeetTheirNeighborsAtEveryEdge(int width, int height, int light)
    {
        // What a light, heavy or double line looks like where it crosses a cell edge.
        string[] across = ["─", "━", "═"], down = ["│", "┃", "║"];
        var horizontal = across.Select(s => Column(Paint(s[0], width, height, light), 0)).Append(new string('.', height)).ToHashSet();
        var vertical = down.Select(s => Row(Paint(s[0], width, height, light), 0)).Append(new string('.', width)).ToHashSet();
        foreach (var codepoint in Enumerable.Range(0x2500, 0x80).Select(c => (uint)c))
        {
            // Dashes are gapped on purpose; arcs and diagonals are paths.
            if (BuiltinGlyphs.IsVector(codepoint) || codepoint is (>= 0x2504 and <= 0x250B) or (>= 0x254C and <= 0x254F))
                continue;
            var pixels = Paint(codepoint, width, height, light);
            Assert.Contains(Column(pixels, 0), horizontal);
            Assert.Contains(Column(pixels, width - 1), horizontal);
            Assert.Contains(Row(pixels, 0), vertical);
            Assert.Contains(Row(pixels, height - 1), vertical);
        }
    }

    [Theory, MemberData(nameof(Sizes))]
    public void BoxLinesGoWhereTheCharacterPoints(int width, int height, int light)
    {
        string blankColumn = new('.', height), blankRow = new('.', width);
        var line = Paint('─', width, height, light);
        var bar = Paint('│', width, height, light);
        Assert.Equal(Column(line, 0), Column(line, width - 1));
        Assert.Equal(Row(bar, 0), Row(bar, height - 1));
        Assert.NotEqual(blankColumn, Column(line, 0));
        Assert.NotEqual(blankRow, Row(bar, 0));

        var corner = Paint('┌', width, height, light); // right and down only
        Assert.Equal(Column(line, 0), Column(corner, width - 1));
        Assert.Equal(Row(bar, 0), Row(corner, height - 1));
        Assert.Equal(blankColumn, Column(corner, 0));
        Assert.Equal(blankRow, Row(corner, 0));

        var tee = Paint('┫', width, height, light); // heavy up, down and left
        var heavyLine = Paint('━', width, height, light);
        var heavyBar = Paint('┃', width, height, light);
        Assert.Equal(Column(heavyLine, 0), Column(tee, 0));
        Assert.Equal(Row(heavyBar, 0), Row(tee, 0));
        Assert.Equal(Row(heavyBar, 0), Row(tee, height - 1));
        Assert.Equal(blankColumn, Column(tee, width - 1));

        var mixed = Paint('╘', width, height, light); // light up, double right
        Assert.Equal(Column(Paint('═', width, height, light), 0), Column(mixed, width - 1));
        Assert.Equal(Row(bar, 0), Row(mixed, 0));
        Assert.Equal(blankColumn, Column(mixed, 0));
        Assert.Equal(blankRow, Row(mixed, height - 1));
    }

    [Theory, MemberData(nameof(Sizes))]
    public void DashesKeepTheirCountAndTheLinesPosition(int width, int height, int light)
    {
        static int Runs(string s) => s.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;
        var middle = Array.IndexOf(Column(Paint('─', width, height, light), 0).ToCharArray(), '#');
        var center = Array.IndexOf(Row(Paint('│', width, height, light), 0).ToCharArray(), '#');
        (char Glyph, int Count)[] horizontal = [('╌', 2), ('┄', 3), ('┈', 4)], vertical = [('╎', 2), ('┆', 3), ('┊', 4)];
        foreach (var (glyph, count) in horizontal)
            if (width >= 2 * count)
                Assert.Equal(count, Runs(Row(Paint(glyph, width, height, light), middle)));
        foreach (var (glyph, count) in vertical)
            Assert.Equal(count, Runs(Column(Paint(glyph, width, height, light), center)));
    }

    [Theory, MemberData(nameof(Sizes))]
    public void BrailleDotsFollowTheCodepointBits(int width, int height, int light)
    {
        Span<BuiltinGlyphs.Box> boxes = stackalloc BuiltinGlyphs.Box[BuiltinGlyphs.MaxBoxes];
        Assert.Equal(0, BuiltinGlyphs.Boxes(0x2800, width, height, light, boxes));
        Assert.Equal(8, BuiltinGlyphs.Boxes(0x28FF, width, height, light, boxes));
        var all = boxes.ToArray();
        // Two columns of four rows, the same in every column and row.
        Assert.Equal(2, all.Select(b => b.Left).Distinct().Count());
        Assert.Equal(4, all.Select(b => b.Top).Distinct().Count());

        Assert.Equal(1, BuiltinGlyphs.Boxes(0x2801, width, height, light, boxes)); // dot 1: top left
        Assert.Equal((all.Min(b => b.Left), all.Min(b => b.Top)), (boxes[0].Left, boxes[0].Top));
        Assert.Equal(1, BuiltinGlyphs.Boxes(0x2880, width, height, light, boxes)); // dot 8: bottom right
        Assert.Equal((all.Max(b => b.Left), all.Max(b => b.Top)), (boxes[0].Left, boxes[0].Top));
        Assert.Equal(1, BuiltinGlyphs.Boxes(0x2808, width, height, light, boxes)); // dot 4: top right
        Assert.Equal((all.Max(b => b.Left), all.Min(b => b.Top)), (boxes[0].Left, boxes[0].Top));
        Assert.Equal(1, BuiltinGlyphs.Boxes(0x2840, width, height, light, boxes)); // dot 7: bottom left
        Assert.Equal((all.Min(b => b.Left), all.Max(b => b.Top)), (boxes[0].Left, boxes[0].Top));
    }

    [Theory, MemberData(nameof(Sizes))]
    public void SextantsCoverTheirSixths(int width, int height, int light)
    {
        var first = Paint(0x1FB00, width, height, light); // sextant 1: upper left only
        Assert.True(first[0, 0]);
        Assert.False(first[width - 1, 0]);
        Assert.False(first[0, height - 1]);
        // U+1FB3B is every sextant but the first, so the two fill the cell between them.
        Assert.True(Full(first, Paint(0x1FB3B, width, height, light)));
        Assert.False(Paint(0x1FB3B, width, height, light)[0, 0]);
        // U+1FB13 (sextant 3) and U+1FB14 (sextants 1 and 3) straddle the skipped left-half pattern.
        var third = Paint(0x1FB03, width, height, light); // sextant 3: middle left
        Assert.True(third[0, height / 2]);
        Assert.False(third[0, 0]);
        Assert.False(third[0, height - 1]);
    }

    [Fact]
    public void CoversTheSharedRangesAndNothingAdjacent()
    {
        foreach (var codepoint in new uint[] { 0x2500, 0x257F, 0x2580, 0x259F, 0x2800, 0x28FF, 0xE0B0, 0xE0BF, 0x1FB00, 0x1FB3B })
            Assert.True(BuiltinGlyphs.Covers(codepoint), $"U+{codepoint:X4}");
        foreach (var codepoint in new uint[] { 'a', 0x24FF, 0x25A0, 0x27FF, 0x2900, 0xE0A0, 0xE0AF, 0xE0C0, 0x1FB3C })
            Assert.False(BuiltinGlyphs.Covers(codepoint), $"U+{codepoint:X4}");
        Assert.True(BuiltinGlyphs.IsVector('╭'));
        Assert.True(BuiltinGlyphs.IsVector('╳'));
        Assert.True(BuiltinGlyphs.IsVector(0xE0B0));
        Assert.False(BuiltinGlyphs.IsVector('─'));
        Assert.Equal(0.5f, BuiltinGlyphs.ShadeOf('▒'));
        Assert.Equal(1f, BuiltinGlyphs.ShadeOf('█'));
    }
}
