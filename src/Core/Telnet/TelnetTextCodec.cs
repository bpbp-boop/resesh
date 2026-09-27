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
/// State belongs to one connection and calls must be serialized by the session.
/// </summary>
internal sealed class TelnetTextCodec(TelnetTextEncoding encoding)
{
    private static readonly Encoding Cp437 = CodePagesEncodingProvider.Instance.GetEncoding(437)!;
    private static readonly byte[][] Cp437Utf8 = Enumerable.Range(0, 256)
        .Select(value => Encoding.Convert(Cp437, Encoding.UTF8, [(byte)value])).ToArray();
    private readonly TelnetTextEncoding _encoding = encoding;
    private bool _autoCp437;
    private byte[] _pending = [];
    private readonly Decoder _inputDecoder = Encoding.UTF8.GetDecoder();

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
            AppendCp437(b, output);
            offset++;
        }
        return [.. output];
    }

    private static void AppendCp437(byte value, List<byte> output) =>
        output.AddRange(Cp437Utf8[value]);

    public byte[] EncodeInput(ReadOnlySpan<byte> input)
    {
        if (_encoding != TelnetTextEncoding.Cp437 && !(_encoding == TelnetTextEncoding.Auto && _autoCp437))
            return input.ToArray();

        // Keep a partial UTF-8 input character until the next write.
        var chars = new char[Encoding.UTF8.GetMaxCharCount(input.Length)];
        var count = _inputDecoder.GetChars(input, chars, flush: false);
        return Cp437.GetBytes(chars, 0, count);
    }
}
