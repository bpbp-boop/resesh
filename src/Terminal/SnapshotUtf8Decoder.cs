using System.Buffers;
using System.Text;

namespace Resesh.Terminal;

/// <summary>Incremental UTF-8 decoder whose only mutable state is a portable 0-3 byte prefix.</summary>
internal sealed class SnapshotUtf8Decoder
{
    private readonly byte[] _pending = new byte[3];
    private int _pendingLength;

    internal byte[] CapturePending() => _pending.AsSpan(0, _pendingLength).ToArray();

    internal static bool IsValidPending(ReadOnlySpan<byte> value) =>
        value.Length <= 3 && (value.IsEmpty || IncompleteSuffixLength(value) == value.Length);

    internal void RestorePending(ReadOnlySpan<byte> value)
    {
        if (!IsValidPending(value))
            throw new InvalidDataException("The pending UTF-8 decoder state is invalid.");
        value.CopyTo(_pending);
        _pendingLength = value.Length;
    }

    internal string Decode(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return string.Empty;
        if (_pendingLength == 0)
        {
            var trailing = IncompleteSuffixLength(value);
            if (trailing > 0)
                value[^trailing..].CopyTo(_pending);
            _pendingLength = trailing;
            return Encoding.UTF8.GetString(value[..^trailing]);
        }
        var length = checked(_pendingLength + value.Length);
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            _pending.AsSpan(0, _pendingLength).CopyTo(rented);
            value.CopyTo(rented.AsSpan(_pendingLength));
            var combined = rented.AsSpan(0, length);
            var trailing = IncompleteSuffixLength(combined);
            var decoded = Encoding.UTF8.GetString(combined[..^trailing]);
            if (trailing > 0)
                combined[^trailing..].CopyTo(_pending);
            _pendingLength = trailing;
            return decoded;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int IncompleteSuffixLength(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return 0;
        var start = value.Length - 1;
        while (start >= 0 && IsContinuation(value[start]) && value.Length - start <= 3)
            start--;
        if (start < 0)
            return 0;

        var expected = SequenceLength(value[start]);
        if (expected == 0)
            return 0;
        var available = value.Length - start;
        if (available >= expected)
            return 0;
        for (var index = start + 1; index < value.Length; index++)
        {
            if (!IsContinuation(value[index]))
                return 0;
        }
        if (available >= 2 && !IsValidSecondByte(value[start], value[start + 1]))
            return 0;
        return available;
    }

    private static int SequenceLength(byte value) => value switch
    {
        >= 0xC2 and <= 0xDF => 2,
        >= 0xE0 and <= 0xEF => 3,
        >= 0xF0 and <= 0xF4 => 4,
        _ => 0,
    };

    private static bool IsContinuation(byte value) => value is >= 0x80 and <= 0xBF;

    private static bool IsValidSecondByte(byte first, byte second) => first switch
    {
        0xE0 => second is >= 0xA0 and <= 0xBF,
        0xED => second is >= 0x80 and <= 0x9F,
        0xF0 => second is >= 0x90 and <= 0xBF,
        0xF4 => second is >= 0x80 and <= 0x8F,
        _ => IsContinuation(second),
    };
}
