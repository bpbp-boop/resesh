using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using GhosttyVtBench;

// Feeds the shared terminal-bench workloads through libghostty-vt and a Direct2D/DirectWrite
// renderer, in the same 32 KiB batches the WebView harness uses.
//
//   parse     VT core only: vt_write every batch, no render state, no drawing.
//   single    One thread does both, like the WebView main thread: write batches, and whenever
//             a 60 Hz frame is due, update the render state and draw dirty rows.
//   threaded  An IO thread writes under a lock; the render thread ticks at 60 Hz, holds the
//             lock only for begin_update, then reads and draws outside it.
//
// usage: GhosttyVtBench --native <dir> --workloads <dir> --out <dir>
//        [--wl logs,color,...] [--modes parse,single,threaded] [--reps 3] [--cols 170] [--rows 50]

internal static unsafe class Program
{
    private static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        Native.Configure(opts["native"]);
        Native.timeBeginPeriod(1);
        string wlDir = opts["workloads"], outDir = opts["out"];
        Directory.CreateDirectory(outDir);
        var workloads = opts.GetValueOrDefault("wl", "logs,color,tiny,tui,unicode").Split(',');
        var modes = opts.GetValueOrDefault("modes", "parse,single,threaded").Split(',');
        int reps = int.Parse(opts.GetValueOrDefault("reps", "3"));
        ushort cols = ushort.Parse(opts.GetValueOrDefault("cols", "170")), rows = ushort.Parse(opts.GetValueOrDefault("rows", "50"));
        const int Batch = 32768;
        var frameInterval = TimeSpan.FromSeconds(1 / 60.0);
        double rateMBps = double.Parse(opts.GetValueOrDefault("rate", "2"));
        double pacedSeconds = double.Parse(opts.GetValueOrDefault("seconds", "3"));

        double cyclesPerMs = Native.CalibrateCyclesPerMs();
        using var renderer = new Renderer(cols, rows, "Cascadia Mono", 14);
        Console.WriteLine($"env cols={cols} rows={rows} cell={renderer.CellWidth}x{renderer.CellHeight} px={renderer.Width}x{renderer.Height} gpu={renderer.AdapterName}");

        var cells = (RvtCell*)NativeMemory.AllocZeroed((nuint)(cols * rows * sizeof(RvtCell)));
        var dirty = (byte*)NativeMemory.AllocZeroed(rows);
        var reader = Native.rvt_reader_new();
        var all = new Dictionary<string, object>();

