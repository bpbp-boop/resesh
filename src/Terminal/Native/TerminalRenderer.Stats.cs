using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace Resesh.Terminal.Native;

/// <summary>Frame timings for <see cref="FrameStats"/>. Only touched while it is enabled.</summary>
internal sealed unsafe partial class TerminalRenderer
{
    // GPU timestamps resolve a few frames late; a slot still pending when its turn comes
    // around again is left alone and that frame goes untimed.
    private const int GpuSlots = 8;
    private GpuQuery[]? _gpuQueries;

    public int StatsSurface { get; set; }
    public long FrameNumber { get; private set; }
    public int BufferWidth => _bufferWidth;
    public int BufferHeight => _bufferHeight;

    /// <summary>The last successful <see cref="Draw"/>: rows repainted and how long each step
    /// held the UI thread.</summary>
    public (int RowsDrawn, double RecordUs, double EndDrawUs, double CopyUs, double PresentUs) LastTimings { get; private set; }

    private sealed class GpuQuery
    {
        public required ID3D11Query Disjoint;
        public required ID3D11Query Begin;
        public required ID3D11Query End;
        public long Frame;
        public bool Pending;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct TimestampDisjoint
    {
        public ulong Frequency;
        public int Disjoint;
    }

    /// <summary>Opens GPU timing for the frame about to be drawn; returns the slot, or -1.</summary>
    private int BeginGpuTiming()
    {
        HarvestGpuTimings();
        if (s_device is null || s_context is null)
            return -1;
        if (_gpuQueries is null)
        {
            _gpuQueries = new GpuQuery[GpuSlots];
            for (var i = 0; i < GpuSlots; i++)
                _gpuQueries[i] = new GpuQuery
                {
                    Disjoint = s_device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint)),
                    Begin = s_device.CreateQuery(new QueryDescription(QueryType.Timestamp)),
                    End = s_device.CreateQuery(new QueryDescription(QueryType.Timestamp)),
                };
        }
        var slot = (int)(FrameNumber % GpuSlots);
        var query = _gpuQueries[slot];
        if (query.Pending)
            return -1;
        // Direct2D records into the device's immediate context, so these bracket its work
        // once EndDraw flushes it, plus the canvas copy.
        s_context.Begin(query.Disjoint);
        s_context.End(query.Begin);
        return slot;
    }

    private void EndGpuTiming(int slot)
    {
        if (slot < 0 || _gpuQueries is null || s_context is null)
            return;
        var query = _gpuQueries[slot];
        s_context.End(query.End);
        s_context.End(query.Disjoint);
        query.Frame = FrameNumber;
        query.Pending = true;
    }

    private void HarvestGpuTimings()
    {
        if (_gpuQueries is null || s_context is null)
            return;
        foreach (var query in _gpuQueries)
        {
            if (!query.Pending)
                continue;
            TimestampDisjoint disjoint;
            ulong begin, end;
            if (s_context.GetData(query.Disjoint, (IntPtr)(&disjoint), (uint)sizeof(TimestampDisjoint), AsyncGetDataFlags.DoNotFlush).Code != 0
                || s_context.GetData(query.Begin, (IntPtr)(&begin), sizeof(ulong), AsyncGetDataFlags.DoNotFlush).Code != 0
                || s_context.GetData(query.End, (IntPtr)(&end), sizeof(ulong), AsyncGetDataFlags.DoNotFlush).Code != 0)
                continue;
            query.Pending = false;
            if (disjoint.Disjoint == 0 && disjoint.Frequency > 0 && end >= begin)
                FrameStats.GpuTime(StatsSurface, query.Frame, (end - begin) * 1_000_000.0 / disjoint.Frequency);
        }
    }

    private void DisposeGpuQueries()
    {
        if (_gpuQueries is null)
            return;
        foreach (var query in _gpuQueries)
        {
            query.Disjoint.Dispose();
            query.Begin.Dispose();
            query.End.Dispose();
        }
        _gpuQueries = null;
    }
}
