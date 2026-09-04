using System.Buffers;
using System.Text;
using Resesh.Terminal;

namespace Resesh.Terminal.Tests;

public sealed class TerminalOutputBufferTests
{
    [Fact]
    public void LargeWritesRemainOrderedAndBoundedAcrossPartialDrains()
    {
        using var buffer = new TerminalOutputBuffer();
        var data = new byte[TerminalOutputBuffer.BlockSize * 3 + 19];
        new Random(42).NextBytes(data);
        buffer.Write(data.AsSpan(0, TerminalOutputBuffer.BlockSize + 7));
        var first = buffer.Read(out var count)!;
        Assert.Equal(TerminalOutputBuffer.BlockSize, count);
        Assert.Equal(data[..count], first[..count]);
        ArrayPool<byte>.Shared.Return(first);
        buffer.Write(data.AsSpan(TerminalOutputBuffer.BlockSize + 7));
        var offset = count;
        while (buffer.HasData)
        {
            var block = buffer.Read(out count)!;
            Assert.InRange(count, 1, TerminalOutputBuffer.BlockSize);
            Assert.Equal(data[offset..(offset + count)], block[..count]);
            offset += count;
            ArrayPool<byte>.Shared.Return(block);
        }
        Assert.Equal(data.Length, offset);
        Assert.Null(buffer.Read(out count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void Utf8AcrossBlockBoundaryAndBufferReuseIsLossless()
    {
        using var buffer = new TerminalOutputBuffer();
        var decoder = new SnapshotUtf8Decoder();
        var expected = new string('a', TerminalOutputBuffer.BlockSize - 1) + "😀\u001b[31mred";
        buffer.Write(Encoding.UTF8.GetBytes(expected));
        var result = new StringBuilder();
        while (buffer.HasData)
        {
            var block = buffer.Read(out var count)!;
            result.Append(decoder.Decode(block.AsSpan(0, count)));
            ArrayPool<byte>.Shared.Return(block);
        }
        Assert.Equal(expected, result.ToString());
        buffer.Write("again"u8);
        buffer.Dispose();
        Assert.False(buffer.HasData);
        buffer.Write("last"u8);
        var last = buffer.Read(out var length)!;
        Assert.Equal("last", decoder.Decode(last.AsSpan(0, length)));
        ArrayPool<byte>.Shared.Return(last);
    }
}
