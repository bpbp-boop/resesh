using System.Text.RegularExpressions;

namespace Resesh.Core.History;

public enum DiffKind
{
    Same,
    Removed,
    Added,
}

/// <summary>One line of a comparison. <see cref="Text"/> is the newer run's text for
/// unchanged lines. <see cref="OldText"/> is set on an unchanged line whose raw text differs
/// only in ignored values (a timestamp, say). <see cref="Changed"/> marks the words that
/// differ from the paired line on the other side.</summary>
public sealed record DiffLine(
    DiffKind Kind,
    int? OldNumber,
    int? NewNumber,
    string Text,
    IReadOnlyList<TextSpan> Changed,
    string? OldText = null)
{
    public bool DiffersOnlyInIgnoredValues => OldText is not null && OldText != Text;
}

/// <summary>A unified-view row: a line, or <see cref="Skipped"/> unchanged lines folded away.</summary>
public sealed record DiffRow(DiffLine? Line, int Skipped = 0)
{
    public bool IsGap => Line is null;
}

/// <summary>A side-by-side row: the older line on the left, the newer on the right. A side
/// is null where the other side has no counterpart.</summary>
public sealed record SideBySideRow(DiffLine? Left, DiffLine? Right, int Skipped = 0)
{
    public bool IsGap => Left is null && Right is null;
}

public sealed record OutputDiffOptions
{
    /// <summary>Dates, clock times, and uptime counters compare as equal.</summary>
    public bool IgnoreTimestamps { get; init; } = true;

    /// <summary>Every number compares as equal (counters, sizes, but also metrics and addresses).</summary>
    public bool IgnoreNumbers { get; init; }
}

public sealed class OutputComparison
{
    internal OutputComparison(IReadOnlyList<DiffLine> lines, bool aligned)
    {
        Lines = lines;
        Aligned = aligned;
        Added = lines.Count(line => line.Kind == DiffKind.Added);
        Removed = lines.Count(line => line.Kind == DiffKind.Removed);
    }

    public IReadOnlyList<DiffLine> Lines { get; }
    public int Added { get; }
    public int Removed { get; }
    public bool Identical => Added == 0 && Removed == 0;

    /// <summary>False when the outputs were too different to line up; the differing middle
    /// is then shown as removed, then added.</summary>
    public bool Aligned { get; }

    /// <summary>Rows for a unified view, with unchanged stretches longer than twice
    /// <paramref name="context"/> folded. A negative context shows every line.</summary>
    public IReadOnlyList<DiffRow> Unified(int context)
    {
        var keep = Visible(Lines.Select(line => line.Kind != DiffKind.Same).ToList(), context);
        var rows = new List<DiffRow>();
        var skipped = 0;
        for (var i = 0; i < Lines.Count; i++)
        {
            if (!keep[i])
            {
                skipped++;
                continue;
            }
            if (skipped > 0)
                rows.Add(new DiffRow(null, skipped));
            skipped = 0;
            rows.Add(new DiffRow(Lines[i]));
        }
        if (skipped > 0)
            rows.Add(new DiffRow(null, skipped));
        return rows;
    }

    /// <summary>Rows for a side-by-side view: removed and added lines of one change pair up
    /// in order; folding works as in <see cref="Unified"/>.</summary>
    public IReadOnlyList<SideBySideRow> SideBySide(int context)
    {
        var pairs = new List<SideBySideRow>();
        for (var i = 0; i < Lines.Count;)
        {
            var line = Lines[i];
            if (line.Kind == DiffKind.Same)
            {
                var left = line with { Text = line.OldText ?? line.Text, NewNumber = null };
                pairs.Add(new SideBySideRow(left, line with { OldNumber = null }));
                i++;
                continue;
            }
            var removed = new List<DiffLine>();
            var added = new List<DiffLine>();
            while (i < Lines.Count && Lines[i].Kind != DiffKind.Same)
            {
                (Lines[i].Kind == DiffKind.Removed ? removed : added).Add(Lines[i]);
                i++;
            }
            for (var k = 0; k < Math.Max(removed.Count, added.Count); k++)
                pairs.Add(new SideBySideRow(k < removed.Count ? removed[k] : null, k < added.Count ? added[k] : null));
        }

        var keep = Visible(pairs.Select(row => row.Left?.Kind != DiffKind.Same || row.Right?.Kind != DiffKind.Same).ToList(), context);
        var rows = new List<SideBySideRow>();
        var skipped = 0;
        for (var i = 0; i < pairs.Count; i++)
        {
            if (!keep[i])
            {
                skipped++;
                continue;
            }
            if (skipped > 0)
                rows.Add(new SideBySideRow(null, null, skipped));
            skipped = 0;
            rows.Add(pairs[i]);
        }
        if (skipped > 0)
            rows.Add(new SideBySideRow(null, null, skipped));
        return rows;
    }

