namespace Resesh.Terminal.Native;

/// <summary>
/// Geometry for the characters the terminal draws itself instead of taking from the font. Font
/// outlines only meet the cell edges at sizes where the scaled metrics land on whole pixels, which
/// leaves seams between cells at every other size. Coverage, proportions and rounding follow
/// Ghostty's sprite font (src/font/sprite/draw at the commit in eng/ghostty-vt.json); Windows
/// Terminal's BuiltinGlyphs and xterm.js's customGlyphs cover the same core ranges.
/// </summary>
internal static class BuiltinGlyphs
{
    /// <summary>A filled rectangle in whole pixels relative to the cell's top left; right and
    /// bottom are exclusive.</summary>
    public readonly record struct Box(int Left, int Top, int Right, int Bottom);

    /// <summary>The most boxes any glyph needs (a double-line cross, a full braille cell).</summary>
    public const int MaxBoxes = 8;

    /// <summary>Box drawing, block elements, braille, Powerline separators and sextants.</summary>
    public static bool Covers(uint codepoint) => codepoint is (>= 0x2500 and <= 0x259F)
        or (>= 0x2800 and <= 0x28FF) or (>= 0xE0B0 and <= 0xE0BF) or (>= 0x1FB00 and <= 0x1FB3B);

    /// <summary>Arcs, diagonals and Powerline shapes are curves or slopes, which the renderer
    /// draws as paths; everything else is boxes.</summary>
    public static bool IsVector(uint codepoint) => codepoint is (>= 0x256D and <= 0x2573) or (>= 0xE0B0 and <= 0xE0BF);

    /// <summary>Coverage of the shade characters; 1 for everything else.</summary>
    public static float ShadeOf(uint codepoint) => codepoint switch
    {
        0x2591 => 0.25f,
        0x2592 => 0.5f,
        0x2593 => 0.75f,
        _ => 1f,
    };

    // Line style toward each edge, as "up right down left": - none, l light, h heavy, d double.
    // Dashes, arcs and diagonals are drawn separately and left as "----".
    private const string Lines =
        /* 2500 */ "-l-l" + "-h-h" + "l-l-" + "h-h-" + "----" + "----" + "----" + "----" +
        /* 2508 */ "----" + "----" + "----" + "----" + "-ll-" + "-hl-" + "-lh-" + "-hh-" +
        /* 2510 */ "--ll" + "--lh" + "--hl" + "--hh" + "ll--" + "lh--" + "hl--" + "hh--" +
        /* 2518 */ "l--l" + "l--h" + "h--l" + "h--h" + "lll-" + "lhl-" + "hll-" + "llh-" +
        /* 2520 */ "hlh-" + "hhl-" + "lhh-" + "hhh-" + "l-ll" + "l-lh" + "h-ll" + "l-hl" +
        /* 2528 */ "h-hl" + "h-lh" + "l-hh" + "h-hh" + "-lll" + "-llh" + "-hll" + "-hlh" +
        /* 2530 */ "-lhl" + "-lhh" + "-hhl" + "-hhh" + "ll-l" + "ll-h" + "lh-l" + "lh-h" +
        /* 2538 */ "hl-l" + "hl-h" + "hh-l" + "hh-h" + "llll" + "lllh" + "lhll" + "lhlh" +
        /* 2540 */ "hlll" + "llhl" + "hlhl" + "hllh" + "hhll" + "llhh" + "lhhl" + "hhlh" +
        /* 2548 */ "lhhh" + "hlhh" + "hhhl" + "hhhh" + "----" + "----" + "----" + "----" +
        /* 2550 */ "-d-d" + "d-d-" + "-dl-" + "-ld-" + "-dd-" + "--ld" + "--dl" + "--dd" +
        /* 2558 */ "ld--" + "dl--" + "dd--" + "l--d" + "d--l" + "d--d" + "ldl-" + "dld-" +
        /* 2560 */ "ddd-" + "l-ld" + "d-dl" + "d-dd" + "-dld" + "-ldl" + "-ddd" + "ld-d" +
        /* 2568 */ "dl-l" + "dd-d" + "ldld" + "dldl" + "dddd" + "----" + "----" + "----" +
        /* 2570 */ "----" + "----" + "----" + "----" + "---l" + "l---" + "-l--" + "--l-" +
        /* 2578 */ "---h" + "h---" + "-h--" + "--h-" + "-h-l" + "l-h-" + "-l-h" + "h-l-";

