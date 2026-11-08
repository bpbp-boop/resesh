using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Resesh.Core.Recording;

/// <summary>
/// A piece of rewind data that is kept raw when captured and compressed later on a
/// background thread. Its payload is swapped in one reference write, so a snapshot can read
/// it outside the capture's lock and decompress there without holding up terminal output.
/// </summary>
internal abstract class RewindChunk
{
    /// <summary>What the chunk counts against the rewind cap now. Changed under the capture's lock.</summary>
    public long Bytes { get; protected set; }

    /// <summary>Trimmed while waiting for compression: the compressor skips it.</summary>
    public bool Dropped { get; set; }

    /// <summary>The compressed payload, or null when compression would not save space.
    /// Called outside the lock, only by the compressor.</summary>
    public abstract object? Compress();

    /// <summary>Swaps in what <see cref="Compress"/> returned; returns the change in
    /// <see cref="Bytes"/>. Called under the capture's lock.</summary>
    public abstract long UseCompressed(object compressed);
}

/// <summary>Brotli settings for rewind. Quality 2 on 64 KB blocks compresses colored terminal
/// output about 4x at about 200 MB/s on one core, well ahead of any terminal's output rate;
/// higher qualities gain little for several times the cost.</summary>
internal static class RewindCompression
{
    private const int Quality = 2;
    private const int Window = 22;

    public static byte[]? Compress(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty)
            return null;
        var rented = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(raw.Length));
        try
        {
            if (!BrotliEncoder.TryCompress(raw, rented, out var written, Quality, Window) ||
                written >= raw.Length)
                return null;
            return rented.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static void Decompress(ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        if (!BrotliDecoder.TryDecompress(compressed, destination, out var written) ||
            written != destination.Length)
            throw new InvalidDataException("Rewind data could not be decompressed.");
    }
}

internal sealed record CompressedPayload(byte[] Data, int RawLength);

internal sealed class RewindKeyframe : RewindChunk
{
    private const long Overhead = 48;

    // byte[] while raw, CompressedPayload once compressed.
    private object _payload;

    public RewindKeyframe(double time, int columns, int rows, byte[] state)
    {
        Time = time;
        Columns = columns;
        Rows = rows;
        _payload = state;
        Bytes = Overhead + state.Length;
    }

    public double Time { get; }
    public int Columns { get; }
    public int Rows { get; }

    public TerminalKeyframe ToKeyframe()
    {
        var payload = Volatile.Read(ref _payload);
        if (payload is byte[] raw)
            return new TerminalKeyframe(Time, Columns, Rows, raw);
        var compressed = (CompressedPayload)payload;
        var state = new byte[compressed.RawLength];
        RewindCompression.Decompress(compressed.Data, state);
        return new TerminalKeyframe(Time, Columns, Rows, state);
    }

    public override object? Compress() =>
        Volatile.Read(ref _payload) is byte[] raw && RewindCompression.Compress(raw) is { } data
            ? new CompressedPayload(data, raw.Length)
            : null;

    public override long UseCompressed(object compressed)
    {
        var payload = (CompressedPayload)compressed;
        Volatile.Write(ref _payload, payload);
        var previous = Bytes;
        Bytes = Overhead + payload.Data.Length;
        return Bytes - previous;
    }
}

/// <summary>
/// Consecutive output and resize events. Blocks never span a keyframe, so trimming to a
/// keyframe drops whole blocks. Compressed, each event is its time, its type and its data
/// as UTF-8, which is also half the size of the strings it replaces before compression.
/// </summary>
internal sealed class RewindEventBlock : RewindChunk
{
    private const long Overhead = 64;

    // TerminalRecordingEvent[] while raw, CompressedPayload once compressed.
    private object _payload;

    public RewindEventBlock(TerminalRecordingEvent[] events, long bytes)
    {
        _payload = events;
        FirstTime = events[0].Time;
        LastTime = events[^1].Time;
        Bytes = bytes;
    }

    public double FirstTime { get; }
    public double LastTime { get; }

    public IReadOnlyList<TerminalRecordingEvent> Events()
    {
        var payload = Volatile.Read(ref _payload);
        return payload as TerminalRecordingEvent[] ?? Decode((CompressedPayload)payload);
    }

    public override object? Compress()
    {
        if (Volatile.Read(ref _payload) is not TerminalRecordingEvent[] events)
            return null;
        var writer = new ArrayBufferWriter<byte>(64 * 1024);
        foreach (var item in events)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(writer.GetSpan(8), item.Time);
            writer.Advance(8);
            WriteString(writer, item.Type);
            WriteString(writer, item.Data);
        }
        return RewindCompression.Compress(writer.WrittenSpan) is { } data
            ? new CompressedPayload(data, writer.WrittenCount)
            : null;
    }

    public override long UseCompressed(object compressed)
    {
        var payload = (CompressedPayload)compressed;
        Volatile.Write(ref _payload, payload);
        var previous = Bytes;
        Bytes = Overhead + payload.Data.Length;
        return Bytes - previous;
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        var span = writer.GetSpan(4 + length);
        BinaryPrimitives.WriteInt32LittleEndian(span, length);
        Encoding.UTF8.GetBytes(value, span[4..]);
        writer.Advance(4 + length);
    }

    private static TerminalRecordingEvent[] Decode(CompressedPayload payload)
    {
        var rented = ArrayPool<byte>.Shared.Rent(payload.RawLength);
        try
        {
            var raw = rented.AsSpan(0, payload.RawLength);
            RewindCompression.Decompress(payload.Data, raw);
            var events = new List<TerminalRecordingEvent>();
            while (!raw.IsEmpty)
            {
                var time = BinaryPrimitives.ReadDoubleLittleEndian(raw);
                raw = raw[8..];
                var type = ReadType(ref raw);
                events.Add(new TerminalRecordingEvent(time, type, ReadString(ref raw)));
            }
            return [.. events];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Output and resize types come back as the shared literals, not a string each.</summary>
    private static string ReadType(ref Span<byte> raw)
    {
        if (BinaryPrimitives.ReadInt32LittleEndian(raw) == 1 && raw[4] is (byte)'o' or (byte)'r')
        {
            var type = raw[4] == (byte)'o' ? "o" : "r";
            raw = raw[5..];
            return type;
        }
        return ReadString(ref raw);
    }

    private static string ReadString(ref Span<byte> raw)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(raw);
        var value = Encoding.UTF8.GetString(raw.Slice(4, length));
        raw = raw[(4 + length)..];
        return value;
    }
}
