using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Resesh.Terminal;

/// <summary>
/// Experimental WinUI host for Microsoft Terminal's HwndTerminal ABI. The child HWND is
/// deliberately isolated here because it always renders above sibling XAML content.
/// </summary>
public sealed class NativeTerminalSurface : TerminalSurface
{
    private const int GwlWndProc = -4;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int SwShow = 5;

    private const uint WmSetFocus = 0x0007;
    private const uint WmKillFocus = 0x0008;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmRightButtonDown = 0x0204;
    private const uint WmMiddleButtonDown = 0x0207;
    private const uint WmXButtonDown = 0x020B;

    private readonly object _outputGate = new();
    private readonly MemoryStream _pendingOutput = new();
    private readonly Decoder _outputDecoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly NativeTerminalApi.EventCallback _eventCallback;
    private readonly WindowProc _windowProc;

    private readonly Border _findBar = new();
    private readonly TextBox _findInput = new();
    private readonly TextBlock _findCount = new();
    private readonly ToggleButton _findCase = new();
    private readonly ToggleButton _findRegex = new();
    private NativeTerminalApi? _api;
    private IntPtr _parentHwnd;
    private IntPtr _childHwnd;
    private IntPtr _terminal;
    private IntPtr _originalWindowProc;
    private long _visibilityCallbackToken;
    private bool _outputDispatchPending;
    private bool _initialized;
    private bool _initializationFailed;
    private bool _inputEnabled = true;
    private bool _hostVisible = true;
    private bool _copyOnSelect = true;
    private bool _rightClickPaste = true;
    private bool _readOnly;
    private bool _reconnectOnEnter;
    private bool _suppressNextCharacter;
    private bool _disposed;
    private int _dpi = 96;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private int _lastWidth = -1;
    private int _lastHeight = -1;
    private int _fontSize = 14;
    private int _scrollback = 10000;
    private string _fontFamily = "Cascadia Mono";
    private string _theme = "dark";
    private XamlRoot? _subscribedRoot;
    private ulong _lastNativeEventSequence;
    private char? _pendingShellMarkAction;
    private bool _alternateBufferActive;
    private bool _bracketedPasteModeEnabled;
    private bool _searchRefreshPending;

    public static Action<string>? TraceHook { get; set; }

    public override event Action<byte[]>? InputReceived;
    public override event Action<int, int>? Resized;
    public override event TerminalOutputObservedHandler? OutputObserved;
    public override event Action<string, int, int, long>? KeyframeCaptured
    {
        add { }
        remove { }
    }
    public override event Action? ReconnectRequested;
    public override event Action? CloseTabRequested;
    public override event Action? SplitRequested;
    public override event Action? FilePaneRequested;
    public override event Action? NewLocalTabRequested;
    public override event Action? CommandPaletteRequested;
    public override event Action? QuickConnectRequested;
    public override event Action<int, int>? Ready;
    public override event Action<string>? TitleChanged;
    public override event Action<string>? CommandChanged;
    public override event Action<string, string?>? PromptContextChanged
    {
        add { }
        remove { }
    }
    public override event Action<string>? WorkingDirectoryReported;
    public override event Action<string>? ContextReported;
    public override event Action<int, string>? AgentOscReceived;
    public override event Action? BellReceived;
    public override event Action<string>? CommandObserved;
    public override event Action<bool>? CommandsPanelOpenChanged
    {
        add { }
        remove { }
    }

    public override bool SupportsRewindCapture => false;
    internal bool IsAlternateBufferActive => _alternateBufferActive;
    internal bool IsBracketedPasteModeEnabled => _bracketedPasteModeEnabled;

    public override int Columns { get; protected set; } = 80;
    public override int Rows { get; protected set; } = 24;

    public NativeTerminalSurface()
    {
        _eventCallback = OnNativeEvent;
        _windowProc = ChildWindowProc;
        AutomationProperties.SetAutomationId(this, "NativeTerminalSurface");
        AutomationProperties.SetName(this, "Terminal");
        ConfigureFindBar();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LayoutUpdated += OnLayoutUpdated;
        _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
    }

