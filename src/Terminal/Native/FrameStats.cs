using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Resesh.Terminal.Native;

/// <summary>
/// Per-frame renderer timings for benchmarks. Off unless the <c>RESESH_FRAME_STATS</c>
/// environment variable names a folder; then the process writes <c>frames-&lt;pid&gt;.csv</c>
/// (CPU side, one row per drawn frame) and <c>gpu-&lt;pid&gt;.csv</c> (GPU time from timestamp
/// queries, read back a few frames later) there. <c>tools/terminal-bench/framestats.py</c>
/// summarizes them. When off, every hook is a single static bool check.
/// </summary>
internal static class FrameStats
{
    public static readonly bool Enabled;

    private const string FrameHeader =
        "surface,frame,t_ms,interval_ms,cols,rows,rows_drawn,full,buf_w,buf_h," +
        "read_us,prep_us,input_us,scroll_us,record_us,enddraw_us,copy_us,present_us,total_us,parsed_bytes,parse_us";

    private static readonly object Gate = new();
    private static readonly StringBuilder Frames = new();
    private static readonly StringBuilder Gpu = new();
    private static readonly long Start = Stopwatch.GetTimestamp();
    private static string? s_framesPath;
    private static string? s_gpuPath;
    private static Timer? s_flushTimer;
    private static int s_nextSurface;

    static FrameStats()
    {
        var dir = Environment.GetEnvironmentVariable("RESESH_FRAME_STATS");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        try
        {
            Directory.CreateDirectory(dir);
            var pid = Environment.ProcessId;
            s_framesPath = Path.Combine(dir, $"frames-{pid}.csv");
            s_gpuPath = Path.Combine(dir, $"gpu-{pid}.csv");
            File.WriteAllText(s_framesPath, FrameHeader + "\n");
            File.WriteAllText(s_gpuPath, "surface,frame,gpu_us\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Enabled = true;
        // Benchmarks stop the app by killing it, so flush often rather than only at exit.
        s_flushTimer = new Timer(_ => Flush(), null, 250, 250);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    /// <summary>A line of run context (adapter, display) in <c>info-&lt;pid&gt;.txt</c>.</summary>
    public static void Info(string line)
    {
        if (!Enabled)
            return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(s_framesPath)!, $"info-{Environment.ProcessId}.txt"), line + "\n");
        }
        catch (IOException)
        {
        }
    }

    public static int NextSurfaceId() => Interlocked.Increment(ref s_nextSurface);

    public static long Now() => Stopwatch.GetTimestamp();

    public static double Microseconds(long from, long to) => (to - from) * 1_000_000.0 / Stopwatch.Frequency;

    public static void Frame(in FrameRecord r)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{r.Surface},{r.Frame},{Microseconds(Start, r.Started) / 1000:F3},{r.IntervalMs:F3},{r.Columns},{r.Rows},{r.RowsDrawn},{(r.Full ? 1 : 0)},{r.BufferWidth},{r.BufferHeight}," +
            $"{r.ReadUs:F1},{r.PrepUs:F1},{r.InputUs:F1},{r.ScrollUs:F1},{r.RecordUs:F1},{r.EndDrawUs:F1},{r.CopyUs:F1},{r.PresentUs:F1},{r.TotalUs:F1},{r.ParsedBytes},{r.ParseUs:F1}\n");
        lock (Gate)
            Frames.Append(line);
    }

    public static void GpuTime(int surface, long frame, double microseconds)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{surface},{frame},{microseconds:F1}\n");
        lock (Gate)
            Gpu.Append(line);
    }

    private static void Flush()
    {
        string frames, gpu;
        lock (Gate)
        {
            if (Frames.Length == 0 && Gpu.Length == 0)
                return;
            frames = Frames.ToString();
            gpu = Gpu.ToString();
            Frames.Clear();
            Gpu.Clear();
        }
        try
        {
            File.AppendAllText(s_framesPath!, frames);
            File.AppendAllText(s_gpuPath!, gpu);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>One drawn frame. Times are microseconds of UI-thread wall time unless named
/// otherwise; parse figures cover the output parsed since the surface's previous frame.</summary>
internal struct FrameRecord
{
    public int Surface;
    public long Frame;
    public long Started;
    public double IntervalMs;
    public int Columns;
    public int Rows;
    public int RowsDrawn;
    public bool Full;
    public int BufferWidth;
    public int BufferHeight;
    public double ReadUs;
    public double PrepUs;
    public double InputUs;
    public double ScrollUs;
    public double RecordUs;
    public double EndDrawUs;
    public double CopyUs;
    public double PresentUs;
    public double TotalUs;
    public long ParsedBytes;
    public double ParseUs;
}
