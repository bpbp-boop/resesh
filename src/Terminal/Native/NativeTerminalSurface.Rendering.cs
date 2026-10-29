using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Resesh.Terminal.Native;

/// <summary>
/// Frames are drawn on the surface's own render thread, as Ghostty does, so output never
/// holds up input and layout on the UI thread. The UI thread still draws directly when layout
/// needs a frame in the same pass (a resize, a tab shown). Both hold <see cref="_renderGate"/>
/// while they read the grid and draw; the renderer serializes the shared device itself.
/// What a frame changes in XAML (scroll bar, IME box, pointer, blink, annotations, margin)
/// goes back to the UI thread as <see cref="FrameEffects"/>, merged until it runs.
/// </summary>
public sealed unsafe partial class NativeTerminalSurface
{
    // Guards the cell buffers, Columns/Rows as the frame sees them, the highlighter's row state,
    // the hover underline and the render-side frame state below.
    private readonly object _renderGate = new();
    private Thread? _renderThread;
    private readonly AutoResetEvent _renderWake = new(false);
    private volatile bool _renderStop;

    // XAML state the render thread can't read itself, mirrored by the UI thread.
    private volatile bool _visible = true;
    private volatile bool _loaded;
    private volatile bool _renderingPaused;

    // Set from any thread; the next frame takes them.
    private int _forceFull = 1;
    private int _cursorRowDirty;
    private volatile bool _cursorBlinkOn = true;
    private volatile bool _focused;

    // Render-side frame state (under _renderGate).
    private GhosttyFrameInfo _renderInfo = new() { MouseShape = GhosttyNative.MouseShapeText };
    private int _lastCursorRow = -1;

    private readonly object _effectsGate = new();
    private FrameEffects _pendingEffects;
    private bool _effectsPosted;

    /// <summary>What a frame needs done on the UI thread.</summary>
    private struct FrameEffects
    {
        public bool HasInfo;          // Info is a new frame's metadata
        public GhosttyFrameInfo Info;
        public bool CursorMoved;
        public bool Annotate;         // output arrived: command marks and ruler follow it
        public bool HoverMoved;       // output moved under the hovered link
        public bool MarginChanged;
        public uint? Margin;
        public bool Poll;             // synchronized output is holding the frame

        public readonly bool Any => HasInfo || Annotate || HoverMoved || MarginChanged || Poll;

        public void Merge(in FrameEffects next)
        {
            if (next.HasInfo)
            {
                HasInfo = true;
                Info = next.Info;
            }
            CursorMoved |= next.CursorMoved;
            Annotate |= next.Annotate;
            HoverMoved |= next.HoverMoved;
            if (next.MarginChanged)
            {
                MarginChanged = true;
                Margin = next.Margin;
            }
            Poll |= next.Poll;
        }
    }

    private void ForceFull() => Volatile.Write(ref _forceFull, 1);

    private void StartRenderThread()
    {
        if (_renderThread is not null)
            return;
        _renderThread = new Thread(RenderLoop, 256 * 1024)
        {
            IsBackground = true,
            Name = "Terminal render",
            Priority = ThreadPriority.AboveNormal,
        };
        _renderThread.Start();
    }

    private void StopRenderThread()
    {
        _renderStop = true;
        _renderWake.Set();
        if (_renderThread is { } thread && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(2));
        _renderThread = null;
    }

