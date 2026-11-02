using System.Text;
using System.Text.Json;
using Resesh.Core.Models;
using Resesh.Core.Telnet;

namespace Resesh.Core.Tests;

public sealed class TelnetTextCodecTests
{
    [Fact]
    public void BbsArt_DecodesIdenticallyAtEveryReadBoundary()
    {
        // CP437 block/shade bytes used in the blackflag.acid.org intro.
        byte[] art = [.. "\u001b[1;36m"u8, 0xdb, 0xdb, 0xb2, 0xb1, 0xb0, 0xdf, 0xdc, 0xdd, 0xde, 0xfe,
            .. "\u001b[0m\r\nLogin: "u8];
        var expected = "\u001b[1;36m██▓▒░▀▄▌▐■\u001b[0m\r\nLogin: ";
        for (var size = 1; size <= art.Length; size++)
            Assert.Equal(expected, DecodeChunks(art, size));
    }

    [Fact]
    public void Auto_PreservesUtf8AcrossEveryReadBoundary()
    {
        var text = "\u001b[32m café 漢字 😀 █▓\u001b[0m\r\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var size = 1; size <= bytes.Length; size++)
            Assert.Equal(text, DecodeChunks(bytes, size));
    }

    [Fact]
    public void Auto_HandlesCp437IntroFollowedByUtf8ArtAtEveryReadBoundary()
    {
        byte[] bytes = [0xdb, 0xdb, 0xb2, .. "\r\n\u001b[37m"u8,
            .. "▄█▀ café\u001b[0m\r\nlogin: "u8];
        for (var size = 1; size <= bytes.Length; size++)
            Assert.Equal("██▓\r\n\u001b[37m▄█▀ café\u001b[0m\r\nlogin: ", DecodeChunks(bytes, size));
    }

    [Fact]
    public void Auto_DoesNotDelayAsciiPrompts()
    {
        var codec = new TelnetTextCodec(TelnetTextEncoding.Auto);
        Assert.Equal("login: "u8.ToArray(), codec.DecodeOutput("login: "u8));
        Assert.Equal("\u001b[24;80R\r"u8.ToArray(), codec.EncodeInput("\u001b[24;80R\r"u8));
    }

    [Fact]
    public void Auto_FlushesAnIncompleteSequenceAsCp437()
    {
        var codec = new TelnetTextCodec(TelnetTextEncoding.Auto);
        Assert.Empty(codec.DecodeOutput([0xdb]));
        Assert.Equal("█", Encoding.UTF8.GetString(codec.DecodeOutput([], flush: true)));
        Assert.Empty(codec.DecodeOutput([], flush: true));
    }

    [Fact]
    public void ForcedCp437_HandlesBytesThatCouldAlsoBeUtf8()
    {
        var codec = new TelnetTextCodec(TelnetTextEncoding.Cp437);
        Assert.Equal("├⌐", Encoding.UTF8.GetString(codec.DecodeOutput([0xc3, 0xa9])));
        // Control bytes must remain terminal commands, not DOS display glyphs.
        Assert.Equal(new byte[] { 0x1b, 7, 8, 9, 10, 12, 13 }, codec.DecodeOutput([0x1b, 7, 8, 9, 10, 12, 13]));
    }

    [Fact]
    public void ForcedUtf8_DoesNotSwitchOnInvalidData()
    {
        var codec = new TelnetTextCodec(TelnetTextEncoding.Utf8);
        Assert.Equal(new byte[] { 0xdb, 0xdb }, codec.DecodeOutput([0xdb, 0xdb]));
        Assert.Equal("é"u8.ToArray(), codec.EncodeInput("é"u8));
    }

    [Fact]
    public void Cp437Input_ConvertsSplitUtf8AndPreservesAnsiCommands()
    {
        var codec = new TelnetTextCodec(TelnetTextEncoding.Auto);
        codec.DecodeOutput([0xdb, 0xdb]);
        Assert.Empty(codec.EncodeInput([0xc3]));
        Assert.Equal(new byte[] { 0x82 }, codec.EncodeInput([0xa9]));
        Assert.Equal("\u001b[A\r"u8.ToArray(), codec.EncodeInput("\u001b[A\r"u8));
        Assert.Equal(new byte[] { 0xff }, codec.EncodeInput("\u00a0"u8));
    }

