using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Resesh.Terminal.Ghostty;

/// <summary>
/// Live terminal drawn in-process: libghostty-vt parses on the backend's reader thread and a
/// Direct2D/DirectWrite renderer presents into a SwapChainPanel on the UI thread, so the
/// terminal is ordinary XAML content (no WebView2 airspace, focus or accelerator workarounds).
/// Selected with RESESH_TERMINAL_SURFACE=ghostty. Not yet implemented here: rewind capture,
/// command marks and the ruler, highlights, find, command history, IME composition, UIA text.
/// </summary>
public sealed unsafe partial class GhosttyTerminalSurface : TerminalSurface
{
    private const int BlinkMilliseconds = 530;
    private const long KeyframeMinimumBytes = 1024 * 1024;
    private const long KeyframeMinimumMilliseconds = 1000;
    private const long KeyframeMaximumMilliseconds = 10000;

    private const double ScrollBarWidth = 14;

    private readonly SwapChainPanel _panel = new();
    private readonly Microsoft.UI.Xaml.Controls.Primitives.ScrollBar _scrollBar = new()
    {
        Orientation = Orientation.Vertical,
        IndicatorMode = Microsoft.UI.Xaml.Controls.Primitives.ScrollingIndicatorMode.MouseIndicator,
        Width = ScrollBarWidth,
        SmallChange = 1,
        IsTabStop = false,
    };
    private bool _updatingScrollBar;

    // Find bar: ordinary XAML over the swap chain (no airspace to work around).
    private readonly Border _findBar = new();
    private readonly TextBox _findInput = new();
    private readonly TextBlock _findCount = new();
    private bool _findOpen;
    private (ulong Total, ulong Offset, ulong Length) _scrollState;
    private readonly GhosttyRenderer _renderer = new();
    private readonly object _termGate = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _blinkTimer;
    private readonly List<byte[]> _earlyOutput = [];
    private IntPtr _term;
    private GCHandle _self;
    private GhosttyCell* _cells;
    private byte* _dirty;
    private int _cellCapacity;
    private int _framePending;
    private bool _renderingHooked;
    private bool _forceFull = true;
    private bool _disposed;
    private bool _initialized;
    private bool _readyRaised;
    private bool _focused;
    private bool _inputEnabled = true;
    private bool _connected = true;
    private bool _isSplit;
    private bool _cursorBlinkOn = true;
    private int _lastCursorRow = -1;
    private GhosttyFrameInfo _lastInfo;
    private ushort _suppressCharactersForKey;
    private char _pendingHighSurrogate;
    private float _fontScale;

    // Rewind keyframes (guarded by _termGate): same cadence as terminal.html.
    private long _keyframeBytes;
    private long _lastKeyframeMs;
    private long _lastObservedMs;

    private int _fontSize = 14;
    private int _zoomDelta;
    private string _fontFamily = "Cascadia Mono, Consolas, monospace";
    private GhosttyTheme _theme = GhosttyThemes.Find("dark");
    private bool _copyOnSelect;
    private bool _rightClickPaste;
    private int _scrollback = 10000;
    private bool _readOnly;
    private bool _fixed80Columns;

    private bool _selecting;
    private int _autoscroll; // 0 none, 1 up, 2 down (libghostty-vt's request while dragging)
    private (float X, float Y) _lastPointer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoscrollTimer;
    private uint _buttonsDown;

    /// <summary>Why the surface cannot be used on this machine, or null when it can.</summary>
    public static string? UnavailableReason => GhosttyNative.TryLoad();

    public override event Action<byte[]>? InputReceived;
    public override event Action<int, int>? Resized;
    public override event TerminalOutputObservedHandler? OutputObserved;
    public override event Action<ReadOnlyMemory<byte>, int, int, long>? KeyframeCaptured;
    public override event Action? ReconnectRequested;
    public override event Action<string, int>? ShortcutRequested;
    public override event Action<int, int>? Ready;
    public override event Action<string>? TitleChanged;
    public override event Action<string, bool>? CommandChanged { add { } remove { } }
    public override event Action<TerminalCommandExecution>? CommandExecutionChanged { add { } remove { } }
    public override event Action<string, string?>? PromptContextChanged { add { } remove { } }
    public override event Action<string>? WorkingDirectoryReported;
    public override event Action<string>? WindowsWorkingDirectoryReported;
    public override event Action<string>? ContextReported;
    public override event Action<int, string>? AgentOscReceived;
    public override event Action? BellReceived;
    public override event Action<string>? CommandObserved { add { } remove { } }
    public override event Action<bool>? CommandsPanelOpenChanged { add { } remove { } }
    public override event Action<TerminalCommandRecord>? CommandRecorded { add { } remove { } }

    public override bool SupportsRewindCapture => true;
    public override int Columns { get; protected set; } = 80;
    public override int Rows { get; protected set; } = 24;

    private int EffectiveFontSize => Math.Clamp(_fontSize + _zoomDelta, 6, 72);