    /// <summary>Asks for a frame. Safe from any thread; requests before the frame coalesce.</summary>
    private void RequestFrame()
    {
        if (!_disposed)
            _renderWake.Set();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private void RenderLoop()
    {
        while (true)
        {
            _renderWake.WaitOne();
            if (_renderStop)
                return;
            FrameEffects effects;
            lock (_renderGate)
                effects = RenderFrameSafely();
            // A held frame (synchronized output) is polled from here, once per composition.
            var poll = effects.Poll;
            effects.Poll = false;
            if (effects.Any)
                PostEffects(effects);
            // At most one frame per composition: the first request after a quiet spell (a
            // keystroke's echo) draws at once, and output that keeps coming gathers here.
            if ((effects.HasInfo || poll) && !_renderStop && DwmFlush() < 0)
                Thread.Sleep(4);
            if (poll)
                _renderWake.Set();
        }
    }

    /// <summary>Draws on the UI thread now, for layout that must show the frame in the same
    /// pass: the effects apply directly.</summary>
    private void DrawFrame()
    {
        FrameEffects effects;
        lock (_renderGate)
            effects = RenderFrameSafely();
        if (effects.Any)
            ApplyEffects(effects);
    }

    private FrameEffects RenderFrameSafely()
    {
        try
        {
            return RenderFrameLocked();
        }
        catch (Exception exception)
        {
            TerminalSurface.TraceHook?.Invoke($"ghostty render failed: {exception}");
            ForceFull();
            return default;
        }
    }

    private void PostEffects(in FrameEffects effects)
    {
        lock (_effectsGate)
        {
            _pendingEffects.Merge(effects);
            if (_effectsPosted)
                return;
            _effectsPosted = true;
        }
        if (!DispatcherQueue.TryEnqueue(ApplyPendingEffects))
        {
            lock (_effectsGate)
                _effectsPosted = false;
        }
    }

    private void ApplyPendingEffects()
    {
        FrameEffects effects;
        lock (_effectsGate)
        {
            effects = _pendingEffects;
            _pendingEffects = default;
            _effectsPosted = false;
        }
        ApplyEffects(effects);
    }

    /// <summary>UI thread: the XAML side of the frames drawn since the last call.</summary>
    private void ApplyEffects(in FrameEffects effects)
    {
        if (_disposed)
            return;
        if (effects.HasInfo)
        {
            var info = effects.Info;
            _lastInfo = info;
            var blink = info.CursorBlinking != 0 && _focused && info.CursorVisible != 0;
            if (blink != _blinkTimer.IsRunning)
            {
                if (blink) _blinkTimer.Start(); else { _blinkTimer.Stop(); _cursorBlinkOn = true; }
            }
            UpdatePointerCursor();
            if (effects.CursorMoved)
                PlaceTextInput();
            UpdateScrollBar(info);
            if (_findOpen)
                UpdateFindCount();
        }
        if (effects.Annotate)
            QueueAnnotations();
        if (effects.HoverMoved)
            UpdateHoverLink(_hoverCell, force: true);
        if (effects.MarginChanged)
        {
            var color = effects.Margin ?? _theme.Background;
            Background = new SolidColorBrush(ToColor(color));
            _ruler.SetLaneBackground(color);
        }
        if (effects.Poll)
            RequestFrame(); // a draw from layout hit a held frame: the render thread polls it
    }

    public override void SetRenderingPaused(bool paused)
    {
        if (_renderingPaused == paused || _disposed)
            return;
        _renderingPaused = paused;
        if (paused)
        {
            _blinkTimer.Stop(); // the next frame restarts it
            _cursorBlinkOn = true;
            return;
        }
        ForceFull();
        RequestFrame();
    }

    /// <summary>Reads the dirty rows, highlights them and draws. Under <see cref="_renderGate"/>,
    /// on the render thread or the UI thread; touches no XAML.</summary>
    private FrameEffects RenderFrameLocked()
    {
        if (_disposed || _term == IntPtr.Zero)
            return default;
        if (_renderingPaused)
            return new FrameEffects { Annotate = true }; // nothing to draw, but command tracking follows the output
        if (!_loaded || _replaying || !_visible || _cells == null || _renderer.SwapChain is null)
            return default;
        if ((long)Columns * Rows > _cellCapacity || Rows > _dirtyCapacity)
            return default; // a failed resize must not read a frame into the previous, smaller buffers
        var info = _renderInfo;
        var full = Interlocked.Exchange(ref _forceFull, 0) != 0;
        var frameStart = FrameStats.Enabled ? FrameStats.Now() : 0;
        var rows = GhosttyNative.rvt_read_frame(_term, _cells, (ushort)Columns, (ushort)Rows, _dirty, full ? 1 : 0, &info);
        var readEnd = FrameStats.Enabled ? FrameStats.Now() : 0;
        if (info.RenderHeld != 0)
        {
            // Poll through the native synchronized-output timeout even without more output.
            // Nothing from a held frame, including cursor/scroll metadata, is published.
            if (full)
                ForceFull();
            return new FrameEffects { Poll = true };
        }
        // Highlight only the rows just read: their cells are fresh, so tints never compound.
        // The alternate screen (vim, htop) is never highlighted, as in terminal.html.
        if (rows > 0 && _highlighter.HasRules && info.Alternate == 0)
        {
            for (var y = 0; y < Rows; y++)
            {
                if (_dirty[y] == 0)
                    continue;
                _highlightRow = y;
                _highlighter.ApplyRow(new Span<TerminalCell>(_cells + y * Columns, Columns), _highlightGrapheme ??= x => GraphemeAt(x, _highlightRow));
            }
        }
        var effects = new FrameEffects { HasInfo = true, Annotate = rows > 0 || full };
        var hoverSpans = _hoverSpans;
        if (rows > 0 && hoverSpans.Length > 0)
        {
            ApplyHoverLink(hoverSpans);
            foreach (var span in hoverSpans)
                effects.HoverMoved |= span.Row < Rows && _dirty[span.Row] != 0; // output moved under the pointer
        }

        var cursorMoved = info.CursorY != _renderInfo.CursorY || info.CursorX != _renderInfo.CursorX
            || info.CursorVisible != _renderInfo.CursorVisible || info.CursorStyle != _renderInfo.CursorStyle;
        if (cursorMoved)
        {
            _cursorBlinkOn = true;
            MarkCursorDirty();
        }
        _renderInfo = info;
        effects.Info = info;
        effects.CursorMoved = cursorMoved;
        if (info.CursorVisible != 0 && info.CursorY < Rows)
            _dirty[info.CursorY] = 1;
        if (_lastCursorRow >= 0 && _lastCursorRow < Rows)
            _dirty[_lastCursorRow] = 1;
        var cursorDirty = Interlocked.Exchange(ref _cursorRowDirty, 0) != 0;
        if (rows == 0 && !full && !cursorMoved && !cursorDirty)
            return effects;

        var drawStart = FrameStats.Enabled ? FrameStats.Now() : 0;
        if (!_renderer.Draw(_cells, Columns, Rows, _dirty, info, _cursorBlinkOn, _focused, _theme.Selection, full))
        {
            ForceFull(); // recovery needs a full frame
            return effects;
        }
        if (FrameStats.Enabled)
            RecordFrameStats(frameStart, readEnd, drawStart, full);
        if (_renderer.MarginColor != _marginColor)
        {
            _marginColor = _renderer.MarginColor;
            effects.MarginChanged = true;
            effects.Margin = _marginColor;
        }
        _lastCursorRow = info.CursorVisible != 0 ? info.CursorY : -1;
        return effects;
    }

    private void RecordFrameStats(long frameStart, long readEnd, long drawStart, bool full)
    {
        var end = FrameStats.Now();
        var timings = _renderer.LastTimings;
        FrameStats.Frame(new FrameRecord
        {
            Surface = _renderer.StatsSurface,
            Frame = _renderer.FrameNumber - 1,
            Started = frameStart,
            IntervalMs = _statsLastFrame == 0 ? 0 : FrameStats.Microseconds(_statsLastFrame, frameStart) / 1000,
            Columns = Columns,
            Rows = Rows,
            RowsDrawn = timings.RowsDrawn,
            Full = full,
            BufferWidth = _renderer.BufferWidth,
            BufferHeight = _renderer.BufferHeight,
            ReadUs = FrameStats.Microseconds(frameStart, readEnd),
            PrepUs = FrameStats.Microseconds(readEnd, drawStart),
            RecordUs = timings.RecordUs,
            EndDrawUs = timings.EndDrawUs,
            CopyUs = timings.CopyUs,
            PresentUs = timings.PresentUs,
            TotalUs = FrameStats.Microseconds(frameStart, end),
            ParsedBytes = Interlocked.Exchange(ref _statsParsedBytes, 0),
            ParseUs = FrameStats.Microseconds(0, Interlocked.Exchange(ref _statsParseTicks, 0)),
            AnnotationsUs = FrameStats.Microseconds(0, Interlocked.Exchange(ref _statsAnnotationTicks, 0)),
            KeyframeUs = FrameStats.Microseconds(0, Interlocked.Exchange(ref _statsKeyframeTicks, 0)),
        });
        _statsLastFrame = frameStart;
    }
}