    /// <summary>Which rows stay visible: changes, their context, and any folded stretch
    /// too short to be worth a marker.</summary>
    private static bool[] Visible(IReadOnlyList<bool> changed, int context)
    {
        var keep = new bool[changed.Count];
        if (context < 0)
        {
            Array.Fill(keep, true);
            return keep;
        }
        for (var i = 0; i < changed.Count; i++)
        {
            if (!changed[i])
                continue;
            for (var j = Math.Max(0, i - context); j <= Math.Min(changed.Count - 1, i + context); j++)
                keep[j] = true;
        }
        // Folding one or two lines saves nothing; show them.
        for (var i = 0; i < keep.Length;)
        {
            if (keep[i])
            {
                i++;
                continue;
            }
            var end = i;
            while (end < keep.Length && !keep[end])
                end++;
            if (end - i <= 2)
                Array.Fill(keep, true, i, end - i);
            i = end;
        }
        return keep;
    }
}

/// <summary>
/// Line-by-line comparison of two command outputs (Myers' difference algorithm), with the
/// changed words inside paired lines marked, and optional masking of values that change on
/// every run. Common leading and trailing lines are trimmed first; if the remaining middle
/// needs more edits than <see cref="MaxEdits"/>, it is reported as replaced wholesale rather
/// than aligned, which keeps time and memory bounded for unrelated outputs.
/// </summary>
public static partial class OutputDiff
{
    internal const int MaxEdits = 1500;
    private const int MaxWordEdits = 120;

