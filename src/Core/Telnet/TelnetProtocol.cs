using System.Text;

namespace Resesh.Core.Telnet;

/// <summary>
/// The telnet wire protocol for one connection, without any I/O: splits the incoming
/// stream into terminal data and IAC commands, answers option negotiation, and encodes
/// keyboard input. Supported options are BINARY, SGA, ECHO (server side only — we never
/// echo locally), TERMINAL-TYPE and NAWS; every other request is refused. State is kept
/// across <see cref="Receive"/> calls, so commands split between reads decode correctly.
/// Not thread-safe: the session serializes calls.
/// </summary>
public sealed class TelnetProtocol
{
    public const byte Se = 240, Nop = 241, DataMark = 242, Break = 243, Sb = 250;
    public const byte Will = 251, Wont = 252, Do = 253, Dont = 254, Iac = 255;

    public const byte OptBinary = 0, OptEcho = 1, OptSga = 3, OptTerminalType = 24, OptNaws = 31;
    private const byte TerminalTypeIs = 0, TerminalTypeSend = 1;
    private const int MaxSubnegotiation = 1024;

    private enum State { Data, Iac, Negotiate, Sub, SubIac }

    // Options we perform (WILL) and options we let the server perform (DO).
    private static bool AcceptLocal(byte option) =>
        option is OptBinary or OptSga or OptTerminalType or OptNaws;

    private static bool AcceptRemote(byte option) =>
        option is OptBinary or OptSga or OptEcho;

    // Per-option state, a reduced RFC 1143: "enabled" plus "we asked and await the answer".
    // Replying only on state changes is what prevents negotiation loops.
    private readonly bool[] _local = new bool[256];
    private readonly bool[] _localPending = new bool[256];
    private readonly bool[] _remote = new bool[256];
    private readonly bool[] _remotePending = new bool[256];

    private readonly byte[] _terminalType;
    private State _state;
    private byte _command;
    private readonly List<byte> _sub = [];
    private bool _afterCr;
    private int _columns;
    private int _rows;

    public TelnetProtocol(string terminalType, int columns, int rows)
    {
        _terminalType = Encoding.ASCII.GetBytes(string.IsNullOrWhiteSpace(terminalType)
            ? "xterm-256color" : terminalType.Trim());
        _columns = columns;
        _rows = rows;
    }

    /// <summary>The server echoes typed characters (the normal case for a login shell).</summary>
    public bool RemoteEcho => _remote[OptEcho];

    public bool IsLocalEnabled(byte option) => _local[option];
    public bool IsRemoteEnabled(byte option) => _remote[option];

    /// <summary>Offers sent right after connecting, so servers that wait for the client
    /// (rather than asking first) still get window size and terminal type.</summary>
    public byte[] InitialNegotiation()
    {
        var output = new List<byte>();
        RequestLocal(OptNaws, output);
        RequestLocal(OptTerminalType, output);
        RequestLocal(OptSga, output);
        RequestRemote(OptSga, output);
        RequestRemote(OptEcho, output);
        return [.. output];
    }

    /// <summary>Decodes bytes from the server. Terminal data is appended to
    /// <paramref name="data"/>; protocol replies to send back go to <paramref name="replies"/>.</summary>
    public void Receive(ReadOnlySpan<byte> input, List<byte> data, List<byte> replies)
    {
        foreach (var b in input)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == Iac)
                    {
                        _state = State.Iac;
                        break;
                    }
                    // NVT: CR NUL is a bare carriage return; drop the NUL outside binary mode.
                    if (b == 0 && _afterCr && !_remote[OptBinary])
                    {
                        _afterCr = false;
                        break;
                    }
                    _afterCr = b == (byte)'\r';
                    data.Add(b);
                    break;

                case State.Iac:
                    switch (b)
                    {
                        case Iac:
                            data.Add(Iac);
                            _afterCr = false;
                            _state = State.Data;
                            break;
                        case Will or Wont or Do or Dont:
                            _command = b;
                            _state = State.Negotiate;
                            break;
                        case Sb:
                            _sub.Clear();
                            _state = State.Sub;
                            break;
                        default:
                            // NOP, DM, GA, AYT, ... carry nothing a terminal can show.
                            _state = State.Data;
                            break;
                    }
                    break;

                case State.Negotiate:
                    Negotiate(_command, b, replies);
                    _state = State.Data;
                    break;

                case State.Sub:
                    if (b == Iac)
                        _state = State.SubIac;
                    else if (_sub.Count < MaxSubnegotiation)
                        _sub.Add(b);
                    break;