    /// <summary>Fills <paramref name="boxes"/> for a non-vector glyph and returns how many were
    /// written. <paramref name="light"/> is the light line thickness in pixels; heavy is twice it.</summary>
    public static int Boxes(uint codepoint, int width, int height, int light, Span<Box> boxes)
    {
        var n = 0;
        switch (codepoint)
        {
            case 0x2504: DashHorizontal(3, light, Math.Max(4, light), width, height, light, boxes, ref n); break;
            case 0x2505: DashHorizontal(3, 2 * light, Math.Max(4, light), width, height, light, boxes, ref n); break;
            case 0x2506: DashVertical(3, light, Math.Max(4, light), width, height, light, boxes, ref n); break;
            case 0x2507: DashVertical(3, 2 * light, Math.Max(4, light), width, height, light, boxes, ref n); break;
            case 0x2508: DashHorizontal(4, light, Math.Max(4, light), width, height, light, boxes, ref n); break;
            case 0x2509: DashHorizontal(4, 2 * light, Math.Max(4, light), width, height, light, boxes, ref n); break;
            case 0x250A: DashVertical(4, light, Math.Max(4, light), width, height, light, boxes, ref n); break;
            case 0x250B: DashVertical(4, 2 * light, Math.Max(4, light), width, height, light, boxes, ref n); break;
            case 0x254C: DashHorizontal(2, light, light, width, height, light, boxes, ref n); break;
            case 0x254D: DashHorizontal(2, 2 * light, 2 * light, width, height, light, boxes, ref n); break;
            case 0x254E: DashVertical(2, light, 2 * light, width, height, light, boxes, ref n); break;
            case 0x254F: DashVertical(2, 2 * light, 2 * light, width, height, light, boxes, ref n); break;
            case >= 0x2500 and <= 0x257F:
                LinesChar(Lines.AsSpan((int)(codepoint - 0x2500) * 4, 4), width, height, light, boxes, ref n);
                break;

            case 0x2580: // upper half
                Add(boxes, ref n, 0, 0, width, Round(height / 2.0));
                break;
            case >= 0x2581 and <= 0x2588: // lower one eighth .. full block
                Add(boxes, ref n, 0, height - Round(height * (codepoint - 0x2580) / 8.0), width, height);
                break;
            case >= 0x2589 and <= 0x258F: // left seven eighths .. left one eighth
                Add(boxes, ref n, 0, 0, Round(width * (0x2590 - codepoint) / 8.0), height);
                break;
            case 0x2590: // right half
                Add(boxes, ref n, width - Round(width / 2.0), 0, width, height);
                break;
            case >= 0x2591 and <= 0x2593: // shades: the whole cell at ShadeOf coverage
                Add(boxes, ref n, 0, 0, width, height);
                break;
            case 0x2594: // upper one eighth
                Add(boxes, ref n, 0, 0, width, Round(height / 8.0));
                break;
            case 0x2595: // right one eighth
                Add(boxes, ref n, width - Round(width / 8.0), 0, width, height);
                break;
            case >= 0x2596 and <= 0x259F:
            {
                // Quadrants: bit 0 upper left, 1 upper right, 2 lower left, 3 lower right.
                ReadOnlySpan<byte> quadrants = [4, 8, 1, 13, 9, 7, 11, 2, 6, 14];
                var mask = quadrants[(int)(codepoint - 0x2596)];
                if ((mask & 1) != 0) Fill(boxes, ref n, width, height, 0, 0.5, 0, 0.5);
                if ((mask & 2) != 0) Fill(boxes, ref n, width, height, 0.5, 1, 0, 0.5);
                if ((mask & 4) != 0) Fill(boxes, ref n, width, height, 0, 0.5, 0.5, 1);
                if ((mask & 8) != 0) Fill(boxes, ref n, width, height, 0.5, 1, 0.5, 1);
                break;
            }

            case >= 0x2800 and <= 0x28FF:
                Braille(codepoint, width, height, boxes, ref n);
                break;

            case >= 0x1FB00 and <= 0x1FB3B:
            {
                // Sextants skip the empty, left-half, right-half and full patterns, which are
                // other characters. Bits: upper left, upper right, middle left, ... lower right.
                var index = codepoint - 0x1FB00;
                var mask = index + index / 0x14 + 1;
                const double third = 1.0 / 3, twoThirds = 2.0 / 3;
                if ((mask & 1) != 0) Fill(boxes, ref n, width, height, 0, 0.5, 0, third);
                if ((mask & 2) != 0) Fill(boxes, ref n, width, height, 0.5, 1, 0, third);
                if ((mask & 4) != 0) Fill(boxes, ref n, width, height, 0, 0.5, third, twoThirds);
                if ((mask & 8) != 0) Fill(boxes, ref n, width, height, 0.5, 1, third, twoThirds);
                if ((mask & 16) != 0) Fill(boxes, ref n, width, height, 0, 0.5, twoThirds, 1);
                if ((mask & 32) != 0) Fill(boxes, ref n, width, height, 0.5, 1, twoThirds, 1);
                break;
            }
        }
        return n;
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static void Add(Span<Box> boxes, ref int n, int left, int top, int right, int bottom)
    {
        if (right > left && bottom > top)
            boxes[n++] = new Box(left, top, right, bottom);
    }

    /// <summary>Fills between two fractions of the cell on each axis. The near edge rounds as the
    /// complement of the far one, so the two halves (or three thirds) of an odd-sized cell come
    /// out the same size and overlap by a pixel rather than leaving one short.</summary>
    private static void Fill(Span<Box> boxes, ref int n, int width, int height, double x0, double x1, double y0, double y1) =>
        Add(boxes, ref n, width - Round((1 - x0) * width), height - Round((1 - y0) * height), Round(x1 * width), Round(y1 * height));

    /// <summary>Lines from the cell edges to the center, each light, heavy or double, with the
    /// joins worked out so every combination meets cleanly.</summary>
    private static void LinesChar(ReadOnlySpan<char> spec, int width, int height, int light, Span<Box> boxes, ref int n)
    {
        const char None = '-', Light = 'l', Heavy = 'h', Double = 'd';
        char up = spec[0], right = spec[1], down = spec[2], left = spec[3];
        var heavy = 2 * light;

        // Horizontal strokes: top and bottom of a light one, a heavy one, and the outer edges of
        // a doubled one (whose inner edges are the light stroke's).
        int hLightTop = Math.Max(0, height - light) / 2, hLightBottom = hLightTop + light;
        int hHeavyTop = Math.Max(0, height - heavy) / 2, hHeavyBottom = hHeavyTop + heavy;
        int hDoubleTop = Math.Max(0, hLightTop - light), hDoubleBottom = hLightBottom + light;
        // Vertical strokes likewise.
        int vLightLeft = Math.Max(0, width - light) / 2, vLightRight = vLightLeft + light;
        int vHeavyLeft = Math.Max(0, width - heavy) / 2, vHeavyRight = vHeavyLeft + heavy;
        int vDoubleLeft = Math.Max(0, vLightLeft - light), vDoubleRight = vLightRight + light;

        // Where each line stops at the center depends on what crosses it.
        var upBottom = left == Heavy || right == Heavy ? hHeavyBottom
            : left != right || down == up ? (left == Double || right == Double ? hDoubleBottom : hLightBottom)
            : left == None && right == None ? hLightBottom
            : hLightTop;
        var downTop = left == Heavy || right == Heavy ? hHeavyTop
            : left != right || up == down ? (left == Double || right == Double ? hDoubleTop : hLightTop)
            : left == None && right == None ? hLightTop
            : hLightBottom;
        var leftRight = up == Heavy || down == Heavy ? vHeavyRight
            : up != down || left == right ? (up == Double || down == Double ? vDoubleRight : vLightRight)
            : up == None && down == None ? vLightRight
            : vLightLeft;
        var rightLeft = up == Heavy || down == Heavy ? vHeavyLeft
            : up != down || right == left ? (up == Double || down == Double ? vDoubleLeft : vLightLeft)
            : up == None && down == None ? vLightLeft
            : vLightRight;

        switch (up)
        {
            case Light: Add(boxes, ref n, vLightLeft, 0, vLightRight, upBottom); break;
            case Heavy: Add(boxes, ref n, vHeavyLeft, 0, vHeavyRight, upBottom); break;
            case Double:
                Add(boxes, ref n, vDoubleLeft, 0, vLightLeft, left == Double ? hLightTop : upBottom);
                Add(boxes, ref n, vLightRight, 0, vDoubleRight, right == Double ? hLightTop : upBottom);
                break;
        }
        switch (right)
        {
            case Light: Add(boxes, ref n, rightLeft, hLightTop, width, hLightBottom); break;
            case Heavy: Add(boxes, ref n, rightLeft, hHeavyTop, width, hHeavyBottom); break;
            case Double:
                Add(boxes, ref n, up == Double ? vLightRight : rightLeft, hDoubleTop, width, hLightTop);
                Add(boxes, ref n, down == Double ? vLightRight : rightLeft, hLightBottom, width, hDoubleBottom);
                break;
        }
        switch (down)
        {
            case Light: Add(boxes, ref n, vLightLeft, downTop, vLightRight, height); break;
            case Heavy: Add(boxes, ref n, vHeavyLeft, downTop, vHeavyRight, height); break;
            case Double:
                Add(boxes, ref n, vDoubleLeft, left == Double ? hLightBottom : downTop, vLightLeft, height);
                Add(boxes, ref n, vLightRight, right == Double ? hLightBottom : downTop, vDoubleRight, height);
                break;
        }
        switch (left)
        {
            case Light: Add(boxes, ref n, 0, hLightTop, leftRight, hLightBottom); break;
            case Heavy: Add(boxes, ref n, 0, hHeavyTop, leftRight, hHeavyBottom); break;
            case Double:
                Add(boxes, ref n, 0, hDoubleTop, up == Double ? vLightLeft : leftRight, hLightTop);
                Add(boxes, ref n, 0, hLightBottom, down == Double ? vLightLeft : leftRight, hDoubleBottom);
                break;
        }
    }

    /// <summary>Dashes with half a gap at each side, so a row of them tiles into one even line.</summary>
    private static void DashHorizontal(int count, int thickness, int desiredGap, int width, int height, int light,
        Span<Box> boxes, ref int n)
    {
        // Without a pixel for every dash and gap, a solid line is the closest thing.
        if (width < 2 * count)
        {
            var middle = Math.Max(0, height - light) / 2;
            Add(boxes, ref n, 0, middle, width, middle + light);
            return;
        }
        // Gaps never take more than half the cell, or the dashes look wrong.
        var gap = Math.Min(desiredGap, width / (2 * count));
        var dash = (width - count * gap) / count;
        var extra = (width - count * gap) % count;
        var y = Math.Max(0, height - thickness) / 2;
        var x = gap / 2;
        for (var i = 0; i < count; i++)
        {
            // Left-over pixels go to the dashes, where they show less than in the gaps.
            var end = x + dash + (extra-- > 0 ? 1 : 0);
            Add(boxes, ref n, x, y, end, y + thickness);
            x = end + gap;
        }
    }

    /// <summary>Dashes from the top with one whole gap at the bottom: vertical dashes join solid
    /// lines above them better that way, and centering matters less than it does across.</summary>
    private static void DashVertical(int count, int thickness, int desiredGap, int width, int height, int light,
        Span<Box> boxes, ref int n)
    {
        if (height < 2 * count)
        {
            var middle = Math.Max(0, width - light) / 2;
            Add(boxes, ref n, middle, 0, middle + light, height);
            return;
        }
        var gap = Math.Min(desiredGap, height / (2 * count));
        var dash = (height - count * gap) / count;
        var extra = (height - count * gap) % count;
        var x = Math.Max(0, width - thickness) / 2;
        var y = 0;
        for (var i = 0; i < count; i++)
        {
            var end = y + dash + (extra-- > 0 ? 1 : 0);
            Add(boxes, ref n, x, y, x + thickness, end);
            y = end + gap;
        }
    }

    /// <summary>Square braille dots on a 2x4 grid. The codepoint's low byte is the pattern: bits
    /// 0-2 the left column's upper three dots, 3-5 the right column's, 6 and 7 the bottom row.</summary>
    private static void Braille(uint codepoint, int width, int height, Span<Box> boxes, ref int n)
    {
        var dot = Math.Min(width / 4, height / 8);
        int xSpacing = width / 4, ySpacing = height / 8;
        int xMargin = xSpacing / 2, yMargin = ySpacing / 2;
        var xLeft = width - 2 * xMargin - xSpacing - 2 * dot;
        var yLeft = height - 2 * yMargin - 3 * ySpacing - 4 * dot;

        // Spend the left-over pixels in order: a visible dot, then margins, spacing, more margin,
        // and last a bigger dot.
        if (xLeft >= 2 && yLeft >= 4 && dot == 0) { dot++; xLeft -= 2; yLeft -= 4; }
        if (xLeft >= 2 && xMargin == 0) { xMargin = 1; xLeft -= 2; }
        if (yLeft >= 2 && yMargin == 0) { yMargin = 1; yLeft -= 2; }
        if (xLeft >= 1) { xSpacing++; xLeft--; }
        if (yLeft >= 3) { ySpacing++; yLeft -= 3; }
        if (xLeft >= 2) { xMargin++; xLeft -= 2; }
        if (yLeft >= 2) { yMargin++; yLeft -= 2; }
        if (xLeft >= 2 && yLeft >= 4) dot++;

        ReadOnlySpan<byte> bits = [0, 1, 2, 6, 3, 4, 5, 7]; // left column top to bottom, then right
        for (var i = 0; i < 8; i++)
        {
            if ((codepoint & (1u << bits[i])) == 0)
                continue;
            var x = xMargin + i / 4 * (dot + xSpacing);
            var y = yMargin + i % 4 * (dot + ySpacing);
            Add(boxes, ref n, x, y, x + dot, y + dot);
        }
    }
}
