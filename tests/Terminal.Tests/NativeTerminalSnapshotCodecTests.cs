using System.Buffers.Binary;
using System.Text;
using Resesh.Terminal;

namespace Resesh.Terminal.Tests;

public sealed class NativeTerminalSnapshotCodecTests
{
    [Fact]
    public void EnvelopeRoundTripsOpaqueStateAndPlaybackRules()
    {
        var rules = new[]
        {
            new NativeTerminalApi.HighlightRulePayload(
                42, "error|warning", true, true, true, 0xff112233, 0xff445566, 7),
        };

        var encoded = NativeTerminalSnapshotCodec.Encode(
            [1, 3, 3, 7],
            "terminal-build",
            [0xf0, 0x9f],
            "needle",
            searchCaseSensitive: true,
            searchRegularExpression: false,
            rules);
        var decoded = NativeTerminalSnapshotCodec.Decode(encoded);

        Assert.Equal("terminal-build", decoded.BuildId);
        Assert.Equal(new byte[] { 1, 3, 3, 7 }, decoded.NativeSnapshot);
        Assert.Equal(new byte[] { 0xf0, 0x9f }, decoded.PendingUtf8);
        Assert.Equal("needle", decoded.SearchQuery);
        Assert.True(decoded.SearchCaseSensitive);
        Assert.False(decoded.SearchRegularExpression);
        Assert.Equal(rules, decoded.HighlightRules);
    }

    [Fact]
    public void NewerMinorSkipsUnknownLengthDelimitedField()
    {
        var original = NativeTerminalSnapshotCodec.Encode(
            [9], "build", [], "", false, false, []);
        var extended = new byte[original.Length + 11];
        original.CopyTo(extended, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(extended.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(extended.AsSpan(original.Length), 0x7fff);
        BinaryPrimitives.WriteUInt16LittleEndian(extended.AsSpan(original.Length + 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(original.Length + 4), 3);
        extended[^3] = 1;
        extended[^2] = 2;
        extended[^1] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(12), (uint)(extended.Length - 24));
        BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(16), Crc32(extended.AsSpan(24)));

        var decoded = NativeTerminalSnapshotCodec.Decode(extended);

        Assert.Equal("build", decoded.BuildId);
        Assert.Equal(new byte[] { 9 }, decoded.NativeSnapshot);
    }

    [Fact]
    public void RejectsMajorChecksumTruncationAndOversizeFailures()
    {
        var valid = NativeTerminalSnapshotCodec.Encode([1], "build", [], "", false, false, []);

        var wrongMajor = valid.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(wrongMajor.AsSpan(4), 2);
        Assert.Throws<InvalidDataException>(() => NativeTerminalSnapshotCodec.Decode(wrongMajor));

        var nonzeroReserved = valid.ToArray();
        nonzeroReserved[20] = 1;
        Assert.Throws<InvalidDataException>(() => NativeTerminalSnapshotCodec.Decode(nonzeroReserved));

        var missingFeatures = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(missingFeatures.AsSpan(8), 0);
        Assert.Throws<InvalidDataException>(() => NativeTerminalSnapshotCodec.Decode(missingFeatures));

        var corrupt = valid.ToArray();
        corrupt[^1] ^= 0xff;
        Assert.Throws<InvalidDataException>(() => NativeTerminalSnapshotCodec.Decode(corrupt));

        Assert.Throws<InvalidDataException>(() => NativeTerminalSnapshotCodec.Decode(valid.AsSpan(0, valid.Length - 1)));
        Assert.Throws<InvalidDataException>(() => NativeTerminalSnapshotCodec.Decode(new byte[32 * 1024 * 1024 + 1]));
        Assert.Throws<InvalidDataException>(() =>
            NativeTerminalSnapshotCodec.Encode([1], "build", [0xf0, 0x9f, 0x92, 0xa9], "", false, false, []));
    }

    [Fact]
    public void DecoderStateRestoresAcrossPartialUtf8Character()
    {
        var source = new SnapshotUtf8Decoder();
        Assert.Equal(string.Empty, source.Decode([0xf0, 0x9f]));
        var pending = source.CapturePending();

        var restored = new SnapshotUtf8Decoder();
        restored.RestorePending(pending);

        Assert.Equal("💩", restored.Decode([0x92, 0xa9]));
        Assert.Empty(restored.CapturePending());
        Assert.Throws<InvalidDataException>(() => restored.RestorePending([0x80]));
    }

    private static uint Crc32(ReadOnlySpan<byte> value)
    {
        var crc = uint.MaxValue;
        foreach (var item in value)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
