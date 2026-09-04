using System.Buffers;

namespace Resesh.Terminal;

/// <summary>FIFO output blocks. The owner serializes access and returns read blocks to the pool.</summary>
internal sealed class TerminalOutputBuffer : IDisposable
{
    internal const int BlockSize = 64 * 1024;
    private readonly Queue<byte[]> _blocks = new();
    private byte[]? _tail;
    private int _tailLength;

    internal bool HasData => _tail is not null;

    internal void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_tail is null || _tailLength == BlockSize)
            {
                _tail = ArrayPool<byte>.Shared.Rent(BlockSize);
                _blocks.Enqueue(_tail);
                _tailLength = 0;
            }
            var count = Math.Min(data.Length, BlockSize - _tailLength);
            data[..count].CopyTo(_tail.AsSpan(_tailLength));
            _tailLength += count;
            data = data[count..];
        }
    }

    internal byte[]? Read(out int count)
    {
        if (!_blocks.TryDequeue(out var block))
        {
            count = 0;
            return null;
        }
        count = ReferenceEquals(block, _tail) ? _tailLength : BlockSize;
        if (_blocks.Count == 0)
        {
            _tail = null;
            _tailLength = 0;
        }
        return block;
    }

    public void Dispose()
    {
        while (_blocks.TryDequeue(out var block))
            ArrayPool<byte>.Shared.Return(block);
        _tail = null;
        _tailLength = 0;
    }
}