        foreach (var wl in workloads)
        {
            var fileBytes = File.ReadAllBytes(Path.Combine(wlDir, wl + ".bin"));
            foreach (var mode in modes)
            {
                // Paced runs replay a prefix at a fixed rate, like a fast remote command.
                var bytes = mode == "paced"
                    ? fileBytes[..(int)Math.Min(fileBytes.Length, rateMBps * pacedSeconds * 1048576)]
                    : fileBytes;
                var runs = new List<Result>();
                for (int r = 0; r < reps; r++)
                    runs.Add(Run(mode, bytes, r == reps - 1 ? Path.Combine(outDir, $"native-{wl}-{mode}.png") : null));
                // Paced wall time is fixed by the schedule; report the median-CPU run instead.
                var best = mode == "paced"
                    ? runs.OrderBy(x => x.CpuMs).ElementAt(runs.Count / 2)
                    : runs.MinBy(x => x.TotalMs)!;
                double mbps = bytes.Length / 1048576.0 / (best.TotalMs / 1000);
                var summary = new
                {
                    bytes = bytes.Length, best.TotalMs, best.LongestBlockMs, best.Frames, best.AvgFrameMs, best.MaxFrameMs,
                    best.MaxLockWaitMs, best.AllocMB, best.EncodeMs, best.DecodeMs, best.SnapshotKB, best.ScrollbackRows, best.ScrollbackMaxLines, best.CpuMs, best.IoBusyMs, best.RenderBusyMs,
                    cpuPerSec = Math.Round(best.CpuMs / (best.TotalMs / 1000), 1), mbps = Math.Round(mbps, 2), all = runs.Select(x => Math.Round(x.TotalMs, 1)), cpuAll = runs.Select(x => x.CpuMs),
                };
                all[$"{wl}/{mode}"] = summary;
                Console.WriteLine($"native {wl,-8} {mode,-9} {JsonSerializer.Serialize(summary)}");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "native-results.json"), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        return 0;

        Result Run(string mode, byte[] bytes, string? png)
        {
            Native.ghostty_terminal_new(IntPtr.Zero, out var term, cols, rows).Check("terminal_new");
            nuint scrollback = 10000;
            Native.ghostty_terminal_set(term, 28 /* SCROLLBACK_MAX_LINES */, &scrollback).Check("scrollback");
            // The default byte cap keeps only a few hundred rows of a 170-column log; drop it so the
            // 10,000-line limit governs, matching xterm's scrollback: 10000.
            Native.ghostty_terminal_set(term, 27 /* SCROLLBACK_MAX_BYTES */, null).Check("scrollback bytes");
            Native.ghostty_terminal_resize(term, cols, rows, (uint)renderer.CellWidth, (uint)renderer.CellHeight).Check("resize");
            Native.ghostty_render_state_new(IntPtr.Zero, out var rs).Check("render_state_new");
            int lastCursorRow = -1;
            var res = new Result();
            long alloc0 = GC.GetTotalAllocatedBytes(true);
            var frameTimes = new List<double>();

            void Frame(bool update)
            {
                var f0 = Stopwatch.GetTimestamp();
                if (update) Native.ghostty_render_state_update(rs, term).Check("update");
                RvtFrameInfo info;
                Native.rvt_read_frame(reader, rs, cells, cols, rows, dirty, &info);
                // A clean render state with the cursor where it was needs no drawing.
                if (info.Dirty != 0 || info.CursorY != lastCursorRow)
                    renderer.DrawFrame(cells, dirty, info, ref lastCursorRow);
                frameTimes.Add(Stopwatch.GetElapsedTime(f0).TotalMilliseconds);
            }

            // Warm the frame path (glyph caches, D2D state) outside the timed region.
            Frame(update: true);
            frameTimes.Clear();

            var cpu0 = Native.ProcessCycles();
            var t0 = Stopwatch.GetTimestamp();
            fixed (byte* p = bytes)
            {
                var data = p;
                if (mode == "parse")
                {
                    for (int o = 0; o < bytes.Length; o += Batch)
                    {
                        var s = Stopwatch.GetTimestamp();
                        Native.ghostty_terminal_vt_write(term, data + o, (nuint)Math.Min(Batch, bytes.Length - o));
                        res.LongestBlockMs = Math.Max(res.LongestBlockMs, Stopwatch.GetElapsedTime(s).TotalMilliseconds);
                    }
                }
                else if (mode == "single")
                {
                    var lastFrame = t0;
                    for (int o = 0; o < bytes.Length; o += Batch)
                    {
                        var s = Stopwatch.GetTimestamp();
                        Native.ghostty_terminal_vt_write(term, data + o, (nuint)Math.Min(Batch, bytes.Length - o));
                        if (Stopwatch.GetElapsedTime(lastFrame) >= frameInterval)
                        {
                            Frame(update: true);
                            lastFrame = Stopwatch.GetTimestamp();
                        }
                        res.LongestBlockMs = Math.Max(res.LongestBlockMs, Stopwatch.GetElapsedTime(s).TotalMilliseconds);
                    }
                    var last = Stopwatch.GetTimestamp();
                    Frame(update: true);
                    res.LongestBlockMs = Math.Max(res.LongestBlockMs, Stopwatch.GetElapsedTime(last).TotalMilliseconds);
                }
                else if (mode is "threaded" or "paced")
                {
                    bool paced = mode == "paced";
                    double ioBusy = 0;
                    var gate = new object();
                    bool done = false;
                    double maxWait = 0, longestWrite = 0;
                    var io = new Thread(() =>
                    {
                        for (int o = 0; o < bytes.Length; o += Batch)
                        {
                            if (paced)
                            {
                                var due = t0 + (long)(o / (rateMBps * 1048576) * Stopwatch.Frequency);
                                var ahead = due - Stopwatch.GetTimestamp();
                                if (ahead > 0) Thread.Sleep(TimeSpan.FromSeconds((double)ahead / Stopwatch.Frequency));
                            }
                            var w = Stopwatch.GetTimestamp();
                            lock (gate)
                            {
                                maxWait = Math.Max(maxWait, Stopwatch.GetElapsedTime(w).TotalMilliseconds);
                                var s = Stopwatch.GetTimestamp();
                                Native.ghostty_terminal_vt_write(term, data + o, (nuint)Math.Min(Batch, bytes.Length - o));
                                var took = Stopwatch.GetElapsedTime(s).TotalMilliseconds;
                                longestWrite = Math.Max(longestWrite, took);
                                ioBusy += took;
                            }
                        }
                        Volatile.Write(ref done, true);
                    }) { IsBackground = true, Name = "vt-io" };
                    io.Start();
                    var next = Stopwatch.GetTimestamp();
                    while (true)
                    {
                        bool finished = Volatile.Read(ref done);
                        lock (gate) Native.ghostty_render_state_begin_update(rs, term).Check("begin");
                        Native.ghostty_render_state_end_update(rs).Check("end");
                        Frame(update: false);
                        if (finished) break;
                        next += (long)(frameInterval.TotalSeconds * Stopwatch.Frequency);
                        var wait = next - Stopwatch.GetTimestamp();
                        if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency));
                        else next = Stopwatch.GetTimestamp();
                    }
                    io.Join();
                    res.MaxLockWaitMs = Math.Round(maxWait, 2);
                    // The UI thread never parses; its longest block is its longest frame.
                    res.LongestBlockMs = frameTimes.Count > 0 ? frameTimes.Max() : 0;
                    res.LongestWriteMs = longestWrite;
                    res.IoBusyMs = Math.Round(ioBusy, 1);
                }
                else if (mode == "snapshot")
                {
                    // Rewind keyframe cost: full-state encode and restore after the whole workload
                    // (10,000 lines of scrollback). Timed region covers only encode + decode.
                    for (int o = 0; o < bytes.Length; o += Batch)
                        Native.ghostty_terminal_vt_write(term, data + o, (nuint)Math.Min(Batch, bytes.Length - o));
                    nuint scrollbackRows = 0, maxLines = 0;
                    Native.ghostty_terminal_get(term, 15 /* SCROLLBACK_ROWS */, &scrollbackRows);
                    Native.ghostty_terminal_get(term, 35 /* SCROLLBACK_MAX_LINES */, &maxLines);
                    res.ScrollbackRows = (long)scrollbackRows;
                    res.ScrollbackMaxLines = (long)maxLines;
                    t0 = Stopwatch.GetTimestamp();
                    Native.ghostty_snapshot_encode_buf(term, null, 0, out var need);
                    var buf = new byte[(int)need];
                    nuint written;
                    var e0 = Stopwatch.GetTimestamp();
                    fixed (byte* b = buf) Native.ghostty_snapshot_encode_buf(term, b, need, out written).Check("encode");
                    res.EncodeMs = Math.Round(Stopwatch.GetElapsedTime(e0).TotalMilliseconds, 2);
                    res.SnapshotKB = Math.Round(written / 1024.0, 1);
                    var d0 = Stopwatch.GetTimestamp();
                    fixed (byte* b = buf)
                    {
                        Native.ghostty_snapshot_decoder_new_buf(IntPtr.Zero, out var dec, b, written).Check("decoder");
                        Native.ghostty_snapshot_decoder_decode(dec, out var restored).Check("decode");
                        Native.ghostty_snapshot_decoder_free(dec);
                        Native.ghostty_terminal_free(restored);
                    }
                    res.DecodeMs = Math.Round(Stopwatch.GetElapsedTime(d0).TotalMilliseconds, 2);
                }
                else throw new ArgumentException("unknown mode " + mode);
            }
            if (mode is not ("parse" or "snapshot")) renderer.WaitForGpu();
            res.TotalMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            res.CpuMs = Math.Round((Native.ProcessCycles() - cpu0) / cyclesPerMs, 1);
            res.RenderBusyMs = Math.Round(frameTimes.Sum(), 1);
            res.AllocMB = Math.Round((GC.GetTotalAllocatedBytes(true) - alloc0) / 1048576.0, 1);
            res.Frames = frameTimes.Count;
            res.AvgFrameMs = frameTimes.Count > 0 ? Math.Round(frameTimes.Average(), 2) : 0;
            res.MaxFrameMs = frameTimes.Count > 0 ? Math.Round(frameTimes.Max(), 2) : 0;
            res.LongestBlockMs = Math.Round(res.LongestBlockMs, 2);
            res.TotalMs = Math.Round(res.TotalMs, 1);
            if (png is not null && mode is not ("parse" or "snapshot")) renderer.SavePng(png);
            Native.ghostty_render_state_free(rs);
            Native.ghostty_terminal_free(term);
            return res;
        }
    }

    static Dictionary<string, string> ParseArgs(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < a.Length; i += 2) d[a[i].TrimStart('-')] = a[i + 1];
        foreach (var k in new[] { "native", "workloads", "out" })
            if (!d.ContainsKey(k)) throw new ArgumentException("missing --" + k);
        return d;
    }
}

internal sealed class Result
{
    public double TotalMs { get; set; }
    public double LongestBlockMs { get; set; }
    public double LongestWriteMs { get; set; }
    public int Frames { get; set; }
    public double AvgFrameMs { get; set; }
    public double MaxFrameMs { get; set; }
    public double MaxLockWaitMs { get; set; }
    public double AllocMB { get; set; }
    public double CpuMs { get; set; }
    public double EncodeMs { get; set; }
    public double DecodeMs { get; set; }
    public double SnapshotKB { get; set; }
    public long ScrollbackRows { get; set; }
    public long ScrollbackMaxLines { get; set; }
    public double IoBusyMs { get; set; }
    public double RenderBusyMs { get; set; }
}

internal static class ResultCheck
{
    public static void Check(this int result, string what)
    {
        if (result != 0) throw new InvalidOperationException($"{what} failed: {result}");
    }
}
