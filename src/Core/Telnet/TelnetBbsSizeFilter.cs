using System.Text;

namespace Resesh.Core.Telnet;

/// <summary>Answers common BBS screen-size probes without changing ordinary cursor reports.</summary>
internal sealed class TelnetBbsSizeFilter
{
    private static readonly byte[][] Probes =
    [
        "\u001b[255B\u001b[255C\u001b[6n"u8.ToArray(),
        "\u001b[999B\u001b[999C\u001b[6n"u8.ToArray(),
        "\u001b[255;255H\u001b[6n"u8.ToArray(),
        "\u001b[999;999H\u001b[6n"u8.ToArray(),
        "\u001b[18t"u8.ToArray(),
    ];
    private const byte Esc = 0x1B, Bel = 0x07, Can = 0x18, Sub = 0x1A;

    /// <summary>How long held bytes wait for the rest of a probe before going out as they
    /// are: a probe split across reads arrives well within this, while a BBS that sent a
    /// cursor move and is waiting for a key would otherwise show nothing.</summary>
    public static readonly TimeSpan HoldLimit = TimeSpan.FromMilliseconds(50);

    private readonly List<byte> _pending = [];
    private bool _inString;
    private bool _osc;
    // The last byte went out as a lone ESC (a flushed hold): the next may introduce a string.
    private bool _escapeFlushed;

    /// <summary>Output held back as the possible start of a probe.</summary>
    public bool HasHeldOutput => _pending.Count > 0;

    public byte[] Process(ReadOnlySpan<byte> input, List<byte> replies, bool flush = false)
    {
        var output = new List<byte>(input.Length);
        foreach (var value in input)
        {
            if (_inString)
            {
                // ECMA-48 / the VT parser: BEL ends an OSC, CAN and SUB abort any string,
                // and ESC ends it too (ST when a backslash follows, otherwise the start of
                // the next sequence, which must still be seen as a possible probe).
                if (value == Esc)
                {
                    _inString = false;
                    _pending.Add(value);
                    continue;
                }
                output.Add(value);
                if ((_osc && value == Bel) || value is Can or Sub)
                    _inString = false;
                continue;
            }

            var afterFlushedEscape = _escapeFlushed;
            _escapeFlushed = false;
            if (IsStringIntroducer(value) &&
                (afterFlushedEscape && _pending.Count == 0 || _pending.Count == 1 && _pending[0] == Esc))
            {
                _inString = true;
                _osc = value == ']';
                output.AddRange(_pending);
                output.Add(value);
                _pending.Clear();
                continue;
            }

            _pending.Add(value);
            while (_pending.Count > 0)
            {
                var pending = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending);
                byte[]? match = null;
                foreach (var probe in Probes)
                    if (pending.SequenceEqual(probe)) { match = probe; break; }
                if (match is not null)
                {
                    // Keep cursor movement, but consume the query so the renderer cannot
                    // also send its real dimensions. Save/restore commands pass through.
                    var sizeQuery = match[^1] == 't';
                    if (!sizeQuery)
                        output.AddRange(match[..^4]);
                    replies.AddRange(Encoding.ASCII.GetBytes(sizeQuery ? "\u001b[8;25;80t" : "\u001b[25;80R"));
                    _pending.Clear();
                    break;
                }
                var prefix = false;
                foreach (var probe in Probes)
                    if (probe.AsSpan().StartsWith(pending)) { prefix = true; break; }
                if (prefix)
                    break;
                output.Add(_pending[0]);
                _pending.RemoveAt(0);
            }
        }
        if (flush && _pending.Count > 0)
        {
            _escapeFlushed = _pending[^1] == Esc;
            output.AddRange(_pending);
            _pending.Clear();
        }
        return [.. output];
    }

    /// <summary>Releases held bytes once no more output arrived within <see cref="HoldLimit"/>.</summary>
    public byte[] FlushHeld() => Process([], [], flush: true);

    private static bool IsStringIntroducer(byte value) =>
        value is (byte)']' or (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X';
}
