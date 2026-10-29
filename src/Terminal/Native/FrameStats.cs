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
        "read_us,prep_us,record_us,enddraw_us,copy_us,present_us,total_us,parsed_bytes,parse_us," +
        "annotations_us,keyframe_us,gc_pause_us,gc0,gc1,gc2,alloc_kb";

    private static readonly object Gate = new();
    private static readonly StringBuilder Frames = new();
    private static readonly StringBuilder Gpu = new();
    private static readonly StringBuilder Ui = new();
    private static string? s_uiPath;
    private static int s_uiProbeStarted;
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
            s_uiPath = Path.Combine(dir, $"ui-{pid}.csv");
            File.WriteAllText(s_uiPath, "t_ms,lag_us\n");
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

    /// <summary>
    /// Measures how long the UI thread takes to get to new work, which is what typing and
    /// clicking feel like: every 10 ms a background thread posts a timestamped callback and
    /// logs how late it ran to <c>ui-&lt;pid&gt;.csv</c>. One probe per process.
    /// </summary>
    public static void StartUiProbe(Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        if (!Enabled || Interlocked.Exchange(ref s_uiProbeStarted, 1) == 1)
            return;
        var thread = new Thread(() =>
        {
            while (true)
            {
                var posted = Now();
                queue.TryEnqueue(() =>
                {
                    var ran = Now();
                    var line = string.Create(CultureInfo.InvariantCulture,
                        $"{Microseconds(Start, posted) / 1000:F3},{Microseconds(posted, ran):F1}\n");
                    lock (Gate)
                        Ui.Append(line);
                });
                Thread.Sleep(10);
            }
        }) { IsBackground = true, Name = "UI lag probe" };
        thread.Start();
    }

    public static int NextSurfaceId() => Interlocked.Increment(ref s_nextSurface);

    public static long Now() => Stopwatch.GetTimestamp();

    public static double Microseconds(long from, long to) => (to - from) * 1_000_000.0 / Stopwatch.Frequency;

    private static TimeSpan s_lastPause;
    private static int s_lastGen0, s_lastGen1, s_lastGen2;
    private static long s_lastAllocated;

    /// <summary>Process-wide GC activity since the previous frame of any surface.</summary>
    private static (double PauseUs, int Gen0, int Gen1, int Gen2, long AllocatedKb) GcSincePrevious()
    {
        var pause = GC.GetTotalPauseDuration();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        var allocated = GC.GetTotalAllocatedBytes(false);
        var result = ((pause - s_lastPause).TotalMicroseconds, gen0 - s_lastGen0, gen1 - s_lastGen1, gen2 - s_lastGen2,
            (allocated - s_lastAllocated) / 1024);
        (s_lastPause, s_lastGen0, s_lastGen1, s_lastGen2, s_lastAllocated) = (pause, gen0, gen1, gen2, allocated);
        return result;
    }

    public static void Frame(in FrameRecord r)
    {
        // Render threads of several surfaces report concurrently.
        (double PauseUs, int Gen0, int Gen1, int Gen2, long AllocatedKb) gc;
        lock (Gate)
            gc = GcSincePrevious();
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{r.Surface},{r.Frame},{Microseconds(Start, r.Started) / 1000:F3},{r.IntervalMs:F3},{r.Columns},{r.Rows},{r.RowsDrawn},{(r.Full ? 1 : 0)},{r.BufferWidth},{r.BufferHeight}," +
            $"{r.ReadUs:F1},{r.PrepUs:F1},{r.RecordUs:F1},{r.EndDrawUs:F1},{r.CopyUs:F1},{r.PresentUs:F1},{r.TotalUs:F1},{r.ParsedBytes},{r.ParseUs:F1}," +
            $"{r.AnnotationsUs:F1},{r.KeyframeUs:F1},{gc.PauseUs:F1},{gc.Gen0},{gc.Gen1},{gc.Gen2},{gc.AllocatedKb}\n");
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
        string frames, gpu, ui;
        lock (Gate)
        {
            if (Frames.Length == 0 && Gpu.Length == 0 && Ui.Length == 0)
                return;
            frames = Frames.ToString();
            gpu = Gpu.ToString();
            ui = Ui.ToString();
            Frames.Clear();
            Gpu.Clear();
            Ui.Clear();
        }
        try
        {
            File.AppendAllText(s_framesPath!, frames);
            File.AppendAllText(s_gpuPath!, gpu);
            File.AppendAllText(s_uiPath!, ui);
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
    public double RecordUs;
    public double EndDrawUs;
    public double CopyUs;
    public double PresentUs;
    public double TotalUs;
    public long ParsedBytes;
    public double ParseUs;
    public double AnnotationsUs;
    public double KeyframeUs;
}