    [Fact]
    public void AutoInput_IgnoresAStrayInvalidByteOnceUtf8IsEstablished()
    {
        var codec = new TelnetTextCodec(TelnetTextEncoding.Auto);
        codec.DecodeOutput("漢字\r\n"u8);
        codec.DecodeOutput([0xe9, 0x20]); // a Latin-1 filename in a listing
        Assert.Equal("é"u8.ToArray(), codec.EncodeInput("é"u8));
    }

    [Fact]
    public void AutoInput_MovesToUtf8OnceAndStays()
    {
        var codec = new TelnetTextCodec(TelnetTextEncoding.Auto);
        codec.DecodeOutput([0xdb, 0xdb, 0x20]); // CP437 intro
        Assert.Equal(new byte[] { 0x82 }, codec.EncodeInput("é"u8));
        codec.DecodeOutput("▄█▀"u8); // the server switched to UTF-8
        Assert.Equal("é"u8.ToArray(), codec.EncodeInput("é"u8));
        // Output still decodes the next invalid byte as CP437, but input does not flip back.
        Assert.Equal("█ ", Encoding.UTF8.GetString(codec.DecodeOutput([0xdb, 0x20])));
        Assert.Equal("é"u8.ToArray(), codec.EncodeInput("é"u8));
    }

    [Fact]
    public void AutoInput_IsNotDecidedByAmbiguousTwoByteSequences()
    {
        var codec = new TelnetTextCodec(TelnetTextEncoding.Auto);
        codec.DecodeOutput([0xdb, 0xb2]); // "█▓" in CP437, also valid two-byte UTF-8
        codec.DecodeOutput([0xdb, 0x20]); // then plainly CP437
        Assert.Equal(new byte[] { 0x82 }, codec.EncodeInput("é"u8));
    }

    [Fact]
    public void AutoInput_KeepsAPartialCharacterAcrossAnEncodingChange()
    {
        var codec = new TelnetTextCodec(TelnetTextEncoding.Auto);
        codec.DecodeOutput([0xdb, 0x20]);
        Assert.Empty(codec.EncodeInput([0xc3]));
        codec.DecodeOutput("▄"u8);
        Assert.Equal(new byte[] { 0xc3, 0xa9 }, codec.EncodeInput([0xa9]));
    }

    [Fact]
    public void Cp437Input_HoldsOnlyAnIncompleteTrailingCharacter()
    {
        var codec = new TelnetTextCodec(TelnetTextEncoding.Cp437);
        Assert.Equal(new byte[] { (byte)'a', 0x82 }, codec.EncodeInput([(byte)'a', 0xc3, 0xa9, 0xe2, 0x96]));
        Assert.Equal(new byte[] { 0xdc }, codec.EncodeInput([0x84])); // ▄
    }

    [Fact]
    public void TelnetIacIsUnescapedBeforeTextConversion_AndEscapedAfterInputConversion()
    {
        var protocol = new TelnetProtocol("xterm", 80, 25);
        var codec = new TelnetTextCodec(TelnetTextEncoding.Cp437);
        var data = new List<byte>();
        var replies = new List<byte>();
        protocol.Receive([255, 251, 1, 0xdb, 255, 255], data, replies);
        Assert.Equal("█\u00a0", Encoding.UTF8.GetString(codec.DecodeOutput(data.ToArray())));
        Assert.Equal(new byte[] { 255, 255 }, protocol.EncodeInput(codec.EncodeInput("\u00a0"u8)));
    }

    [Theory]
    [InlineData(TelnetTextEncoding.Auto)]
    [InlineData(TelnetTextEncoding.Utf8)]
    [InlineData(TelnetTextEncoding.Cp437)]
    public void SessionEncoding_RoundTrips(TelnetTextEncoding encoding)
    {
        var session = new Session { Kind = SessionKind.Telnet, TelnetEncoding = encoding };
        Assert.Equal(encoding, JsonSerializer.Deserialize<Session>(JsonSerializer.Serialize(session))!.TelnetEncoding);
        Assert.Equal(TelnetTextEncoding.Auto, JsonSerializer.Deserialize<Session>("{}")!.TelnetEncoding);
    }

    private static string DecodeChunks(byte[] bytes, int size)
    {
        var codec = new TelnetTextCodec(TelnetTextEncoding.Auto);
        var output = new List<byte>();
        for (var offset = 0; offset < bytes.Length; offset += size)
            output.AddRange(codec.DecodeOutput(bytes.AsSpan(offset, Math.Min(size, bytes.Length - offset))));
        output.AddRange(codec.DecodeOutput([], flush: true));
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }
}
