using System.Globalization;

namespace Resesh.Core.Models;

public enum TerminalProgressState
{
    None,
    Normal,
    Error,
    Indeterminate,
    Paused,
}

/// <summary>
/// A task's progress as shown on a tab and the taskbar button. Remote programs report it
/// with ConEmu's <c>OSC 9 ; 4 ; state ; percent</c>, the form Windows Terminal also reads;
/// SFTP transfers report their own.
/// </summary>
public readonly record struct TerminalProgress(TerminalProgressState State, int Value)
{
    public static readonly TerminalProgress None = default;

    public bool IsActive => State != TerminalProgressState.None;

    public static TerminalProgress Percent(int value) =>
        new(TerminalProgressState.Normal, Math.Clamp(value, 0, 100));

    public static TerminalProgress Indeterminate { get; } = new(TerminalProgressState.Indeterminate, 0);

    /// <summary>Progress through a batch of <paramref name="count"/> files while file
    /// <paramref name="index"/> (1-based) has <paramref name="done"/> of <paramref name="total"/>
    /// bytes, so a multi-file transfer fills once instead of restarting per file.
    /// Indeterminate while the file's size is unknown.</summary>
    public static TerminalProgress FromTransfer(long done, long total, int index = 1, int count = 1)
    {
        if (total <= 0)
            return Indeterminate;
        count = Math.Max(1, count);
        var finished = Math.Clamp(index - 1, 0, count - 1);
        var file = (double)Math.Clamp(done, 0, total) / total;
        return Percent((int)((finished + file) * 100 / count));
    }

    /// <summary>
    /// Parses an OSC 9 payload. Returns null unless it is the <c>4;</c> progress form, or
    /// when its fields are malformed. States: 0 clear, 1 set, 2 error, 3 indeterminate,
    /// 4 paused. As in Windows Terminal, error and paused without a percent keep the
    /// current value, so a bar turns red or amber where it stood.
    /// </summary>
    public static TerminalProgress? ParseOsc9(string? payload, TerminalProgress current)
    {
        if (payload is null)
            return null;
        var fields = payload.Split(';');
        if (fields[0] != "4" || fields.Length > 3)
            return null;
        var state = 0;
        if (fields.Length > 1 && fields[1].Length > 0 && !TryParse(fields[1], out state))
            return null;
        int? percent = null;
        if (fields.Length > 2 && fields[2].Length > 0)
        {
            if (!TryParse(fields[2], out var value))
                return null;
            percent = Math.Clamp(value, 0, 100);
        }
        var kept = percent ?? (current.State is TerminalProgressState.None or TerminalProgressState.Indeterminate ? 0 : current.Value);
        return state switch
        {
            0 => None,
            1 => Percent(percent ?? 0),
            2 => new(TerminalProgressState.Error, kept),
            3 => new(TerminalProgressState.Indeterminate, current.Value),
            4 => new(TerminalProgressState.Paused, kept),
            _ => null,
        };
    }

    /// <summary>
    /// One indicator for several sources: a tab's terminal and file transfers, or all of a
    /// window's tabs. Trouble shows first (error, then paused), then known progress, where
    /// the least complete task decides, then indeterminate work.
    /// </summary>
    public static TerminalProgress Combine(IEnumerable<TerminalProgress> sources)
    {
        var result = None;
        foreach (var source in sources)
        {
            if (!source.IsActive)
                continue;
            var rank = Rank(source.State);
            var best = Rank(result.State);
            if (rank > best || (rank == best && source.Value < result.Value))
                result = source;
        }
        return result;
    }

    public static TerminalProgress Combine(params TerminalProgress[] sources) =>
        Combine((IEnumerable<TerminalProgress>)sources);

    private static int Rank(TerminalProgressState state) => state switch
    {
        TerminalProgressState.Error => 4,
        TerminalProgressState.Paused => 3,
        TerminalProgressState.Normal => 2,
        TerminalProgressState.Indeterminate => 1,
        _ => 0,
    };

    private static bool TryParse(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
