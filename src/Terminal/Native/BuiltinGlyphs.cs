using System.Numerics;

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

    /// <summary>A line stroked at the light box-drawing thickness, in cell coordinates.</summary>
    public readonly record struct Segment(Vector2 From, Vector2 To);

    /// <summary>The most boxes any glyph needs (a checkerboard fill).</summary>
    public const int MaxBoxes = 32;

    /// <summary>The most corners any filled polygon has (a smooth mosaic).</summary>
    public const int MaxPoints = 10;

    /// <summary>The most segments any line glyph needs (a diagonal fill in a wide cell).</summary>
    public const int MaxSegments = 64;

    /// <summary>Box drawing, block elements, braille, Powerline separators, and the parts of
    /// Symbols for Legacy Computing (and its supplement's octants) that Ghostty draws.</summary>
    public static bool Covers(uint codepoint) => codepoint is (>= 0x2500 and <= 0x259F)
        or (>= 0x2800 and <= 0x28FF) or (>= 0xE0B0 and <= 0xE0BF) or (>= 0x1FB00 and <= 0x1FBAF)
        or (>= 0x1FBBD and <= 0x1FBBF) or (>= 0x1FBCE and <= 0x1FBEF) or (>= 0x1CD00 and <= 0x1CDE5);

    /// <summary>Arcs, diagonals, wedges, circles and Powerline shapes are curves or slopes, which
    /// the renderer draws as paths; everything else is boxes.</summary>
    public static bool IsVector(uint codepoint) => codepoint is (>= 0x256D and <= 0x2573) or (>= 0xE0B0 and <= 0xE0BF)
        or (>= 0x1FB3C and <= 0x1FB6F) or (>= 0x1FB98 and <= 0x1FBAE) or (>= 0x1FBBD and <= 0x1FBBF)
        or (>= 0x1FBD0 and <= 0x1FBE3) or (>= 0x1FBE8 and <= 0x1FBEF);

    /// <summary>Coverage of the shade characters; 1 for everything else.</summary>
    public static float ShadeOf(uint codepoint) => codepoint switch
    {
        0x2591 => 0.25f,
        0x2592 => 0.5f,
        0x2593 => 0.75f,
        (>= 0x1FB8C and <= 0x1FB90) or (>= 0x1FB9C and <= 0x1FB9F) => 0.5f, // medium shade halves and corners
        _ => 1f,
    };

    /// <summary>Medium shade with a solid half on top: the renderer fills the cell at half
    /// coverage first, then draws <see cref="Boxes"/> solid.</summary>
    public static bool HasShadedBackdrop(uint codepoint) => codepoint is 0x1FB91 or 0x1FB92 or 0x1FB94;

    /// <summary>A solid cell with <see cref="Segments"/> cut out of it in the background color.</summary>
    public static bool IsInverted(uint codepoint) => codepoint is >= 0x1FBBD and <= 0x1FBBF;

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

            case >= 0x1FB70 and <= 0x1FB75: // vertical one eighth blocks, second column to seventh
            {
                var column = codepoint - 0x1FB70 + 1;
                Fill(boxes, ref n, width, height, column / 8.0, (column + 1) / 8.0, 0, 1);
                break;
            }
            case >= 0x1FB76 and <= 0x1FB7B: // horizontal one eighth blocks, second row to seventh
            {
                var row = codepoint - 0x1FB76 + 1;
                Fill(boxes, ref n, width, height, 0, 1, row / 8.0, (row + 1) / 8.0);
                break;
            }
            case 0x1FB7C: Block(boxes, ref n, width, height, -1, 0, 1 / 8.0, 1); Block(boxes, ref n, width, height, 0, 1, 1, 1 / 8.0); break;
            case 0x1FB7D: Block(boxes, ref n, width, height, -1, 0, 1 / 8.0, 1); Block(boxes, ref n, width, height, 0, -1, 1, 1 / 8.0); break;
            case 0x1FB7E: Block(boxes, ref n, width, height, 1, 0, 1 / 8.0, 1); Block(boxes, ref n, width, height, 0, -1, 1, 1 / 8.0); break;
            case 0x1FB7F: Block(boxes, ref n, width, height, 1, 0, 1 / 8.0, 1); Block(boxes, ref n, width, height, 0, 1, 1, 1 / 8.0); break;
            case 0x1FB80: Block(boxes, ref n, width, height, 0, -1, 1, 1 / 8.0); Block(boxes, ref n, width, height, 0, 1, 1, 1 / 8.0); break;
            case 0x1FB81: // horizontal one eighth blocks 1, 3, 5 and 8
                foreach (var row in (ReadOnlySpan<int>)[0, 2, 4, 7])
                    Fill(boxes, ref n, width, height, 0, 1, row / 8.0, (row + 1) / 8.0);
                break;
            case >= 0x1FB82 and <= 0x1FB86: // upper one quarter .. seven eighths
            {
                ReadOnlySpan<double> sizes = [2 / 8.0, 3 / 8.0, 5 / 8.0, 6 / 8.0, 7 / 8.0];
                Block(boxes, ref n, width, height, 0, -1, 1, sizes[(int)(codepoint - 0x1FB82)]);
                break;
            }
            case >= 0x1FB87 and <= 0x1FB8B: // right one quarter .. seven eighths
            {
                ReadOnlySpan<double> sizes = [2 / 8.0, 3 / 8.0, 5 / 8.0, 6 / 8.0, 7 / 8.0];
                Block(boxes, ref n, width, height, 1, 0, sizes[(int)(codepoint - 0x1FB87)], 1);
                break;
            }
            // Medium shade halves (ShadeOf), and the solid halves over a shaded backdrop.
            case 0x1FB8C: Block(boxes, ref n, width, height, -1, 0, 0.5, 1); break;
            case 0x1FB8D or 0x1FB94: Block(boxes, ref n, width, height, 1, 0, 0.5, 1); break;
            case 0x1FB8E or 0x1FB91: Block(boxes, ref n, width, height, 0, -1, 1, 0.5); break;
            case 0x1FB8F or 0x1FB92: Block(boxes, ref n, width, height, 0, 1, 1, 0.5); break;
            case 0x1FB90: Add(boxes, ref n, 0, 0, width, height); break;
            case 0x1FB95 or 0x1FB96:
                Checkerboard(codepoint == 0x1FB95 ? 0 : 1, width, height, boxes, ref n);
                break;
            case 0x1FB97: // heavy horizontal fill: the second and fourth quarters
                Add(boxes, ref n, 0, height / 4, width, 2 * height / 4);
                Add(boxes, ref n, 0, 3 * height / 4, width, height);
                break;
            case 0x1FBAF: // box drawings light horizontal with vertical stroke
                LinesChar("hlhl", width, height, light, boxes, ref n);
                break;
            case 0x1FBCE: Block(boxes, ref n, width, height, -1, 0, 2 / 3.0, 1); break;
            case 0x1FBCF: Block(boxes, ref n, width, height, -1, 0, 1 / 3.0, 1); break;
            case 0x1FBE4: Block(boxes, ref n, width, height, 0, -1, 0.5, 0.5); break;
            case 0x1FBE5: Block(boxes, ref n, width, height, 0, 1, 0.5, 0.5); break;
            case 0x1FBE6: Block(boxes, ref n, width, height, -1, 0, 0.5, 0.5); break;
            case 0x1FBE7: Block(boxes, ref n, width, height, 1, 0, 0.5, 0.5); break;

            case >= 0x1CD00 and <= 0x1CDE5:
            {
                // Octants: bit 0 is the upper left eighth, then left to right, top to bottom.
                var mask = Octants[(int)(codepoint - 0x1CD00)];
                for (var i = 0; i < 8; i++)
                    if ((mask & (1 << i)) != 0)
                        Fill(boxes, ref n, width, height, i % 2 * 0.5, i % 2 * 0.5 + 0.5, i / 2 * 0.25, i / 2 * 0.25 + 0.25);
                break;
            }
        }
        return n;
    }

    /// <summary>The octant patterns in codepoint order, from the Unicode names ("BLOCK
    /// OCTANT-1235"; Ghostty's octants.txt). Patterns other characters already cover are skipped.</summary>
    private static ReadOnlySpan<byte> Octants =>
    [
        0x04, 0x06, 0x07, 0x08, 0x09, 0x0B, 0x0C, 0x0D, 0x0E, 0x10, 0x11, 0x12, 0x13, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
        0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
        0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A,
        0x4B, 0x4C, 0x4D, 0x4E, 0x4F, 0x51, 0x52, 0x53, 0x54, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C, 0x5D,
        0x5E, 0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E,
        0x6F, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E,
        0x7F, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
        0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F,
        0xA1, 0xA2, 0xA3, 0xA4, 0xA6, 0xA7, 0xA8, 0xA9, 0xAB, 0xAC, 0xAD, 0xAE, 0xB0, 0xB1, 0xB2, 0xB3,
        0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC1, 0xC2, 0xC3, 0xC4,
        0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF, 0xD0, 0xD1, 0xD2, 0xD3, 0xD4,
        0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF, 0xE0, 0xE1, 0xE2, 0xE3, 0xE4,
        0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF, 0xF1, 0xF2, 0xF3, 0xF4, 0xF6,
        0xF7, 0xF8, 0xF9, 0xFB, 0xFD, 0xFE,
    ];

    /// <summary>A block of a fraction of the cell, against an edge or centered on each axis
    /// (-1 left or top, 0 center, 1 right or bottom), sized by rounding like Ghostty's.</summary>
    private static void Block(Span<Box> boxes, ref int n, int width, int height, int horizontal, int vertical,
        double widthFraction, double heightFraction)
    {
        int w = Round(width * widthFraction), h = Round(height * heightFraction);
        var x = horizontal < 0 ? 0 : horizontal > 0 ? width - w : (width - w) / 2;
        var y = vertical < 0 ? 0 : vertical > 0 ? height - h : (height - h) / 2;
        Add(boxes, ref n, x, y, x + w, y + h);
    }

    /// <summary>Four squares across and about as many down as keep them square; capped so the
    /// boxes fit <see cref="MaxBoxes"/> in very tall cells.</summary>
    private static void Checkerboard(int parity, int width, int height, Span<Box> boxes, ref int n)
    {
        const int across = 4;
        var down = Math.Clamp(Round(4.0 * height / Math.Max(1, width)), 1, MaxBoxes * 2 / across);
        for (var x = 0; x < across; x++)
            for (var y = 0; y < down; y++)
                if ((x + y) % 2 == parity)
                    Add(boxes, ref n, width * x / across, height * y / down, width * (x + 1) / across, height * (y + 1) / down);
    }

    /// <summary>Fills <paramref name="points"/> with the corners of a filled vector glyph from
    /// Symbols for Legacy Computing and returns how many; 0 for glyphs that are not polygons.
    /// Polygons may touch themselves at a point (two triangles meeting at the center).</summary>
    public static int Polygon(uint codepoint, float width, float height, Span<Vector2> points)
    {
        Vector2 topLeft = new(0, 0), topRight = new(width, 0), bottomRight = new(width, height), bottomLeft = new(0, height);
        var center = new Vector2(MathF.Round(width / 2, MidpointRounding.AwayFromZero),
            MathF.Round(height / 2, MidpointRounding.AwayFromZero));
        switch (codepoint)
        {
            case >= 0x1FB3C and <= 0x1FB67:
                return Mosaic(Mosaics[(int)(codepoint - 0x1FB3C)], width, height, points);
            case >= 0x1FB68 and <= 0x1FB6F:
            {
                // Triangles from an edge to the center: left, top, right, bottom, first cut out of
                // a full cell and then on their own. Corners run clockwise from the top left, so
                // edge i goes from corner i to corner i + 1.
                ReadOnlySpan<Vector2> corners = [topLeft, topRight, bottomRight, bottomLeft];
                ReadOnlySpan<int> edges = [3, 0, 1, 2];
                var edge = edges[(int)(codepoint - 0x1FB68) % 4];
                if (codepoint >= 0x1FB6C)
                    return Points(points, corners[edge], corners[(edge + 1) % 4], center);
                return Points(points, corners[(edge + 1) % 4], corners[(edge + 2) % 4], corners[(edge + 3) % 4], corners[edge], center);
            }
            case 0x1FB9A: return Points(points, topLeft, topRight, center, bottomRight, bottomLeft, center);   // upper and lower triangles
            case 0x1FB9B: return Points(points, topLeft, center, topRight, bottomRight, center, bottomLeft);   // left and right triangles
            case 0x1FB9C: return Points(points, topLeft, bottomLeft, topRight);       // upper left half, shaded
            case 0x1FB9D: return Points(points, topLeft, bottomRight, topRight);      // upper right
            case 0x1FB9E: return Points(points, bottomLeft, bottomRight, topRight);   // lower right
            case 0x1FB9F: return Points(points, topLeft, bottomLeft, bottomRight);    // lower left
        }
        return 0;
    }

    /// <summary>Fills <paramref name="segments"/> for a line glyph from Symbols for Legacy
    /// Computing and returns how many; 0 for glyphs that are not lines.</summary>
    public static int Segments(uint codepoint, float width, float height, int light, Span<Segment> segments)
    {
        var n = 0;
        float w = width, h = height;
        switch (codepoint)
        {
            case 0x1FB98 or 0x1FB99:
            {
                // Diagonal fill: parallel lines about a stroke apart, running past the cell to be
                // clipped. Ghostty rounds the spacing, which leaves the corners bare at some sizes;
                // exact spacing makes the lines reach both corners.
                var count = Math.Clamp((int)width / (2 * Math.Max(1, light)), 1, (MaxSegments - 1) / 2);
                var stride = w / count;
                for (var i = -count; i <= count; i++)
                {
                    var x = i * stride;
                    segments[n++] = codepoint == 0x1FB98
                        ? new Segment(new(x, 0), new(w + x, h))   // upper left to lower right
                        : new Segment(new(w + x, 0), new(x, h));  // upper right to lower left
                }
                break;
            }
            case (>= 0x1FBA0 and <= 0x1FBAE) or 0x1FBBE or 0x1FBBF:
            {
                // Lines between the midpoints of the edges, cutting off each named corner.
                ReadOnlySpan<byte> corners = [1, 2, 4, 8, 5, 10, 12, 3, 9, 6, 14, 13, 11, 7, 15]; // 1 tl, 2 tr, 4 bl, 8 br
                var mask = codepoint switch { 0x1FBBE => 8, 0x1FBBF => 15, _ => corners[(int)(codepoint - 0x1FBA0)] };
                // The odd pixel goes to the far side, as Ghostty's does.
                float cx = MathF.Ceiling(w / 2), cy = MathF.Ceiling(h / 2);
                if ((mask & 1) != 0) segments[n++] = new(new(cx, 0), new(0, cy));
                if ((mask & 2) != 0) segments[n++] = new(new(cx, 0), new(w, cy));
                if ((mask & 4) != 0) segments[n++] = new(new(cx, h), new(0, cy));
                if ((mask & 8) != 0) segments[n++] = new(new(cx, h), new(w, cy));
                break;
            }
            case 0x1FBBD: // the light diagonal cross
                segments[n++] = new(new(0, 0), new(w, h));
                segments[n++] = new(new(w, 0), new(0, h));
                break;
            case >= 0x1FBD0 and <= 0x1FBDF:
            {
                // Diagonals between corners and edge midpoints, as fractions of the cell: each
                // entry is x0 y0 x1 y1, and the last eight are pairs joined in the middle.
                ReadOnlySpan<byte> halves =
                [
                    2, 1, 0, 2,  2, 0, 0, 1,  0, 0, 2, 1,  0, 1, 2, 2,  0, 0, 1, 2,  1, 0, 2, 2,  2, 0, 1, 2,  1, 0, 0, 2,
                    0, 0, 1, 1, 1, 1, 2, 0,   2, 0, 1, 1, 1, 1, 2, 2,   0, 2, 1, 1, 1, 1, 2, 2,   0, 0, 1, 1, 1, 1, 0, 2,
                    0, 0, 1, 2, 1, 2, 2, 0,   2, 0, 0, 1, 0, 1, 2, 2,   0, 2, 1, 0, 1, 0, 2, 2,   0, 0, 2, 1, 2, 1, 0, 2,
                ];
                var index = (int)(codepoint - 0x1FBD0);
                var (start, lines) = index < 8 ? (index * 4, 1) : (32 + (index - 8) * 8, 2);
                for (var i = 0; i < lines; i++)
                {
                    var p = halves.Slice(start + i * 4, 4);
                    segments[n++] = new(new(w * p[0] / 2, h * p[1] / 2), new(w * p[2] / 2, h * p[3] / 2));
                }
                break;
            }
        }
        return n;
    }

    /// <summary>The circles of Symbols for Legacy Computing: a circle the size of the cell's
    /// shorter side centered on an edge or corner, clipped to the cell, filled or outlined.</summary>
    public static bool Circle(uint codepoint, float width, float height, out Vector2 center, out float radius, out bool filled)
    {
        (float X, float Y)? at = codepoint switch
        {
            0x1FBE0 or 0x1FBE8 => (0.5f, 0),
            0x1FBE1 or 0x1FBE9 => (1, 0.5f),
            0x1FBE2 or 0x1FBEA => (0.5f, 1),
            0x1FBE3 or 0x1FBEB => (0, 0.5f),
            0x1FBEC => (1, 0),
            0x1FBED => (0, 1),
            0x1FBEE => (1, 1),
            0x1FBEF => (0, 0),
            _ => null,
        };
        center = at is { } c ? new Vector2(c.X * width, c.Y * height) : default;
        radius = 0.5f * MathF.Min(width, height);
        filled = codepoint >= 0x1FBE8;
        return at is not null;
    }

    private static int Points(Span<Vector2> points, params ReadOnlySpan<Vector2> corners)
    {
        corners.CopyTo(points);
        return corners.Length;
    }

    /// <summary>Smooth mosaics as a 3x4 grid, top row first: '#' marks the filled points of the
    /// grid, and the outline runs through them on the cell edge (left, upper and lower thirds,
    /// bottom, center) clockwise from the top left. Slopes are drawn as '/' and '\' for the
    /// reader; only '#' matters. Transcribed from Ghostty's table.</summary>
    private static readonly string[] Mosaics =
    [
        /* 1FB3C */ "..." + "..." + "#.." + "##.",
        /* 1FB3D */ "..." + "..." + "#\\." + "###",
        /* 1FB3E */ "..." + "#.." + "#\\." + "##.",
        /* 1FB3F */ "..." + "#.." + "##." + "###",
        /* 1FB40 */ "#.." + "#.." + "##." + "##.",
        /* 1FB41 */ "/##" + "###" + "###" + "###",
        /* 1FB42 */ "./#" + "###" + "###" + "###",
        /* 1FB43 */ ".##" + ".##" + "###" + "###",
        /* 1FB44 */ "..#" + ".##" + "###" + "###",
        /* 1FB45 */ ".##" + ".##" + ".##" + "###",
        /* 1FB46 */ "..." + "./#" + "###" + "###",
        /* 1FB47 */ "..." + "..." + "..#" + ".##",
        /* 1FB48 */ "..." + "..." + "./#" + "###",
        /* 1FB49 */ "..." + "..#" + "./#" + ".##",
        /* 1FB4A */ "..." + "..#" + ".##" + "###",
        /* 1FB4B */ "..#" + "..#" + ".##" + ".##",
        /* 1FB4C */ "##\\" + "###" + "###" + "###",
        /* 1FB4D */ "#\\." + "###" + "###" + "###",
        /* 1FB4E */ "##." + "##." + "###" + "###",
        /* 1FB4F */ "#.." + "##." + "###" + "###",
        /* 1FB50 */ "##." + "##." + "##." + "###",
        /* 1FB51 */ "..." + "#\\." + "###" + "###",
        /* 1FB52 */ "###" + "###" + "###" + "\\##",
        /* 1FB53 */ "###" + "###" + "###" + ".\\#",
        /* 1FB54 */ "###" + "###" + ".##" + ".##",
        /* 1FB55 */ "###" + "###" + ".##" + "..#",
        /* 1FB56 */ "###" + ".##" + ".##" + ".##",
        /* 1FB57 */ "##." + "#.." + "..." + "...",
        /* 1FB58 */ "###" + "#/." + "..." + "...",
        /* 1FB59 */ "##." + "#/." + "#.." + "...",
        /* 1FB5A */ "###" + "##." + "#.." + "...",
        /* 1FB5B */ "##." + "##." + "#.." + "#..",
        /* 1FB5C */ "###" + "###" + "#/." + "...",
        /* 1FB5D */ "###" + "###" + "###" + "##/",
        /* 1FB5E */ "###" + "###" + "###" + "#/.",
        /* 1FB5F */ "###" + "###" + "##." + "##.",
        /* 1FB60 */ "###" + "###" + "##." + "#..",
        /* 1FB61 */ "###" + "##." + "##." + "##.",
        /* 1FB62 */ ".##" + "..#" + "..." + "...",
        /* 1FB63 */ "###" + ".\\#" + "..." + "...",
        /* 1FB64 */ ".##" + ".\\#" + "..#" + "...",
        /* 1FB65 */ "###" + ".##" + "..#" + "...",
        /* 1FB66 */ ".##" + ".##" + "..#" + "..#",
        /* 1FB67 */ "###" + "###" + ".\\#" + "...",
    ];

    /// <summary>Walks the outline points of a smooth mosaic. A point on an edge between two filled
    /// corners is skipped, since the straight edge already passes through it.</summary>
    private static int Mosaic(string pattern, float width, float height, Span<Vector2> points)
    {
        bool On(int row, int column) => pattern[row * 3 + column] == '#';
        float left = 0, center = width / 2, right = width, top = 0, upper = height / 3, lower = 2 * height / 3, bottom = height;
        var n = 0;
        if (On(0, 0)) points[n++] = new(left, top);
        if (On(1, 0) && !(On(0, 0) && On(2, 0))) points[n++] = new(left, upper);
        if (On(2, 0) && !(On(1, 0) && On(3, 0))) points[n++] = new(left, lower);
        if (On(3, 0)) points[n++] = new(left, bottom);
        if (On(3, 1) && !(On(3, 0) && On(3, 2))) points[n++] = new(center, bottom);
        if (On(3, 2)) points[n++] = new(right, bottom);
        if (On(2, 2) && !(On(3, 2) && On(1, 2))) points[n++] = new(right, lower);
        if (On(1, 2) && !(On(2, 2) && On(0, 2))) points[n++] = new(right, upper);
        if (On(0, 2)) points[n++] = new(right, top);
        if (On(0, 1) && !(On(0, 2) && On(0, 0))) points[n++] = new(center, top);
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