    public override Task InitializeAsync()
    {
        if (_disposed || _initialized || _initializationFailed)
            return Task.CompletedTask;

        try
        {
            var root = XamlRoot ?? throw new InvalidOperationException("The terminal is not attached to a XAML root.");
            _parentHwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(
                root.ContentIslandEnvironment.AppWindowId);
            if (_parentHwnd == IntPtr.Zero)
                throw new InvalidOperationException("The application window handle is not available.");

            _api = NativeTerminalApi.Instance;
            var creationSettings = new NativeTerminalApi.NativeTerminalCreationSettings(
                Columns,
                Rows,
                _scrollback,
                _fontFamily,
                ToNativePointSize(_fontSize),
                NativeTerminalThemeCatalog.Find(_theme),
                _copyOnSelect,
                _rightClickPaste,
                AllowOscClipboard: false,
                AllowOscNotifications: false,
                _readOnly);
            _terminal = _api.CreateTerminal(_parentHwnd, creationSettings, out _childHwnd);
            if (_childHwnd == IntPtr.Zero || _terminal == IntPtr.Zero)
                throw new InvalidOperationException("Microsoft Terminal returned an empty terminal handle.");

            Marshal.SetLastPInvokeError(0);
            _originalWindowProc = SetWindowLongPtr(
                _childHwnd,
                GwlWndProc,
                Marshal.GetFunctionPointerForDelegate(_windowProc));
            var windowProcError = Marshal.GetLastPInvokeError();
            if (_originalWindowProc == IntPtr.Zero && windowProcError != 0)
                throw new InvalidOperationException($"Could not subclass the terminal window (Win32 error {windowProcError}).");

            _api.RegisterEventCallback(_terminal, _eventCallback);
            _dpi = checked((int)GetDpiForWindow(_parentHwnd));
            ApplyNativeTheme();
            _initialized = true;
            UpdateBounds();
            QueueOutputFlush();
            Ready?.Invoke(Columns, Rows);
        }
        catch (Exception exception)
        {
            DestroyNativeTerminal();
            _initializationFailed = true;
            lock (_outputGate)
            {
                _pendingOutput.SetLength(0);
                _pendingOutput.Position = 0;
                _outputDispatchPending = false;
            }
            ShowInitializationError(exception.Message);
            Ready?.Invoke(Columns, Rows);
        }

        return Task.CompletedTask;
    }

    public override void WriteOutput(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty)
            return;