                case State.SubIac:
                    if (b == Se)
                    {
                        Subnegotiate(replies);
                        _state = State.Data;
                    }
                    else
                    {
                        // IAC IAC inside SB is a literal 255; anything else is malformed —
                        // keep collecting rather than resyncing on a guess.
                        if (b == Iac && _sub.Count < MaxSubnegotiation)
                            _sub.Add(Iac);
                        _state = State.Sub;
                    }
                    break;
            }
        }
    }

    /// <summary>Encodes keyboard/paste bytes for the wire: IAC doubled, and outside binary
    /// mode a lone CR sent as CR NUL (RFC 854) so the server sees exactly one Enter.</summary>
    public byte[] EncodeInput(ReadOnlySpan<byte> input)
    {
        var binary = _local[OptBinary];
        var output = new List<byte>(input.Length + 8);
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            output.Add(b);
            if (b == Iac)
                output.Add(Iac);
            else if (b == (byte)'\r' && !binary && (i + 1 >= input.Length || input[i + 1] != (byte)'\n'))
                output.Add(0);
        }
        return [.. output];
    }

    /// <summary>Records the terminal size; returns the NAWS report to send, or an empty
    /// array while the server has not enabled NAWS (it is sent when it does).</summary>
    public byte[] Resize(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
        if (!_local[OptNaws])
            return [];
        var output = new List<byte>();
        AppendWindowSize(output);
        return [.. output];
    }

    /// <summary>IAC BRK — the telnet "break" key, used by network gear for boot interrupts.</summary>
    public static byte[] BreakCommand() => [Iac, Break];

    private void Negotiate(byte command, byte option, List<byte> replies)
    {
        switch (command)
        {
            case Will:
                if (_remote[option])
                {
                    _remotePending[option] = false;
                    return;
                }
                if (AcceptRemote(option))
                {
                    _remote[option] = true;
                    if (!_remotePending[option])
                        Send(replies, Do, option);
                    _remotePending[option] = false;
                }
                else
                {
                    Send(replies, Dont, option);
                }
                break;

            case Wont:
                if (_remote[option] || _remotePending[option])
                {
                    var wasAsked = _remotePending[option] && !_remote[option];
                    _remote[option] = false;
                    _remotePending[option] = false;
                    if (!wasAsked)
                        Send(replies, Dont, option);
                }
                break;

            case Do:
                if (_local[option])
                {
                    _localPending[option] = false;
                    return;
                }
                if (AcceptLocal(option))
                {
                    _local[option] = true;
                    if (!_localPending[option])
                        Send(replies, Will, option);
                    _localPending[option] = false;
                    if (option == OptNaws)
                        AppendWindowSize(replies);
                }
                else
                {
                    Send(replies, Wont, option);
                }
                break;

            case Dont:
                if (_local[option] || _localPending[option])
                {
                    var wasAsked = _localPending[option] && !_local[option];
                    _local[option] = false;
                    _localPending[option] = false;
                    if (!wasAsked)
                        Send(replies, Wont, option);
                }
                break;
        }
    }

    private void Subnegotiate(List<byte> replies)
    {
        if (_sub.Count >= 2 && _sub[0] == OptTerminalType && _sub[1] == TerminalTypeSend && _local[OptTerminalType])
        {
            // We have one name; repeating it on later SENDs tells the server the list ended.
            replies.AddRange([Iac, Sb, OptTerminalType, TerminalTypeIs]);
            replies.AddRange(_terminalType);
            replies.AddRange([Iac, Se]);
        }
    }

    private void RequestLocal(byte option, List<byte> output)
    {
        _localPending[option] = true;
        Send(output, Will, option);
    }

    private void RequestRemote(byte option, List<byte> output)
    {
        _remotePending[option] = true;
        Send(output, Do, option);
    }

    private void AppendWindowSize(List<byte> output)
    {
        output.AddRange([Iac, Sb, OptNaws]);
        AppendEscaped16(output, _columns);
        AppendEscaped16(output, _rows);
        output.AddRange([Iac, Se]);
    }

    private static void AppendEscaped16(List<byte> output, int value)
    {
        var clamped = Math.Clamp(value, 0, ushort.MaxValue);
        foreach (var b in new[] { (byte)(clamped >> 8), (byte)clamped })
        {
            output.Add(b);
            if (b == Iac)
                output.Add(Iac);
        }
    }

    private static void Send(List<byte> output, byte command, byte option) =>
        output.AddRange([Iac, command, option]);
}
