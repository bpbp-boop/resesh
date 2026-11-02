using System.Buffers;
using System.Text;
using Resesh.Core.Models;

namespace Resesh.Core.Telnet;

/// <summary>
/// Converts Telnet text to the terminal's UTF-8 byte stream after IAC processing.
/// Auto preserves valid UTF-8 and selects CP437 on an invalid sequence. A later
/// UTF-8 sequence of three or four bytes can switch back (BBS intros can precede
/// the server's UTF-8 detection). Two-byte sequences remain CP437 after detection
/// because common adjacent DOS block characters also form valid two-byte UTF-8.
/// Detection is a heuristic; a session can force either encoding for ambiguous text.
/// <para>
/// Typed input does not follow every switch of the output decoder, or what a key sends
/// would depend on what the last screen happened to contain (one stray Latin-1 byte from a
/// UTF-8 server turned the next "é" into CP437). Its encoding only moves forward: UTF-8
/// until the first invalid sequence says CP437, then UTF-8 for good once a three- or
/// four-byte sequence (strong evidence, rare in CP437 art) shows up. Ambiguous two-byte
/// sequences never decide it.
/// </para>
/// State belongs to one connection and calls must be serialized by the session.
/// </summary>
internal sealed class TelnetTextCodec(TelnetTextEncoding encoding)
{
    private static readonly Encoding Cp437 = CodePagesEncodingProvider.Instance.GetEncoding(437)!;
    private static readonly byte[][] Cp437Utf8 = Enumerable.Range(0, 256)
        .Select(value => Encoding.Convert(Cp437, Encoding.UTF8, [(byte)value])).ToArray();

    private enum InputEncoding { Undecided, Cp437, Utf8 }

    private readonly TelnetTextEncoding _encoding = encoding;
    private bool _autoCp437;
    private InputEncoding _input;
    private byte[] _pending = [];
    // The start of a UTF-8 character typed across two writes, kept as bytes so it survives
    // a change of input encoding between them.
    private byte[] _inputPartial = [];

    public byte[] DecodeOutput(ReadOnlySpan<byte> input, bool flush = false)
    {
        if (_encoding == TelnetTextEncoding.Utf8)
            return input.ToArray();
        if (_encoding == TelnetTextEncoding.Cp437)
            return Encoding.Convert(Cp437, Encoding.UTF8, input.ToArray());

        byte[] bytes = [.. _pending, .. input];
        _pending = [];
        var output = new List<byte>(bytes.Length);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var b = bytes[offset];
            if (b < 0x80)
            {
                output.Add(b);
                offset++;
                continue;
            }
            if (_autoCp437 && b < 0xe0)
            {
                AppendCp437(b, output);
                offset++;
                continue;
            }
            var status = Rune.DecodeFromUtf8(bytes.AsSpan(offset), out _, out var consumed);
            if (status == OperationStatus.Done)
            {
                _autoCp437 = false;
                if (consumed >= 3)
                    _input = InputEncoding.Utf8;
                for (var i = 0; i < consumed; i++)
                    output.Add(bytes[offset + i]);
                offset += consumed;
                continue;
            }
            if (status == OperationStatus.NeedMoreData && !flush)
            {
                _pending = bytes[offset..];
                break;
            }

            _autoCp437 = true;
            if (_input == InputEncoding.Undecided)
                _input = InputEncoding.Cp437;
            AppendCp437(b, output);
            offset++;
        }
        return [.. output];
    }

    private static void AppendCp437(byte value, List<byte> output) =>
        output.AddRange(Cp437Utf8[value]);

    public byte[] EncodeInput(ReadOnlySpan<byte> input)
    {
        byte[] bytes = _inputPartial.Length == 0 ? input.ToArray() : [.. _inputPartial, .. input];
        _inputPartial = [];
        var cp437 = _encoding == TelnetTextEncoding.Cp437
            || _encoding == TelnetTextEncoding.Auto && _input == InputEncoding.Cp437;
        if (!cp437)
            return bytes;

        // Keep a partial UTF-8 input character until the next write.
        var complete = CompleteUtf8Length(bytes);
        _inputPartial = bytes[complete..];
        return Cp437.GetBytes(Encoding.UTF8.GetString(bytes, 0, complete));
    }

    /// <summary>The length of <paramref name="bytes"/> without a trailing character whose
    /// remaining bytes have not arrived yet.</summary>
    private static int CompleteUtf8Length(byte[] bytes)
    {
        for (var i = bytes.Length - 1; i >= Math.Max(0, bytes.Length - 3); i--)
        {
            if ((bytes[i] & 0xC0) == 0x80)
                continue; // a continuation byte: look further back for its lead
            return Rune.DecodeFromUtf8(bytes.AsSpan(i), out _, out _) == OperationStatus.NeedMoreData
                ? i : bytes.Length;
        }
        return bytes.Length;
    }
}