        var unixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            OutputObserved?.Invoke(data, unixMilliseconds);
        }
        catch
        {
            // Recording must not interrupt the live terminal data path.
        }

        if (_initializationFailed)
            return;

        // Bell events come from TerminalCore. Scanning bytes here would treat a BEL
        // that terminates an OSC sequence as an audible bell.

        lock (_outputGate)
        {
            if (_disposed || _initializationFailed)
                return;
            _pendingOutput.Write(data);
        }
        QueueOutputFlush();
    }

    public override void NotifyConnected() => _reconnectOnEnter = false;

    public override void NotifyDisconnected(string message, string action = "reconnect", bool neutral = false)
    {
        FlushOutput();
        _reconnectOnEnter = true;
        var color = neutral ? "\x1b[90m" : "\x1b[33m";
        SendDisplayText($"\r\n{color}{SanitizeNotice(message)}\x1b[0m\r\nPress Enter to {SanitizeNotice(action)}.\r\n");
    }

    public override void WriteDivider() => SendDisplayText("\r\n\x1b[90m────────────────────────────────────────\x1b[0m\r\n");

    public override void WriteNotice(string message) =>
        SendDisplayText($"\r\n\x1b[90m{SanitizeNotice(message)}\x1b[0m\r\n");

    public override void FocusTerminal()
    {
        if (!_disposed && _inputEnabled && _childHwnd != IntPtr.Zero)
        {
            ShowWindow(_childHwnd, SwShow);
            SetFocus(_childHwnd);
        }
    }

    public override void SetInputEnabled(bool enabled)
    {
        _inputEnabled = enabled;
        UpdateBounds();
    }

    public override void SetHostVisible(bool visible)
    {
        _hostVisible = visible;
        UpdateBounds(force: visible);
    }

    public override void ToggleCommandsPanel()
    {
        // HwndTerminal reports shell marks but has no commands-panel ABI.
    }

    public override void SetRulerPresentation(bool isSplit, bool isGroupFocused)
    {
        // HwndTerminal has no annotated-ruler ABI.
    }

    public override void SetPromptPlatform(string? platform)
    {
        // HwndTerminal has no prompt-platform discovery ABI.
    }

    public override void SetInitialOptions(
        int fontSize,
        string fontFamily,
        string theme,
        bool copyOnSelect,
        bool rightClickPaste,
        int scrollback,
        IReadOnlyList<object>? highlights = null,
        bool readOnly = false)
    {
        _fontSize = fontSize;
        _fontFamily = FirstFontFamily(fontFamily);
        _theme = theme;
        _copyOnSelect = copyOnSelect;
        _rightClickPaste = rightClickPaste;
        _scrollback = scrollback;
        _readOnly = readOnly;
    }

    public override void ApplyOptions(
        int? fontSize = null,
        string? fontFamily = null,
        string? theme = null,
        bool? copyOnSelect = null,
        bool? rightClickPaste = null,
        int? scrollback = null)
    {
        if (fontSize is not null)
            _fontSize = fontSize.Value;
        if (fontFamily is not null)
            _fontFamily = FirstFontFamily(fontFamily);
        if (theme is not null)
            _theme = theme;
        if (copyOnSelect is not null)
            _copyOnSelect = copyOnSelect.Value;
        if (rightClickPaste is not null)
            _rightClickPaste = rightClickPaste.Value;
        if (scrollback is not null)
            _scrollback = scrollback.Value;
        ApplyNativeTheme();
        if (_terminal != IntPtr.Zero && _api is not null)
            _api.SetInteraction(_terminal, _copyOnSelect, _rightClickPaste, _readOnly);
        UpdateBounds(force: true);
    }

    public override void ApplyHighlights(IReadOnlyList<object> rules)
    {
        // HwndTerminal has no highlight-rule ABI.
    }

    public override Task<(string Context, string? Platform)?> RequestPromptContextAsync() =>
        Task.FromResult<(string Context, string? Platform)?>(null);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        SubscribeToXamlRoot();
        UpdateBounds();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        UnsubscribeFromXamlRoot();
        // Moving a live tab between split groups can queue Unloaded after its new Loaded
        // event. Defer the decision so a stale event cannot leave the child HWND hidden.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
            {
                SubscribeToXamlRoot();
                UpdateBounds(force: true);
            }
            else if (_childHwnd != IntPtr.Zero)
            {
                ShowWindow(_childHwnd, SwHide);
            }
        });
    }

    private void OnLayoutUpdated(object? sender, object args) => UpdateBounds();

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property) => UpdateBounds();

    private void SubscribeToXamlRoot()
    {
        if (ReferenceEquals(_subscribedRoot, XamlRoot))
            return;
        UnsubscribeFromXamlRoot();
        _subscribedRoot = XamlRoot;
        if (_subscribedRoot is not null)
            _subscribedRoot.Changed += OnXamlRootChanged;
    }

    private void UnsubscribeFromXamlRoot()
    {
        if (_subscribedRoot is not null)
            _subscribedRoot.Changed -= OnXamlRootChanged;
        _subscribedRoot = null;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateBounds(force: true);

    private void UpdateBounds(bool force = false)
    {
        if (!_initialized || _childHwnd == IntPtr.Zero || XamlRoot?.Content is not FrameworkElement rootContent)
            return;

        if (!IsLoaded || !_hostVisible || Visibility != Visibility.Visible || !_inputEnabled
            || ActualWidth <= 0 || ActualHeight <= 0)
        {
            ShowWindow(_childHwnd, SwHide);
            return;
        }

        var findHeight = _findBar.Visibility == Visibility.Visible ? _findBar.Height : 0;
        Windows.Foundation.Rect bounds;
        try
        {
            bounds = TransformToVisual(rootContent).TransformBounds(
                new Windows.Foundation.Rect(0, findHeight, ActualWidth, Math.Max(0, ActualHeight - findHeight)));
        }
        catch (InvalidOperationException)
        {
            ShowWindow(_childHwnd, SwHide);
            return;
        }

        var left = Math.Max(0, bounds.Left);
        var top = Math.Max(0, bounds.Top);
        var right = Math.Min(rootContent.ActualWidth, bounds.Right);
        var bottom = Math.Min(rootContent.ActualHeight, bounds.Bottom);
        var scale = XamlRoot.RasterizationScale;
        var x = checked((int)Math.Round(left * scale));
        var y = checked((int)Math.Round(top * scale));
        var width = checked((int)Math.Round(Math.Max(0, right - left) * scale));
        var height = checked((int)Math.Round(Math.Max(0, bottom - top) * scale));
        if (width == 0 || height == 0)
        {
            ShowWindow(_childHwnd, SwHide);
            return;
        }

        var newDpi = checked((int)GetDpiForWindow(_parentHwnd));
        if (newDpi != _dpi)
        {
            _dpi = newDpi;
            ApplyNativeTheme();
            force = true;
        }

        if (!force && x == _lastX && y == _lastY && width == _lastWidth && height == _lastHeight)
            return;

        _lastX = x;
        _lastY = y;
        _lastWidth = width;
        _lastHeight = height;
        TraceHook?.Invoke(
            $"NativeTerminal bounds dip=({bounds.X:F1},{bounds.Y:F1},{bounds.Width:F1},{bounds.Height:F1}) " +
            $"root=({rootContent.ActualWidth:F1},{rootContent.ActualHeight:F1}) scale={scale:F2} " +
            $"px=({x},{y},{width},{height}) hwnd=0x{_parentHwnd.ToInt64():X}");
        // The native resize operation always moves its child window to (0, 0).
        // Position it after that call so the HWND follows the XAML element.
        var dimensions = _api!.ResizePixels(_terminal, width, height);
        SetWindowPos(
            _childHwnd,
            IntPtr.Zero,
            x,
            y,
            width,
            height,
            SwpNoActivate | SwpNoZOrder | SwpShowWindow);
        if (dimensions.X > 0 && dimensions.Y > 0
            && (Columns != dimensions.X || Rows != dimensions.Y))
        {
            Columns = dimensions.X;
            Rows = dimensions.Y;
            Resized?.Invoke(Columns, Rows);
        }
    }
    private void QueueOutputFlush()
    {
        bool queue;
        lock (_outputGate)
        {
            queue = !_disposed && !_outputDispatchPending && _pendingOutput.Length > 0;
            if (queue)
                _outputDispatchPending = true;
        }

        if (queue && !DispatcherQueue.TryEnqueue(FlushOutput))
        {
            lock (_outputGate)
                _outputDispatchPending = false;
        }
    }

    private void FlushOutput()
    {
        if (_terminal == IntPtr.Zero || _api is null)
        {
            lock (_outputGate)
                _outputDispatchPending = false;
            return;
        }

        byte[]? bytes = null;
        char[]? chars = null;
        try
        {
            int byteCount;
            lock (_outputGate)
            {
                _outputDispatchPending = false;
                byteCount = checked((int)_pendingOutput.Length);
                if (byteCount == 0)
                    return;
                bytes = ArrayPool<byte>.Shared.Rent(byteCount);
                _pendingOutput.GetBuffer().AsSpan(0, byteCount).CopyTo(bytes);
                _pendingOutput.SetLength(0);
                _pendingOutput.Position = 0;
            }

            chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(byteCount));
            var charCount = _outputDecoder.GetChars(bytes.AsSpan(0, byteCount), chars, flush: false);
            if (charCount > 0)
                _api.SendOutput(_terminal, new string(chars, 0, charCount));
        }
        finally
        {
            if (chars is not null)
                ArrayPool<char>.Shared.Return(chars);
            if (bytes is not null)
                ArrayPool<byte>.Shared.Return(bytes);
        }

        QueueOutputFlush();
    }

    private void SendDisplayText(string text)
    {
        if (_terminal != IntPtr.Zero && _api is not null)
            _api.SendOutput(_terminal, text);
    }

    private void OnNativeEvent(IntPtr context, in NativeTerminalApi.NativeEvent eventData)
    {
        try
        {
            if (_disposed
                || eventData.StructSize < Marshal.SizeOf<NativeTerminalApi.NativeEvent>()
                || eventData.AbiMajor != NativeTerminalApi.AbiMajor
                || eventData.Sequence == 0
                || eventData.Sequence <= _lastNativeEventSequence
                || eventData.TextLength > 16 * 1024 * 1024
                || eventData.HtmlLength > 16 * 1024 * 1024
                || eventData.RtfLength > 16 * 1024 * 1024
                || (eventData.TextLength > 0 && eventData.Text == IntPtr.Zero)
                || (eventData.HtmlLength > 0 && eventData.Html == IntPtr.Zero)
                || (eventData.RtfLength > 0 && eventData.Rtf == IntPtr.Zero))
            {
                return;
            }

            _lastNativeEventSequence = eventData.Sequence;
            var eventType = (NativeTerminalApi.NativeEventType)eventData.Type;
            switch (eventType)
            {
                case NativeTerminalApi.NativeEventType.Input:
                    OnNativeInput(ReadEventText(in eventData));
                    break;
                case NativeTerminalApi.NativeEventType.ClipboardCopy:
                {
                    var text = ReadEventText(in eventData);
                    var html = eventData.HtmlLength == 0
                        ? null
                        : Marshal.PtrToStringUTF8(eventData.Html, checked((int)eventData.HtmlLength));
                    var rtf = eventData.RtfLength == 0
                        ? null
                        : Marshal.PtrToStringUTF8(eventData.Rtf, checked((int)eventData.RtfLength));
                    DispatcherQueue.TryEnqueue(() => CopyToClipboard(text, html, rtf));
                    break;
                }
                case NativeTerminalApi.NativeEventType.ClipboardPasteRequest:
                    DispatcherQueue.TryEnqueue(() => _ = PasteFromClipboardAsync());
                    break;
                case NativeTerminalApi.NativeEventType.TitleChanged:
                {
                    var title = ReadEventText(in eventData);
                    if (title.Length > 512)
                        title = title[..512];
                    DispatcherQueue.TryEnqueue(() => TitleChanged?.Invoke(title));
                    break;
                }
                case NativeTerminalApi.NativeEventType.WorkingDirectoryChanged:
                {
                    var workingDirectory = ReadEventText(in eventData);
                    DispatcherQueue.TryEnqueue(() => WorkingDirectoryReported?.Invoke(workingDirectory));
                    break;
                }
                case NativeTerminalApi.NativeEventType.Bell:
                    DispatcherQueue.TryEnqueue(() => BellReceived?.Invoke());
                    break;
                case NativeTerminalApi.NativeEventType.BufferOrViewportChanged:
                    if (_findBar.Visibility == Visibility.Visible
                        && _findInput.Text.Length > 0
                        && !_searchRefreshPending)
                    {
                        _searchRefreshPending = true;
                        if (!DispatcherQueue.TryEnqueue(() =>
                        {
                            _searchRefreshPending = false;
                            RunFind(forward: true, execute: false);
                        }))
                        {
                            _searchRefreshPending = false;
                        }
                    }
                    break;
                case NativeTerminalApi.NativeEventType.AlternateBufferChanged:
                    _alternateBufferActive = (eventData.Flags & 1) != 0;
                    break;
                case NativeTerminalApi.NativeEventType.ShellIntegrationMarkChanged:
                {
                    var action = _pendingShellMarkAction;
                    _pendingShellMarkAction = null;
                    if (action == 'C')
                    {
                        var command = ReadEventText(in eventData);
                        if (command.Length > 512)
                            command = command[..512];
                        if (!string.IsNullOrWhiteSpace(command))
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                CommandObserved?.Invoke(command);
                                CommandChanged?.Invoke(command);
                            });
                        }
                    }
                    break;
                }
                case NativeTerminalApi.NativeEventType.TerminalModeChanged:
                    if (eventData.Value0 == 2)
                        _bracketedPasteModeEnabled = (eventData.Flags & 1) != 0;
                    break;
                case NativeTerminalApi.NativeEventType.OscObserved:
                    ObserveOsc(eventData.Value0, ReadEventText(in eventData));
                    break;
                case NativeTerminalApi.NativeEventType.OpenLink:
                {
                    var uri = ReadEventText(in eventData);
                    DispatcherQueue.TryEnqueue(() => TerminalLinkPolicy.Open(uri, TraceHook));
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            // Managed exceptions must never cross the native callback boundary.
            try { TraceHook?.Invoke($"native event callback failed: {exception.Message}"); }
            catch { }
        }
    }

    private static string ReadEventText(in NativeTerminalApi.NativeEvent eventData) =>
        eventData.TextLength == 0
            ? string.Empty
            : Marshal.PtrToStringUni(eventData.Text, checked((int)eventData.TextLength)) ?? string.Empty;

    private void ObserveOsc(long codeValue, string payload)
    {
        if (codeValue is < 0 or > int.MaxValue)
            return;

        var code = (int)codeValue;
        switch (code)
        {
            case 7 when IsValidOscPayload(payload, 2048):
                DispatcherQueue.TryEnqueue(() => WorkingDirectoryReported?.Invoke(payload));
                break;
            case 133 when IsValidOscPayload(payload, 4096):
            {
                var separator = payload.IndexOf(';');
                var action = separator < 0 ? payload : payload[..separator];
                _pendingShellMarkAction = action is "A" or "B" or "C" or "D" ? action[0] : null;
                if (_pendingShellMarkAction == 'D')
                    DispatcherQueue.TryEnqueue(() => CommandChanged?.Invoke(string.Empty));
                break;
            }
            case 3008 when IsValidOscPayload(payload, 4096):
                DispatcherQueue.TryEnqueue(() => ContextReported?.Invoke(payload));
                break;
            case 7377 or 9 or 777 when IsValidOscPayload(payload, 2048):
                DispatcherQueue.TryEnqueue(() => AgentOscReceived?.Invoke(code, payload));
                break;
        }
    }

    private static bool IsValidOscPayload(string payload, int maximumLength)
    {
        if (payload.Length > maximumLength)
            return false;
        foreach (var character in payload)
        {
            if (character < ' ' || character == '\u007f')
                return false;
        }
        return true;
    }

    private static void CopyToClipboard(string text, string? html, string? rtf)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            if (!string.IsNullOrEmpty(html))
                package.SetHtmlFormat(html);
            if (!string.IsNullOrEmpty(rtf))
                package.SetRtf(rtf);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native clipboard copy failed: {exception.Message}");
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        try
        {
            if (_disposed || _readOnly || _terminal == IntPtr.Zero || _api is null)
                return;
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return;
            var text = await content.GetTextAsync();
            if (!_disposed && !string.IsNullOrEmpty(text) && _terminal != IntPtr.Zero)
                _api.PasteText(_terminal, text);
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native clipboard paste failed: {exception.Message}");
        }
    }

    private void OnNativeInput(string data)
    {
        if (_disposed || !_inputEnabled || _readOnly || string.IsNullOrEmpty(data))
            return;
        if (_reconnectOnEnter && data.IndexOfAny(['\r', '\n']) >= 0)
        {
            _reconnectOnEnter = false;
            ReconnectRequested?.Invoke();
            return;
        }
        InputReceived?.Invoke(Encoding.UTF8.GetBytes(data));
    }

    private IntPtr ChildWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return ChildWindowProcCore(hwnd, message, wParam, lParam);
        }
        catch (Exception exception)
        {
            // Managed exceptions must never cross the Win32 window procedure boundary.
            try { TraceHook?.Invoke($"native window procedure failed: {exception.Message}"); }
            catch { }
            return _originalWindowProc == IntPtr.Zero
                ? DefWindowProc(hwnd, message, wParam, lParam)
                : CallWindowProc(_originalWindowProc, hwnd, message, wParam, lParam);
        }
    }

    private IntPtr ChildWindowProcCore(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_api is not null && _terminal != IntPtr.Zero)
        {
            switch (message)
            {
                case WmSetFocus:
                    _api.SetFocused(_terminal, true);
                    break;
                case WmKillFocus:
                    _api.SetFocused(_terminal, false);
                    break;
                case WmMouseActivate:
                    SetFocus(hwnd);
                    break;
                case WmLeftButtonDown:
                case WmRightButtonDown:
                case WmMiddleButtonDown:
                case WmXButtonDown:
                    RequestHostFocus();
                    SetFocus(hwnd);
                    break;
                case WmKeyDown:
                case WmSysKeyDown:
                    if (TryHandleAppShortcut(checked((ushort)wParam.ToInt64())))
                        return IntPtr.Zero;
                    UnpackKeyMessage(wParam, lParam, out var downKey, out var downScan, out var downFlags);
                    _api.SendKeyEvent(_terminal, downKey, downScan, downFlags, true);
                    return IntPtr.Zero;
                case WmKeyUp:
                case WmSysKeyUp:
                    UnpackKeyMessage(wParam, lParam, out var upKey, out var upScan, out var upFlags);
                    _api.SendKeyEvent(_terminal, upKey, upScan, upFlags, false);
                    return IntPtr.Zero;
                case WmChar:
                    if (_suppressNextCharacter)
                    {
                        _suppressNextCharacter = false;
                        return IntPtr.Zero;
                    }
                    UnpackKeyMessage(wParam, lParam, out var character, out var charScan, out var charFlags);
                    _api.SendCharEvent(_terminal, (char)character, charScan, charFlags);
                    return IntPtr.Zero;
            }
        }

        return _originalWindowProc == IntPtr.Zero
            ? DefWindowProc(hwnd, message, wParam, lParam)
            : CallWindowProc(_originalWindowProc, hwnd, message, wParam, lParam);
    }

    private bool TryHandleAppShortcut(ushort virtualKey)
    {
        var control = (GetKeyState(0x11) & 0x8000) != 0;
        var shift = (GetKeyState(0x10) & 0x8000) != 0;
        if (!control)
            return false;
        if (shift && virtualKey == 0x46)
        {
            _suppressNextCharacter = true;
            DispatcherQueue.TryEnqueue(OpenFind);
            return true;
        }
        if (shift && virtualKey == 0x43
            && _api?.CopySelection(_terminal, clearSelection: false) == true)
        {
            _suppressNextCharacter = true;
            return true;
        }
        if (shift && virtualKey == 0x56)
        {
            _suppressNextCharacter = true;
            _ = PasteFromClipboardAsync();
            return true;
        }

        Action? action = virtualKey switch
        {
            0x73 => CloseTabRequested,
            0xDC when shift => SplitRequested,
            0x45 when shift => FilePaneRequested,
            0x54 when shift => NewLocalTabRequested,
            0x50 when shift => CommandPaletteRequested,
            0x4B when shift => QuickConnectRequested,
            _ => null,
        };
        if (action is null)
            return false;

        _suppressNextCharacter = virtualKey != 0x73; // F4 does not produce WM_CHAR.
        action.Invoke();
        return true;
    }

    private static void UnpackKeyMessage(
        IntPtr wParam,
        IntPtr lParam,
        out ushort virtualKey,
        out ushort scanCode,
        out ushort flags)
    {
        var scanCodeAndFlags = ((ulong)lParam.ToInt64() >> 16) & 0xFFFF;
        scanCode = (ushort)(scanCodeAndFlags & 0x00FF);
        flags = (ushort)(scanCodeAndFlags & 0xFF00);
        virtualKey = checked((ushort)wParam.ToInt64());
    }

    private void ConfigureFindBar()
    {
        _findBar.Height = 40;
        _findBar.Padding = new Thickness(8, 4, 8, 4);
        _findBar.VerticalAlignment = VerticalAlignment.Top;
        _findBar.HorizontalAlignment = HorizontalAlignment.Stretch;
        _findBar.BorderThickness = new Thickness(0, 0, 0, 1);
        _findBar.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(64, 128, 128, 128));
        _findBar.Visibility = Visibility.Collapsed;
        _findBar.SetValue(Canvas.ZIndexProperty, 1);

        _findBar.KeyDown += OnFindRowKeyDown;
        _findInput.Width = 240;
        _findInput.PlaceholderText = "Find";
        _findInput.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetAutomationId(_findInput, "NativeTerminalFindInput");
        AutomationProperties.SetName(_findInput, "Find");
        _findInput.TextChanged += (_, _) => RunFind(forward: true, execute: false);
        _findInput.KeyDown += OnFindInputKeyDown;

        _findCount.Width = 88;
        _findCount.VerticalAlignment = VerticalAlignment.Center;
        _findCount.TextAlignment = TextAlignment.Center;
        AutomationProperties.SetAutomationId(_findCount, "NativeTerminalFindCount");
        AutomationProperties.SetAccessibilityView(_findCount, AccessibilityView.Content);

        ConfigureFindToggle(_findCase, "Aa", "Match case", "NativeTerminalFindCase");
        ConfigureFindToggle(_findRegex, ".*", "Use regular expression", "NativeTerminalFindRegex");
        _findCase.Click += (_, _) => RunFind(forward: true, execute: false);
        _findRegex.Click += (_, _) => RunFind(forward: true, execute: false);

        var previous = CreateFindButton("\uE72B", "Previous match", "NativeTerminalFindPrevious");
        previous.Click += (_, _) => RunFind(forward: false, execute: true);
        var next = CreateFindButton("\uE72A", "Next match", "NativeTerminalFindNext");
        next.Click += (_, _) => RunFind(forward: true, execute: true);
        var close = CreateFindButton("\uE711", "Close find", "NativeTerminalFindClose");
        close.Click += (_, _) => CloseFind();

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(_findInput);
        row.Children.Add(_findCount);
        row.Children.Add(_findCase);
        row.Children.Add(_findRegex);
        row.Children.Add(previous);
        row.Children.Add(next);
        row.Children.Add(close);
        _findBar.Child = row;
        Children.Add(_findBar);
    }

    private static void ConfigureFindToggle(ToggleButton button, string content, string name, string automationId)
    {
        button.Content = content;
        button.MinWidth = 32;
        button.Height = 30;
        button.Margin = new Thickness(2, 0, 0, 0);
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, automationId);
    }

    private static Button CreateFindButton(string glyph, string name, string automationId)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            MinWidth = 32,
            Height = 30,
            Margin = new Thickness(2, 0, 0, 0),
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private void OpenFind()
    {
        if (_disposed)
            return;
        _findBar.Visibility = Visibility.Visible;
        UpdateBounds(force: true);
        _findInput.Focus(FocusState.Programmatic);
        _findInput.SelectAll();
        if (_findInput.Text.Length > 0)
            RunFind(forward: true, execute: false);
    }

    private void CloseFind()
    {
        if (_findBar.Visibility != Visibility.Visible)
            return;
        if (_terminal != IntPtr.Zero && _api is not null)
            _api.ClearSearch(_terminal);
        _findCount.Text = "";
        _findBar.Visibility = Visibility.Collapsed;
        UpdateBounds(force: true);
        FocusTerminal();
    }

    private void OnFindInputKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            var shift = (GetKeyState(0x10) & 0x8000) != 0;
            RunFind(forward: !shift, execute: true);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Escape)
        {
            CloseFind();
            args.Handled = true;
        }
    }

    private void OnFindRowKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            CloseFind();
            args.Handled = true;
            return;
        }
        var control = (GetKeyState(0x11) & 0x8000) != 0;
        var shift = (GetKeyState(0x10) & 0x8000) != 0;
        if (control && shift && args.Key == VirtualKey.F)
        {
            _findInput.Focus(FocusState.Programmatic);
            _findInput.SelectAll();
            args.Handled = true;
        }
    }


    private void RunFind(bool forward, bool execute)
    {
        if (_terminal == IntPtr.Zero || _api is null || _findBar.Visibility != Visibility.Visible)
            return;
        var query = _findInput.Text;
        if (query.Length == 0)
        {
            _api.ClearSearch(_terminal);
            _findCount.Text = "";
            return;
        }

        var state = _api.Search(
            _terminal,
            query,
            forward,
            _findCase.IsChecked == true,
            _findRegex.IsChecked == true,
            execute,
            scrollIntoView: true);
        _findCount.Text = state.InvalidRegex
            ? "Bad regex"
            : state.TotalMatches == 0
                ? "No results"
                : $"{Math.Max(0, state.CurrentMatch) + 1} of {(state.TotalMatches > 999 ? "999+" : state.TotalMatches)}";
        _findInput.Focus(FocusState.Programmatic);
    }

    private void ApplyNativeTheme()
    {
        if (_terminal == IntPtr.Zero || _api is null)
            return;
        _api.SetTheme(
            _terminal,
            NativeTerminalThemeCatalog.Find(_theme),
            _fontFamily,
            ToNativePointSize(_fontSize),
            _dpi);
    }

    // xterm.js sizes fonts in CSS pixels. Microsoft Terminal sizes fonts in points.
    private static short ToNativePointSize(int cssPixels) =>
        checked((short)Math.Max(1, (cssPixels * 3 + 2) / 4));

    private static string FirstFontFamily(string families)
    {
        foreach (var family in families.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = family.Trim('\'', '"');
            if (!candidate.Equals("monospace", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return "Cascadia Mono";
    }

    private static string SanitizeNotice(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(character < ' ' && character != '\t' ? ' ' : character);
        return builder.ToString();
    }

    private void ShowInitializationError(string message)
    {
        var error = new TextBlock
        {
            Text = "Native terminal initialization failed.\n\n" + message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(error, "Native terminal initialization error");
        Children.Clear();
        Children.Add(error);
    }

    private void DestroyNativeTerminal()
    {
        if (_terminal != IntPtr.Zero && _api is not null)
            _api.DestroyTerminal(_terminal);
        _terminal = IntPtr.Zero;
        _childHwnd = IntPtr.Zero;
        _originalWindowProc = IntPtr.Zero;
        _initialized = false;
    }

    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        LayoutUpdated -= OnLayoutUpdated;
        UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
        UnsubscribeFromXamlRoot();
        DestroyNativeTerminal();
        lock (_outputGate)
        {
            _pendingOutput.SetLength(0);
            _outputDispatchPending = false;
        }
        _pendingOutput.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr previousWindowProc,
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