    public static OutputComparison Compare(string older, string newer, OutputDiffOptions? options = null)
    {
        options ??= new OutputDiffOptions();
        var a = SplitLines(older);
        var b = SplitLines(newer);
        var interned = new Dictionary<string, int>(StringComparer.Ordinal);
        int Key(string line) => Intern(interned, Mask(line, options));
        var ka = a.Select(Key).ToArray();
        var kb = b.Select(Key).ToArray();

        var prefix = 0;
        while (prefix < ka.Length && prefix < kb.Length && ka[prefix] == kb[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < ka.Length - prefix && suffix < kb.Length - prefix
               && ka[ka.Length - 1 - suffix] == kb[kb.Length - 1 - suffix])
            suffix++;

        var ops = new List<(DiffKind Kind, int A, int B)>();
        for (var i = 0; i < prefix; i++)
            ops.Add((DiffKind.Same, i, i));
        var middle = Myers(ka, prefix, ka.Length - suffix, kb, prefix, kb.Length - suffix, MaxEdits);
        var aligned = middle is not null;
        if (middle is null)
        {
            middle = [];
            for (var i = prefix; i < ka.Length - suffix; i++)
                middle.Add((DiffKind.Removed, i, -1));
            for (var j = prefix; j < kb.Length - suffix; j++)
                middle.Add((DiffKind.Added, -1, j));
        }
        ops.AddRange(middle);
        for (var i = 0; i < suffix; i++)
            ops.Add((DiffKind.Same, ka.Length - suffix + i, kb.Length - suffix + i));

        return new OutputComparison(BuildLines(ops, a, b, aligned), aligned);
    }

    private static List<DiffLine> BuildLines(List<(DiffKind Kind, int A, int B)> ops, string[] a, string[] b, bool aligned)
    {
        var lines = new List<DiffLine>(ops.Count);
        for (var i = 0; i < ops.Count;)
        {
            var op = ops[i];
            if (op.Kind == DiffKind.Same)
            {
                var oldText = a[op.A] == b[op.B] ? null : a[op.A];
                lines.Add(new DiffLine(DiffKind.Same, op.A + 1, op.B + 1, b[op.B], [], oldText));
                i++;
                continue;
            }

            // One change: its removed lines, then its added lines. Pair them in order for
            // word highlights, the way a reader lines them up.
            var removed = new List<int>();
            var added = new List<int>();
            while (i < ops.Count && ops[i].Kind != DiffKind.Same)
            {
                if (ops[i].Kind == DiffKind.Removed)
                    removed.Add(ops[i].A);
                else
                    added.Add(ops[i].B);
                i++;
            }
            var removedSpans = new IReadOnlyList<TextSpan>[removed.Count];
            var addedSpans = new IReadOnlyList<TextSpan>[added.Count];
            for (var k = 0; k < removed.Count; k++)
                removedSpans[k] = [];
            for (var k = 0; k < added.Count; k++)
                addedSpans[k] = [];
            if (aligned)
            {
                for (var k = 0; k < Math.Min(removed.Count, added.Count); k++)
                    (removedSpans[k], addedSpans[k]) = WordChanges(a[removed[k]], b[added[k]]);
            }
            for (var k = 0; k < removed.Count; k++)
                lines.Add(new DiffLine(DiffKind.Removed, removed[k] + 1, null, a[removed[k]], removedSpans[k]));
            for (var k = 0; k < added.Count; k++)
                lines.Add(new DiffLine(DiffKind.Added, null, added[k] + 1, b[added[k]], addedSpans[k]));
        }
        return lines;
    }

    /// <summary>The words that differ between two paired lines. Lines that share little
    /// get no word marks: the whole line is already shown as changed.</summary>
    internal static (IReadOnlyList<TextSpan> Old, IReadOnlyList<TextSpan> New) WordChanges(string older, string newer)
    {
        var ta = Tokenize(older);
        var tb = Tokenize(newer);
        var interned = new Dictionary<string, int>(StringComparer.Ordinal);
        var ka = ta.Select(token => Intern(interned, older.Substring(token.Start, token.Length))).ToArray();
        var kb = tb.Select(token => Intern(interned, newer.Substring(token.Start, token.Length))).ToArray();
        var ops = Myers(ka, 0, ka.Length, kb, 0, kb.Length, MaxWordEdits);
        if (ops is null)
            return ([], []);

        var oldSpans = new List<TextSpan>();
        var newSpans = new List<TextSpan>();
        foreach (var op in ops)
        {
            if (op.Kind == DiffKind.Removed)
                AddMerged(oldSpans, ta[op.A]);
            else if (op.Kind == DiffKind.Added)
                AddMerged(newSpans, tb[op.B]);
        }
        var oldChanged = oldSpans.Sum(span => span.Length);
        var newChanged = newSpans.Sum(span => span.Length);
        if (oldChanged > older.Length * 0.6 || newChanged > newer.Length * 0.6)
            return ([], []);
        return (Trim(older, oldSpans), Trim(newer, newSpans));
    }

    /// <summary>Whitespace at a span's edges is not worth highlighting.</summary>
    private static IReadOnlyList<TextSpan> Trim(string text, List<TextSpan> spans)
    {
        var result = new List<TextSpan>(spans.Count);
        foreach (var span in spans)
        {
            var start = span.Start;
            var end = span.End;
            while (start < end && char.IsWhiteSpace(text[start]))
                start++;
            while (end > start && char.IsWhiteSpace(text[end - 1]))
                end--;
            if (end > start)
                result.Add(new TextSpan(start, end - start));
        }
        return result;
    }

    private static void AddMerged(List<TextSpan> spans, TextSpan token)
    {
        if (spans.Count > 0 && spans[^1].End == token.Start)
            spans[^1] = new TextSpan(spans[^1].Start, spans[^1].Length + token.Length);
        else
            spans.Add(token);
    }

    private static List<TextSpan> Tokenize(string text) =>
        WordToken().Matches(text).Select(match => new TextSpan(match.Index, match.Length)).ToList();

    /// <summary>The comparison key for a line: trailing spaces dropped, ignored values masked.</summary>
    internal static string Mask(string line, OutputDiffOptions options)
    {
        var key = line.TrimEnd();
        if (options.IgnoreTimestamps)
            key = Timestamp().Replace(key, "⟨time⟩");
        if (options.IgnoreNumbers)
            key = Number().Replace(key, "#");
        return key;
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd('\r');
        return lines;
    }

    private static int Intern(Dictionary<string, int> table, string value)
    {
        if (!table.TryGetValue(value, out var id))
        {
            id = table.Count;
            table[value] = id;
        }
        return id;
    }

    /// <summary>Myers' O((N+M)D) shortest edit script over a[aStart..aEnd) and b[bStart..bEnd).
    /// Keeps only the live diagonal range per step, so memory is O(D²). Returns null when
    /// more than <paramref name="maxEdits"/> edits would be needed.</summary>
    internal static List<(DiffKind Kind, int A, int B)>? Myers(
        int[] a, int aStart, int aEnd, int[] b, int bStart, int bEnd, int maxEdits)
    {
        var n = aEnd - aStart;
        var m = bEnd - bStart;
        var max = n + m;
        var ops = new List<(DiffKind, int, int)>();
        if (max == 0)
            return ops;

        var limit = Math.Min(max, maxEdits);
        var offset = limit + 1;
        var v = new int[2 * limit + 3];
        var trace = new List<int[]>();
        var found = -1;
        for (var d = 0; d <= limit && found < 0; d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
            var snapshot = new int[2 * d + 1];
            Array.Copy(v, offset - d, snapshot, 0, snapshot.Length);
            trace.Add(snapshot);
        }
        if (found < 0)
            return null;

        var cx = n;
        var cy = m;
        for (var d = found; d > 0; d--)
        {
            var previous = trace[d - 1];
            int Get(int k) => previous[k + d - 1];
            var kk = cx - cy;
            var down = kk == -d || (kk != d && Get(kk - 1) < Get(kk + 1));
            var prevK = down ? kk + 1 : kk - 1;
            var prevX = Get(prevK);
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                ops.Add((DiffKind.Same, aStart + cx - 1, bStart + cy - 1));
                cx--;
                cy--;
            }
            if (down)
                ops.Add((DiffKind.Added, -1, bStart + cy - 1));
            else
                ops.Add((DiffKind.Removed, aStart + cx - 1, -1));
            cx = prevX;
            cy = prevY;
        }
        while (cx > 0 && cy > 0)
        {
            ops.Add((DiffKind.Same, aStart + cx - 1, bStart + cy - 1));
            cx--;
            cy--;
        }
        ops.Reverse();
        return ops;
    }

    // Addresses, versions, paths, and interface names (10.0.0.1, 1.2.3, /var/log, Gi0/1)
    // are one word, so a change marks the whole value rather than one digit of it.
    [GeneratedRegex(@"\w+(?:[.:/-]\w+)*|\s+|[^\w\s]")]
    private static partial Regex WordToken();

    // ISO and common log timestamps, dates, clock times, month-day stamps, weekday names,
    // and compact uptimes such as 1d02h or 3w4d.
    [GeneratedRegex(
        @"\b\d{4}-\d{2}-\d{2}(?:[T ]\d{1,2}:\d{2}(?::\d{2}(?:[.,]\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?\b"
        + @"|\b\d{1,4}[/.]\d{1,2}[/.]\d{2,4}\b"
        + @"|\b\d{1,2}:\d{2}(?::\d{2}(?:[.,]\d+)?)?(?:\s?[AaPp][Mm])?\b"
        + @"|\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2}(?:,?\s+\d{4})?\b"
        + @"|\b(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)[a-z]*\b"
        + @"|\b(?:\d+[ywdhms]){2,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex Number();
}
