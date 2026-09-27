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
    private readonly List<byte> _pending = [];
    private bool _inString;
    private bool _stringEscape;
    private bool _osc;

    public byte[] Process(ReadOnlySpan<byte> input, List<byte> replies, bool flush = false)
    {
        var output = new List<byte>(input.Length);
        foreach (var value in input)
        {
            if (_inString)
            {
                output.Add(value);
                if ((_osc && value == 7) || (_stringEscape && value == '\\'))
                    _inString = false;
                _stringEscape = value == 27;
                continue;
            }

            _pending.Add(value);
            if (_pending.Count == 2 && _pending[0] == 27 && value is (byte)']' or (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X')
            {
                _inString = true;
                _osc = value == ']';
                _stringEscape = false;
                output.AddRange(_pending);
                _pending.Clear();
                continue;
            }

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
        if (flush)
        {
            output.AddRange(_pending);
            _pending.Clear();
        }
        return [.. output];
    }
}