    public GhosttyTerminalSurface()
    {
        if (UnavailableReason is { } reason)
            throw new InvalidOperationException(reason);

        AutomationProperties.SetAutomationId(this, "GhosttyTerminalSurface");
        AutomationProperties.SetName(this, "Terminal");
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        Background = new SolidColorBrush(ToColor(_theme.Background));
        _panel.IsHitTestVisible = false;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ScrollBarWidth) });
        Children.Add(_panel);
        SetColumn(_scrollBar, 1);
        Children.Add(_scrollBar);
        // ValueChanged, not Scroll: Scroll fires only for pointer input, while keyboard and
        // UI Automation (screen readers, tests) move the bar through its value.
        _scrollBar.ValueChanged += OnScrollBarValueChanged;
        ConfigureFindBar();

        _renderer.GraphemeAt = GraphemeAt;
        _blinkTimer = DispatcherQueue.CreateTimer();
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(BlinkMilliseconds);
        _blinkTimer.Tick += (_, _) =>
        {
            _cursorBlinkOn = !_cursorBlinkOn;
            MarkCursorDirty();
            RequestFrame();
        };

        _panel.SizeChanged += (_, _) => Relayout();
        _panel.CompositionScaleChanged += (_, _) => Relayout();
        Loaded += (_, _) => Relayout();
        Unloaded += (_, _) => UnhookRendering();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            _forceFull = true;
            RequestFrame();
        });

        PreviewKeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        CharacterReceived += OnCharacterReceived;
        GotFocus += (_, _) => SetFocused(true);
        LostFocus += (_, _) => SetFocused(false);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndSelectionGesture(null);
        PointerWheelChanged += OnPointerWheelChanged;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    }

    // ---- lifecycle ------------------------------------------------------------------------

    public override Task InitializeAsync()
    {
        if (_initialized || _disposed)
            return Task.CompletedTask;
        _initialized = true;
        _self = GCHandle.Alloc(this);
        lock (_termGate)
        {
            _term = GhosttyNative.rvt_new((ushort)Columns, (ushort)Rows, (nuint)Math.Max(0, _scrollback),
                &OnNativeEvent, GCHandle.ToIntPtr(_self));
            if (_term == IntPtr.Zero)
                throw new InvalidOperationException("libghostty-vt could not create a terminal.");
            ApplyColors();
            foreach (var chunk in _earlyOutput)
                fixed (byte* p = chunk)
                    GhosttyNative.rvt_write(_term, p, (nuint)chunk.Length);
            _earlyOutput.Clear();
            CaptureKeyframeLocked(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        Relayout();
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _blinkTimer.Stop();
        UnhookRendering();
        lock (_termGate)
        {
            if (_term != IntPtr.Zero)
                GhosttyNative.rvt_free(_term);
            _term = IntPtr.Zero;
        }
        if (_self.IsAllocated)
            _self.Free();
        _renderer.Dispose();
        if (_cells != null) NativeMemory.Free(_cells);
        if (_dirty != null) NativeMemory.Free(_dirty);
        _cells = null;
        _dirty = null;
    }

    // ---- output ---------------------------------------------------------------------------

    /// <summary>Parses on the caller's (backend reader) thread; the next composition frame draws it.</summary>
    public override void WriteOutput(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty)
            return;
        lock (_termGate)
        {
            // Replay takes the events strictly after a keyframe's time, so output must never
            // share a millisecond with the keyframe that already contains earlier output.
            var unixMs = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _lastKeyframeMs + 1);
            _lastObservedMs = unixMs;
            try
            {
                OutputObserved?.Invoke(data, unixMs);
            }
            catch (Exception exception)
            {
                TerminalControl.TraceHook?.Invoke($"output observer failed: {exception.Message}");
            }
            WriteToTerminal(data);
            if (_term == IntPtr.Zero)
                return;
            _keyframeBytes += data.Length;
            var since = unixMs - _lastKeyframeMs;
            if (since >= KeyframeMinimumMilliseconds &&
                (_keyframeBytes >= KeyframeMinimumBytes || since >= KeyframeMaximumMilliseconds))
                CaptureKeyframeLocked(unixMs);
        }
    }

    /// <summary>Serializes the full screen as VT text on the parsing thread, so the keyframe
    /// holds exactly the output observed up to <paramref name="unixMs"/>.</summary>
    private void CaptureKeyframeLocked(long unixMs)
    {
        if (_term == IntPtr.Zero || _readOnly || KeyframeCaptured is not { } handler)
            return;
        var buffer = GhosttyNative.rvt_format_vt(_term, out var length);
        byte[] state;
        try
        {
            state = buffer == null || length == 0
                ? "\u001b[0m"u8.ToArray() // an empty screen still needs a non-empty keyframe
                : new ReadOnlySpan<byte>(buffer, checked((int)length)).ToArray();
        }
        finally
        {
            GhosttyNative.rvt_free_buffer(buffer, length);
        }
        _keyframeBytes = 0;
        _lastKeyframeMs = Math.Max(unixMs, _lastObservedMs);
        try
        {
            handler(state, Columns, Rows, _lastKeyframeMs);
        }
        catch (Exception exception)
        {
            TerminalControl.TraceHook?.Invoke($"keyframe capture failed: {exception.Message}");
        }
    }

    private void WriteToTerminal(ReadOnlySpan<byte> data)
    {
        lock (_termGate)
        {
            if (_disposed)
                return;
            if (_term == IntPtr.Zero)
            {
                _earlyOutput.Add(data.ToArray());
                return;
            }
            fixed (byte* p = data)
                GhosttyNative.rvt_write(_term, p, (nuint)data.Length);
        }
        RequestFrame();
    }

    private void WriteDisplayText(string text) => WriteToTerminal(Encoding.UTF8.GetBytes(text));

    private string Rule() => "\r\n\x1b[90m" + new string('─', Math.Max(Columns - 1, 10)) + "\x1b[0m\r\n";

    public override void NotifyConnected() => _connected = true;

    public override void NotifyDisconnected(string message, string action = "reconnect", bool neutral = false)
    {
        _connected = false;
        WriteDisplayText(Rule() + (neutral ? "\x1b[90m" : "\x1b[1;33m") +
            (string.IsNullOrEmpty(message) ? "Disconnected." : message) +
            "\x1b[0m Press Enter to " + (string.IsNullOrEmpty(action) ? "reconnect" : action) + ".\r\n");
    }

    public override void WriteDivider() => WriteDisplayText(Rule());

    public override void WriteNotice(string message) => WriteDisplayText("\x1b[90m" + message + "\x1b[0m\r\n");

    [UnmanagedCallersOnly]
    private static void OnNativeEvent(IntPtr user, int kind, byte* data, nuint length)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not GhosttyTerminalSurface self || self._disposed)
                return;
            var bytes = length == 0 ? [] : new ReadOnlySpan<byte>(data, checked((int)length)).ToArray();
            // Raised on the UI thread, like the WebView surface's events.
            self.DispatcherQueue.TryEnqueue(() => self.DispatchNativeEvent((GhosttyEventKind)kind, bytes));
        }
        catch (Exception exception)
        {
            TerminalControl.TraceHook?.Invoke($"ghostty event failed: {exception.Message}");
        }
    }

    private void DispatchNativeEvent(GhosttyEventKind kind, byte[] data)
    {
        if (_disposed)
            return;
        switch (kind)
        {
            case GhosttyEventKind.Pty:
                if (!_readOnly)
                    InputReceived?.Invoke(data);
                break;
            case GhosttyEventKind.Bell:
                BellReceived?.Invoke();
                break;
            case GhosttyEventKind.Title:
                TitleChanged?.Invoke(Encoding.UTF8.GetString(data));
                break;
            case GhosttyEventKind.WorkingDirectory:
                var directory = Encoding.UTF8.GetString(data);
                if (directory.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    WorkingDirectoryReported?.Invoke(directory);
                else if (directory.Length > 0)
                    WindowsWorkingDirectoryReported?.Invoke("9;" + directory); // OSC 9;9 form
                break;
            case GhosttyEventKind.Clipboard:
                if (!_readOnly)
                    CopyText(Encoding.UTF8.GetString(data));
                break;
            case GhosttyEventKind.Osc:
                var content = Encoding.UTF8.GetString(data);
                var separator = content.IndexOf(';');
                if (separator <= 0 || !int.TryParse(content.AsSpan(0, separator), out var code))
                    break;
                var payload = content[(separator + 1)..];
                if (code == 7377)
                    AgentOscReceived?.Invoke(code, payload.Length > 2048 ? payload[..2048] : payload);
                else if (code == 3008)
                    ContextReported?.Invoke(payload);
                break;
        }
    }

    // ---- layout and drawing ---------------------------------------------------------------

    private void Relayout()
    {
        if (_disposed || _panel.ActualWidth <= 0 || _panel.ActualHeight <= 0)
            return;
        var scale = _panel.CompositionScaleX > 0 ? _panel.CompositionScaleX : (float)(XamlRoot?.RasterizationScale ?? 1);
        var fontChanged = scale != _fontScale;
        if (fontChanged)
        {
            _fontScale = scale;
            _renderer.SetFont(_fontFamily, EffectiveFontSize * scale);
        }
        var pixelWidth = (int)Math.Round(_panel.ActualWidth * scale);
        var pixelHeight = (int)Math.Round(_panel.ActualHeight * scale);
        if (_renderer.Resize(pixelWidth, pixelHeight))
            AttachSwapChain();
        _renderer.SetCompositionScale(scale, scale);

        _renderer.OriginX = (int)Math.Round(6 * scale);
        var cols = _fixed80Columns ? 80 : Math.Max(2, (pixelWidth - _renderer.OriginX) / _renderer.CellWidth);
        var rows = Math.Max(1, pixelHeight / _renderer.CellHeight);
        var sizeChanged = cols != Columns || rows != Rows;
        Columns = cols;
        Rows = rows;
        EnsureCellBuffers();
        if (_term != IntPtr.Zero && (sizeChanged || fontChanged || !_readyRaised))
            GhosttyNative.rvt_resize(_term, (ushort)cols, (ushort)rows, (uint)_renderer.CellWidth, (uint)_renderer.CellHeight);
        _forceFull = true;
        RequestFrame();

        if (!_initialized)
            return;
        if (!_readyRaised)
        {
            _readyRaised = true;
            Ready?.Invoke(cols, rows);
        }
        else if (sizeChanged)
        {
            Resized?.Invoke(cols, rows);
        }
    }

    private void EnsureCellBuffers()
    {
        var needed = Columns * Rows;
        if (needed <= _cellCapacity && _dirty != null)
            return;
        if (_cells != null) NativeMemory.Free(_cells);
        if (_dirty != null) NativeMemory.Free(_dirty);
        _cellCapacity = Math.Max(needed, 1);
        _cells = (GhosttyCell*)NativeMemory.AllocZeroed((nuint)(_cellCapacity * sizeof(GhosttyCell)));
        _dirty = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(Rows, 512));
    }

    private void AttachSwapChain()
    {
        if (_renderer.SwapChain is not { } chain)
            return;
        var unknown = ((WinRT.IWinRTObject)_panel).NativeObject.ThisPtr;
        var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"); // ISwapChainPanelNative
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var native));
        try
        {
            var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(IntPtr**)native)[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, chain.NativePointer));
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    /// <summary>Asks for a draw on the next composition frame. Safe from any thread; repeated
    /// requests before that frame coalesce.</summary>
    private void RequestFrame()
    {
        if (_disposed || Interlocked.Exchange(ref _framePending, 1) == 1)
            return;
        if (!DispatcherQueue.TryEnqueue(HookRendering))
            Volatile.Write(ref _framePending, 0);
    }

    private void HookRendering()
    {
        if (_disposed || _renderingHooked)
            return;
        _renderingHooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked)
            return;
        _renderingHooked = false;
        CompositionTarget.Rendering -= OnRendering;
        Volatile.Write(ref _framePending, 0);
    }

    private void OnRendering(object? sender, object args)
    {
        // Unhook first: leaving Rendering subscribed makes XAML draw every frame even when idle.
        UnhookRendering();
        try
        {
            RenderFrame();
        }
        catch (Exception exception)
        {
            TerminalControl.TraceHook?.Invoke($"ghostty render failed: {exception}");
            _forceFull = true;
        }
    }

    private void RenderFrame()
    {
        if (_disposed || _term == IntPtr.Zero || _cells == null || _renderer.SwapChain is null || Visibility != Visibility.Visible)
            return;
        GhosttyFrameInfo info;
        var full = _forceFull;
        var rows = GhosttyNative.rvt_read_frame(_term, _cells, (ushort)Columns, (ushort)Rows, _dirty, full ? 1 : 0, &info);

        var cursorMoved = info.CursorY != _lastInfo.CursorY || info.CursorX != _lastInfo.CursorX
            || info.CursorVisible != _lastInfo.CursorVisible || info.CursorStyle != _lastInfo.CursorStyle;
        if (cursorMoved)
        {
            _cursorBlinkOn = true;
            MarkCursorDirty();
        }
        var blink = info.CursorBlinking != 0 && _focused && info.CursorVisible != 0;
        if (blink != _blinkTimer.IsRunning)
        {
            if (blink) _blinkTimer.Start(); else { _blinkTimer.Stop(); _cursorBlinkOn = true; }
        }
        _lastInfo = info;
        UpdateScrollBar(info);
        if (_findOpen)
            UpdateFindCount();
        if (info.CursorVisible != 0 && info.CursorY < Rows)
            _dirty[info.CursorY] = 1;
        if (_lastCursorRow >= 0 && _lastCursorRow < Rows)
            _dirty[_lastCursorRow] = 1;
        if (rows == 0 && !full && !cursorMoved && !_cursorRowDirty)
            return;
        _cursorRowDirty = false;

        _renderer.Draw(_cells, Columns, Rows, _dirty, info, _cursorBlinkOn, _focused, _theme.Selection, full);
        _forceFull = false;
        _lastCursorRow = info.CursorVisible != 0 ? info.CursorY : -1;
        if (!HasPainted)
            OnPainted();
    }

    private bool _cursorRowDirty;

    /// <summary>Mirrors libghostty-vt's scroll state; it has no change event, so every frame
    /// compares against the last values.</summary>
    private void UpdateScrollBar(in GhosttyFrameInfo info)
    {
        var state = (info.ScrollTotal, info.ScrollOffset, info.ScrollLength);
        if (state == _scrollState)
            return;
        _scrollState = state;
        var maximum = info.ScrollTotal > info.ScrollLength ? info.ScrollTotal - info.ScrollLength : 0;
        _updatingScrollBar = true;
        try
        {
            _scrollBar.Minimum = 0;
            _scrollBar.Maximum = maximum;
            _scrollBar.ViewportSize = Math.Max(1, info.ScrollLength);
            _scrollBar.LargeChange = Math.Max(1, info.ScrollLength - 1);
            _scrollBar.Value = Math.Min(info.ScrollOffset, maximum);
            _scrollBar.IsEnabled = maximum > 0;
        }
        finally
        {
            _updatingScrollBar = false;
        }
    }

    private void OnScrollBarValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (_updatingScrollBar || _disposed || _term == IntPtr.Zero)
            return;
        GhosttyNative.rvt_scroll(_term, GhosttyNative.ScrollRow, (nint)Math.Round(args.NewValue));
        RequestFrame();
    }

    private void MarkCursorDirty() => _cursorRowDirty = true;

    private string? GraphemeAt(int x, int y)
    {
        if (_term == IntPtr.Zero)
            return null;
        var buffer = stackalloc uint[32];
        var n = GhosttyNative.rvt_cell_graphemes(_term, (ushort)x, (ushort)y, buffer, 32);
        if (n <= 0)
            return null;
        var builder = new StringBuilder(n * 2);
        for (var i = 0; i < n; i++)
            builder.Append(char.ConvertFromUtf32((int)buffer[i]));
        return builder.ToString();
    }

    private void ApplyColors()
    {
        if (_term == IntPtr.Zero)
            return;
        fixed (uint* palette = _theme.Ansi)
            GhosttyNative.rvt_set_colors(_term, _theme.Foreground, _theme.Background, _theme.Cursor, palette);
        Background = new SolidColorBrush(ToColor(_theme.Background));
    }

    private static Windows.UI.Color ToColor(uint rgb) =>
        Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ---- options --------------------------------------------------------------------------

    public override void SetInitialOptions(int fontSize, string fontFamily, string theme, bool copyOnSelect,
        bool rightClickPaste, int scrollback, IReadOnlyList<object>? highlights = null, bool readOnly = false,
        bool fixed80Columns = false)
    {
        _fontSize = fontSize > 0 ? fontSize : 14;
        _fontFamily = string.IsNullOrWhiteSpace(fontFamily) ? _fontFamily : fontFamily;
        _theme = GhosttyThemes.Find(theme);
        _copyOnSelect = copyOnSelect;
        _rightClickPaste = rightClickPaste;
        _scrollback = scrollback;
        _readOnly = readOnly;
        _fixed80Columns = fixed80Columns;
        Background = new SolidColorBrush(ToColor(_theme.Background));
    }

    public override void ApplyOptions(int? fontSize = null, string? fontFamily = null, string? theme = null,
        bool? copyOnSelect = null, bool? rightClickPaste = null, int? scrollback = null)
    {
        var relayout = false;
        if (fontSize is > 0 && fontSize != _fontSize) { _fontSize = fontSize.Value; relayout = true; }
        if (!string.IsNullOrWhiteSpace(fontFamily) && fontFamily != _fontFamily) { _fontFamily = fontFamily; relayout = true; }
        if (theme is not null)
        {
            _theme = GhosttyThemes.Find(theme);
            ApplyColors();
        }
        if (copyOnSelect is { } c) _copyOnSelect = c;
        if (rightClickPaste is { } r) _rightClickPaste = r;
        if (scrollback is { } s && s != _scrollback)
        {
            _scrollback = s;
            if (_term != IntPtr.Zero)
                GhosttyNative.rvt_set_scrollback(_term, (nuint)Math.Max(0, s));
        }
        if (relayout)
            _fontScale = 0; // forces SetFont on the next layout pass
        _forceFull = true;
        Relayout();
        RequestFrame();
    }

    private void SetZoom(int delta)
    {
        var before = EffectiveFontSize;
        _zoomDelta = Math.Clamp(_fontSize + delta, 6, 72) - _fontSize;
        if (EffectiveFontSize == before)
            return;
        _fontScale = 0;
        Relayout();
    }

    // ---- focus ----------------------------------------------------------------------------

    public override void FocusTerminal()
    {
        if (!_disposed)
            Focus(FocusState.Programmatic);
    }

    private void SetFocused(bool focused)
    {
        _focused = focused;
        _cursorBlinkOn = true;
        if (!focused) _suppressCharactersForKey = 0;
        MarkCursorDirty();
        RequestFrame();
    }

    public override void SetInputEnabled(bool enabled) => _inputEnabled = enabled;

    public override void SetRulerPresentation(bool isSplit, bool isGroupFocused) => _isSplit = isSplit;

    // ---- keyboard -------------------------------------------------------------------------

    [LibraryImport("user32.dll")] private static partial short GetKeyState(int virtualKey);
    [LibraryImport("user32.dll")] private static partial IntPtr GetKeyboardLayout(uint thread);
    [LibraryImport("user32.dll")] private static partial uint MapVirtualKeyExW(uint code, uint mapType, IntPtr layout);
    [LibraryImport("user32.dll")]
    private static partial int ToUnicodeEx(uint virtualKey, uint scanCode, byte* keyState, char* buffer, int capacity,
        uint flags, IntPtr layout);

    private static bool Down(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsModifierKey(VirtualKey key) => key is VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu
        or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftWindows or VirtualKey.RightWindows
        or VirtualKey.CapitalLock or VirtualKey.NumberKeyLock or VirtualKey.Scroll;

    /// <summary>The text a key types in the current layout with only Shift and Caps Lock
    /// applied (so Ctrl/Alt chords can still be encoded as the character they modify).
    /// Dead keys and unmapped keys return an empty string.</summary>
    private static string TextFor(uint virtualKey, uint scanCode, bool shift)
    {
        var state = stackalloc byte[256];
        if (shift) { state[0x10] = 0x80; state[0xA0] = 0x80; }
        state[0x14] = (byte)(GetKeyState(0x14) & 1);
        var buffer = stackalloc char[8];
        // Flag 4: do not change the keyboard state (keeps pending dead keys intact).
        var n = ToUnicodeEx(virtualKey, scanCode, state, buffer, 8, 4, GetKeyboardLayout(0));
        return n > 0 ? new string(buffer, 0, n) : string.Empty;
    }

    /// <summary>Input aimed at the find bar or scroll bar is theirs, not the shell's.</summary>
    private bool FromTerminal(RoutedEventArgs args) => ReferenceEquals(args.OriginalSource, this);

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_disposed || !_inputEnabled || _term == IntPtr.Zero || !FromTerminal(args))
            return;
        var virtualKey = (ushort)args.Key;
        if (_suppressCharactersForKey != 0 && _suppressCharactersForKey != virtualKey)
            _suppressCharactersForKey = 0; // a missed key-up must not swallow later typing
        if (TryHandleShortcut(virtualKey))
        {
            args.Handled = true;
            return;
        }
        if (IsModifierKey(args.Key))
            return;
        if (!_connected)
        {
            if (args.Key == VirtualKey.Enter)
            {
                _connected = true; // the host reports again if the reconnect fails
                ReconnectRequested?.Invoke();
            }
            args.Handled = true;
            _suppressCharactersForKey = virtualKey;
            return;
        }

        var shift = Down(0x10);
        var control = Down(0x11);
        var alt = Down(0x12);
        var altGr = control && alt && Down(0xA5);
        var scanCode = args.KeyStatus.ScanCode;
        var text = TextFor(virtualKey, scanCode, shift);
        var printable = text.Length > 0 && !char.IsControl(text[0]);
        // Plain and AltGr text arrives through CharacterReceived, which knows dead keys and layouts.
        if (printable && (!control && !alt || altGr))
            return;

        var mods = (shift ? GhosttyNative.ModShift : 0) | (control ? GhosttyNative.ModCtrl : 0) | (alt ? GhosttyNative.ModAlt : 0);
        var unshiftedText = TextFor(virtualKey, scanCode, shift: false);
        var unshifted = unshiftedText.Length > 0 ? (uint)char.ConvertToUtf32(unshiftedText, 0) : 0;
        var utf8 = printable ? Encoding.UTF8.GetBytes(text) : [];
        var output = stackalloc byte[128];
        int written;
        fixed (byte* u = utf8)
        {
            written = GhosttyNative.rvt_encode_key(_term, virtualKey, mods,
                args.KeyStatus.WasKeyDown ? GhosttyNative.KeyRepeat : GhosttyNative.KeyPress,
                u, (nuint)utf8.Length, unshifted, output, 128);
        }
        if (written < 0)
            return; // a key libghostty-vt does not know: leave it to XAML
        args.Handled = true; // also keeps Tab and the arrows from moving XAML focus
        _suppressCharactersForKey = virtualKey;
        if (written > 0)
            SendUserInput(new ReadOnlySpan<byte>(output, written).ToArray());
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (_suppressCharactersForKey == (ushort)args.Key)
            _suppressCharactersForKey = 0;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_disposed || !_inputEnabled || _term == IntPtr.Zero || !FromTerminal(args))
            return;
        args.Handled = true;
        if (_suppressCharactersForKey != 0 || !_connected)
            return;
        var ch = args.Character;
        string text;
        if (char.IsHighSurrogate(ch))
        {
            _pendingHighSurrogate = ch;
            return;
        }
        if (char.IsLowSurrogate(ch) && _pendingHighSurrogate != 0)
        {
            text = new string([_pendingHighSurrogate, ch]);
            _pendingHighSurrogate = '\0';
        }
        else
        {
            text = ch.ToString();
        }
        SendUserInput(Encoding.UTF8.GetBytes(text));
    }

    private void SendUserInput(byte[] bytes)
    {
        if (_readOnly || bytes.Length == 0)
            return;
        InputReceived?.Invoke(bytes);
        if (_lastInfo.ScrollOffset + _lastInfo.ScrollLength < _lastInfo.ScrollTotal)
        {
            GhosttyNative.rvt_scroll(_term, GhosttyNative.ScrollBottom, 0);
            RequestFrame();
        }
    }

    private bool TryHandleShortcut(ushort virtualKey)
    {
        if (MatchShortcut(virtualKey, Down(0x11), Down(0x10), Down(0x12)) is not var (shortcut, chord))
            return false;
        if (shortcut.WhenSplit && !_isSplit)
            return false;
        if (shortcut.Forward)
            ShortcutRequested?.Invoke(shortcut.Id, chord);
        else if (!RunTerminalShortcut(shortcut.Id))
            return false;
        _suppressCharactersForKey = virtualKey;
        return true;
    }

    public override bool InvokeShortcut(string id) => RunTerminalShortcut(id);

    private bool RunTerminalShortcut(string id)
    {
        switch (id)
        {
            case "terminal.find":
                OpenFind();
                return true;
            case "terminal.copy":
                return CopySelection();
            case "terminal.paste":
                _ = PasteFromClipboardAsync();
                return true;
            case "terminal.zoomIn":
                SetZoom(_zoomDelta + 1);
                return true;
            case "terminal.zoomOut":
                SetZoom(_zoomDelta - 1);
                return true;
            case "terminal.zoomReset":
                SetZoom(0);
                return true;
            default:
                return false;
        }
    }

    // ---- find -----------------------------------------------------------------------------

    private void ConfigureFindBar()
    {
        _findInput.Width = 200;
        _findInput.PlaceholderText = "Find";
        _findInput.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_findInput, "Find in terminal");
        _findCount.VerticalAlignment = VerticalAlignment.Center;
        _findCount.Margin = new Thickness(8, 0, 4, 0);
        _findCount.MinWidth = 64;
        _findCount.Opacity = 0.8;
        Button Glyph(string glyph, string name, RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 12 },
                Padding = new Thickness(6),
                Margin = new Thickness(2, 0, 0, 0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Click += click;
            return button;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_findInput);
        row.Children.Add(_findCount);
        row.Children.Add(Glyph("\uE70E", "Older match, above (Enter)", (_, _) => StepFind(1)));
        row.Children.Add(Glyph("\uE70D", "Newer match, below (Shift+Enter)", (_, _) => StepFind(-1)));
        row.Children.Add(Glyph("\uE711", "Close (Esc)", (_, _) => CloseFind()));
        _findBar.Child = row;
        _findBar.Padding = new Thickness(6, 4, 6, 4);
        _findBar.CornerRadius = new CornerRadius(0, 0, 6, 6);
        _findBar.BorderThickness = new Thickness(1, 0, 1, 1);
        _findBar.HorizontalAlignment = HorizontalAlignment.Right;
        _findBar.VerticalAlignment = VerticalAlignment.Top;
        _findBar.Margin = new Thickness(0, 0, 8, 0);
        _findBar.Visibility = Visibility.Collapsed;
        _findBar.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorSecondaryBrush"];
        _findBar.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        Children.Add(_findBar);

        _findInput.TextChanged += (_, _) => RunFind();
        _findInput.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                StepFind(Down(0x10) ? -1 : 1);
                args.Handled = true;
            }
            else if (args.Key == VirtualKey.Escape)
            {
                CloseFind();
                args.Handled = true;
            }
        };
    }

    private void OpenFind()
    {
        if (!_findOpen)
        {
            _findOpen = true;
            _findBar.Visibility = Visibility.Visible;
            // Seed with the selected text, like most find bars.
            var selected = SelectionText();
            if (!string.IsNullOrEmpty(selected) && !selected.Contains('\n'))
                _findInput.Text = selected;
            RunFind();
        }
        _findInput.Focus(FocusState.Programmatic);
        _findInput.SelectAll();
    }

    private void CloseFind()
    {
        if (!_findOpen)
            return;
        _findOpen = false;
        _findBar.Visibility = Visibility.Collapsed;
        if (_term != IntPtr.Zero)
            GhosttyNative.rvt_search_set(_term, null, 0);
        RequestFrame();
        FocusTerminal();
    }

    private void RunFind()
    {
        if (_term == IntPtr.Zero)
            return;
        var needle = Encoding.UTF8.GetBytes(_findInput.Text);
        fixed (byte* p = needle)
            GhosttyNative.rvt_search_set(_term, p, (nuint)needle.Length);
        if (needle.Length > 0)
            GhosttyNative.rvt_search_step(_term, 1); // select the newest match
        UpdateFindCount();
        RequestFrame();
    }

    private void StepFind(int direction)
    {
        if (_term == IntPtr.Zero || _findInput.Text.Length == 0)
            return;
        GhosttyNative.rvt_search_step(_term, direction);
        UpdateFindCount();
        RequestFrame();
    }

    private void UpdateFindCount()
    {
        if (_term == IntPtr.Zero)
            return;
        GhosttyNative.rvt_search_status(_term, out var total, out var current);
        _findCount.Text = _findInput.Text.Length == 0 ? ""
            : total == 0 ? "No results"
            : current == 0 ? $"{total} found"
            : $"{current} of {total}";
    }

    private string? SelectionText()
    {
        if (_term == IntPtr.Zero)
            return null;
        var buffer = GhosttyNative.rvt_selection_text(_term, out var length);
        if (buffer == null)
            return null;
        try
        {
            return Encoding.UTF8.GetString(buffer, checked((int)length));
        }
        finally
        {
            GhosttyNative.rvt_free_buffer(buffer, length);
        }
    }

    // ---- mouse, selection, clipboard ------------------------------------------------------

    private (float X, float Y) PixelPosition(PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this).Position;
        return ((float)(point.X * _fontScale), (float)(point.Y * _fontScale));
    }

    /// <summary>Pointer position in the grid's pixel space, as the mouse encoder expects.</summary>
    private (float X, float Y) GridPosition(PointerRoutedEventArgs args)
    {
        var (x, y) = PixelPosition(args);
        return (Math.Max(0, x - _renderer.OriginX), y);
    }

    private (int X, int Y) CellAt(float x, float y) =>
        (Math.Clamp((int)((x - _renderer.OriginX) / _renderer.CellWidth), 0, Columns - 1), Math.Clamp((int)(y / _renderer.CellHeight), 0, Rows - 1));

    private static int Mods(PointerRoutedEventArgs args)
    {
        var m = args.KeyModifiers;
        return (m.HasFlag(VirtualKeyModifiers.Shift) ? GhosttyNative.ModShift : 0)
            | (m.HasFlag(VirtualKeyModifiers.Control) ? GhosttyNative.ModCtrl : 0)
            | (m.HasFlag(VirtualKeyModifiers.Menu) ? GhosttyNative.ModAlt : 0);
    }

    /// <summary>Mouse reporting wins unless Shift is held, which selects like other terminals.</summary>
    private bool ReportsMouse(PointerRoutedEventArgs args) =>
        _term != IntPtr.Zero && !args.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)
        && GhosttyNative.rvt_mouse_tracking(_term) != 0;

    private void SendMouse(int action, int button, PointerRoutedEventArgs args)
    {
        var (x, y) = GridPosition(args);
        var output = stackalloc byte[64];
        var n = GhosttyNative.rvt_encode_mouse(_term, action, button, Mods(args), x, y, _buttonsDown != 0 ? 1 : 0, output, 64);
        if (n > 0)
            InputReceived?.Invoke(new ReadOnlySpan<byte>(output, n).ToArray());
    }

    private static int ButtonOf(PointerPointProperties p) =>
        p.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 1,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 3,
            _ => 0,
        };

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_disposed || _term == IntPtr.Zero || !FromTerminal(args))
            return;
        Focus(FocusState.Pointer);
        var props = args.GetCurrentPoint(this).Properties;
        var button = ButtonOf(props);
        CapturePointer(args.Pointer);
        args.Handled = true;
        if (ReportsMouse(args) && button != 0)
        {
            _buttonsDown |= 1u << button;
            SendMouse(GhosttyNative.MousePress, button, args);
            return;
        }
        if (button == 1)
        {
            _selecting = true;
            Gesture(GestureKind.Press, PixelPosition(args));
        }
        else if (button == 2 && _rightClickPaste)
        {
            if (!CopySelection(clear: true))
                _ = PasteFromClipboardAsync();
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_disposed || _term == IntPtr.Zero)
            return;
        if (_buttonsDown != 0 || (ReportsMouse(args) && !_selecting))
        {
            SendMouse(GhosttyNative.MouseMotion, 0, args);
            return;
        }
        if (!_selecting)
            return;
        Gesture(GestureKind.Drag, PixelPosition(args));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_disposed || _term == IntPtr.Zero)
            return;
        var button = ButtonOf(args.GetCurrentPoint(this).Properties);
        ReleasePointerCapture(args.Pointer);
        if (button != 0 && (_buttonsDown & (1u << button)) != 0)
        {
            _buttonsDown &= ~(1u << button);
            SendMouse(GhosttyNative.MouseRelease, button, args);
            return;
        }
        if (_selecting && button == 1)
            EndSelectionGesture(PixelPosition(args));
    }

    private enum GestureKind { Press = 0, Release = 1, Drag = 2, AutoscrollTick = 3 }

    /// <summary>Feeds libghostty-vt's selection gesture: it counts clicks (word on double,
    /// line on triple), extends by the same unit while dragging, and asks for autoscroll
    /// when the pointer leaves the grid.</summary>
    private (int Clicks, bool Dragged) Gesture(GestureKind kind, (float X, float Y) position, (int X, int Y)? cell = null)
    {
        _lastPointer = position;
        var (x, y) = cell ?? CellAt(position.X, position.Y);
        var nanoseconds = (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
        GhosttyNative.rvt_gesture(_term, (int)kind, position.X, position.Y, (ushort)x, (ushort)y, nanoseconds,
            (uint)_renderer.OriginX, out var autoscroll, out var clicks, out var dragged);
        SetAutoscroll(kind is GestureKind.Drag or GestureKind.AutoscrollTick ? autoscroll : 0);
        RequestFrame();
        return (clicks, dragged != 0);
    }

    private void EndSelectionGesture((float X, float Y)? position)
    {
        if (!_selecting || _term == IntPtr.Zero)
            return;
        _selecting = false;
        SetAutoscroll(0);
        var (clicks, dragged) = Gesture(GestureKind.Release, position ?? _lastPointer);
        // A plain click selects nothing; a drag or a word/line click copies when asked to.
        if (_copyOnSelect && (dragged || clicks >= 2))
            CopySelection();
    }

    private void SetAutoscroll(int direction)
    {
        _autoscroll = direction;
        if (direction == 0)
        {
            _autoscrollTimer?.Stop();
            return;
        }
        if (_autoscrollTimer is null)
        {
            _autoscrollTimer = DispatcherQueue.CreateTimer();
            _autoscrollTimer.Interval = TimeSpan.FromMilliseconds(50);
            _autoscrollTimer.Tick += (_, _) =>
            {
                if (!_selecting || _autoscroll == 0 || _term == IntPtr.Zero)
                {
                    _autoscrollTimer.Stop();
                    return;
                }
                GhosttyNative.rvt_scroll(_term, GhosttyNative.ScrollDelta, _autoscroll == 1 ? -1 : 1);
                var edge = (CellAt(_lastPointer.X, 0).X, _autoscroll == 1 ? 0 : Rows - 1);
                Gesture(GestureKind.AutoscrollTick, _lastPointer, edge);
            };
        }
        if (!_autoscrollTimer.IsRunning)
            _autoscrollTimer.Start();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (_disposed || _term == IntPtr.Zero)
            return;
        args.Handled = true;
        var delta = args.GetCurrentPoint(this).Properties.MouseWheelDelta;
        var notches = Math.Max(1, Math.Abs(delta) / 120);
        if (ReportsMouse(args))
        {
            for (var i = 0; i < notches; i++)
                SendMouse(GhosttyNative.MousePress, delta > 0 ? 4 : 5, args);
            return;
        }
        GhosttyNative.rvt_scroll(_term, GhosttyNative.ScrollDelta, (delta > 0 ? -3 : 3) * notches);
        RequestFrame();
    }

    private bool CopySelection(bool clear = false)
    {
        if (_term == IntPtr.Zero)
            return false;
        var buffer = GhosttyNative.rvt_selection_text(_term, out var length);
        if (buffer == null)
            return false;
        try
        {
            var text = Encoding.UTF8.GetString(buffer, checked((int)length));
            if (text.Length == 0)
                return false;
            CopyText(text);
        }
        finally
        {
            GhosttyNative.rvt_free_buffer(buffer, length);
        }
        if (clear)
        {
            GhosttyNative.rvt_select_clear(_term);
            RequestFrame();
        }
        return true;
    }

    private static void CopyText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception exception)
        {
            TerminalControl.TraceHook?.Invoke($"clipboard copy failed: {exception.Message}");
        }
    }

    private Task PasteFromClipboardAsync() => GhosttyClipboard.PasteIntoAsync(PasteText);

    /// <summary>Bracketed when the program enabled mode 2004; the encoded bytes come back
    /// through the PTY event and leave as <see cref="InputReceived"/>.</summary>
    public override void PasteText(string text)
    {
        if (_disposed || _readOnly || _term == IntPtr.Zero || string.IsNullOrEmpty(text))
            return;
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* p = bytes)
            GhosttyNative.rvt_paste(_term, p, (nuint)bytes.Length);
        GhosttyNative.rvt_scroll(_term, GhosttyNative.ScrollBottom, 0);
        RequestFrame();
    }

    // ---- features the WebView surface has and this one does not yet ----------------------

    public override void ToggleCommandsPanel() { }
    public override void SetHistoryCapture(bool enabled) { }
    public override void FlushHistory() { }
    public override void ScrollToCommand(long id) { }
    public override void SetPromptPlatform(string? platform) { }
    public override void ApplyHighlights(IReadOnlyList<object> rules) { }
    public override Task<(string Context, string? Platform)?> RequestPromptContextAsync() =>
        Task.FromResult<(string Context, string? Platform)?>(null);
    public override Task ShowReplayAsync(int columns, int rows, ReadOnlyMemory<byte> keyframe, IReadOnlyList<TerminalReplayEvent> events) =>
        Task.CompletedTask;
    public override Task LoadPlaybackAsync(int columns, int rows, IReadOnlyList<TerminalTimedReplayEvent> events) =>
        Task.CompletedTask;
    public override Task SeekPlaybackAsync(double time) => Task.CompletedTask;
}
