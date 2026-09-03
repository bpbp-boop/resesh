using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinRT;

namespace Resesh.Terminal;

/// <summary>
/// WinUI host for Microsoft Terminal's composition-surface renderer.
/// </summary>
public sealed class NativeTerminalSurface : TerminalSurface
{
    private const int PromptParseWindow = 4096;
    private const int MaximumCommandLength = 512;

    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmRightButtonDown = 0x0204;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmMiddleButtonDown = 0x0207;
    private const uint WmMiddleButtonUp = 0x0208;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmXButtonUp = 0x020C;
    private const uint WmMouseHorizontalWheel = 0x020E;
    private const uint WmMouseLeave = 0x02A3;
    private const uint MkLeftButton = 0x0001;
    private const uint MkRightButton = 0x0002;
    private const uint MkShift = 0x0004;
    private const uint MkControl = 0x0008;
    private const uint MkMiddleButton = 0x0010;
    private const uint MkXButton1 = 0x0020;
    private const uint MkXButton2 = 0x0040;
    private const uint PointerHandled = 0x01;
    private const uint PointerCapture = 0x02;
    private const uint PointerRelease = 0x04;
    private const uint PointerHandCursor = 0x08;

    private readonly object _outputGate = new();
    private readonly MemoryStream _pendingOutput = new();
    private readonly Decoder _outputDecoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly NativeTerminalApi.EventCallback _eventCallback;
    private readonly SwapChainPanel _terminalPanel = new();
    private readonly InputCursor _textCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private readonly InputCursor _linkCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private readonly Border _findBar = new();
    private readonly TextBox _findInput = new();
    private readonly TextBlock _findCount = new();
    private readonly ToggleButton _findCase = new();
    private readonly ToggleButton _findRegex = new();
    private readonly NativeTerminalRuler _ruler = new();
    private readonly NativeTerminalCommandsPanel _commandsPanel = new();
    private readonly Dictionary<ulong, CancellationTokenSource> _promptProbes = [];
    private readonly Dictionary<ulong, string> _probeCommands = [];
    private readonly Dictionary<ulong, int?> _probeExitCodes = [];
    private readonly Dictionary<string, ulong> _osc3008Probes = new(StringComparer.Ordinal);
    private static readonly Regex PromptCommandPattern = new(
        @"^(?<prompt>(?:PS [^\n]{0,200}>|[^@\s$#%>]{1,100}@[^@\s$#%>]{1,100}\s+[^\r\n$#%>]{1,160}?\s*[$#%>]|(?:\[[^\]]{1,100}\]|[^\s$#%>]{0,100})[$#%>]))\s?(?<command>\S.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex WindowsPromptContextPattern = new(
        @"^(?:PS )?(?<context>(?:[A-Za-z]:[\\/]|\\\\)[^\r\n>]*)>$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UnixBracketedPromptContextPattern = new(
        @"^\[[^@\]\s]{1,100}@[^\]\s]{1,100}\s+(?<context>[^\]\r\n]{1,160})\]\s*[$#%>]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UnixSpacedPromptContextPattern = new(
        @"^[^@\s$#%>]{1,100}@[^\s$#%>]{1,100}\s+(?<context>[^\r\n$#%>]{1,160}?)\s*[$#%>]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CiscoPromptContextPattern = new(
        @"^(?:RP/\d+/(?:RP|RSP)\d+/CPU\d+:)?[A-Za-z0-9][A-Za-z0-9._-]{0,100}(?:\((?<mode>[^()\r\n]{1,100})\))?(?<terminator>[#>])$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JuniperPromptContextPattern = new(
        @"^[^@\s]+@[^\s#>]+(?<terminator>[#>])$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NokiaPromptContextPattern = new(
        @"^[A-Z]:[^@\s]+@[^\s#]+#$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private NativeTerminalApi? _api;
    private IntPtr _hostHwnd;
    private IntPtr _terminal;
    private long _visibilityCallbackToken;
    private bool _outputDispatchPending;
    private bool _initialized;
    private bool _initializationFailed;
    private bool _inputEnabled = true;
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
    private IReadOnlyList<NativeTerminalApi.MarkRecord> _marks = [];
    private int _viewTop;
    private int _bufferHeight = 24;
    private long _titleEpoch;
    private bool _commandsPanelOpen;
    private bool _exactShellMarksSeen;
    private string? _promptPlatform;
    private string _promptContext = string.Empty;
    private string? _promptContextPlatform;
    private bool _annotationRefreshPending;
    private ulong _commandsFingerprint = ulong.MaxValue;
    private ulong _lastPromptProbeId;

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
    public override event Action<string, string?>? PromptContextChanged;
    public override event Action<string>? WorkingDirectoryReported;
    public override event Action<string>? ContextReported;
    public override event Action<int, string>? AgentOscReceived;
    public override event Action? BellReceived;
    public override event Action<string>? CommandObserved;
    public override event Action<bool>? CommandsPanelOpenChanged;

    public override bool SupportsRewindCapture => false;
    internal bool IsAlternateBufferActive => _alternateBufferActive;
    internal bool IsBracketedPasteModeEnabled => _bracketedPasteModeEnabled;

    public override int Columns { get; protected set; } = 80;
    public override int Rows { get; protected set; } = 24;

    public NativeTerminalSurface()
    {
        _eventCallback = OnNativeEvent;
        AutomationProperties.SetAutomationId(this, "NativeTerminalSurface");
        AutomationProperties.SetName(this, "Terminal");
        IsTabStop = true;
        _terminalPanel.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _terminalPanel.PointerPressed += OnPointerPressed;
        _terminalPanel.PointerReleased += OnPointerReleased;
        _terminalPanel.PointerMoved += OnPointerMoved;
        _terminalPanel.PointerWheelChanged += OnPointerWheelChanged;
        _terminalPanel.PointerExited += OnPointerExited;
        Children.Add(_terminalPanel);
        ConfigureFindBar();
        ConfigureRuler();
        ConfigureCommandsPanel();

        KeyDown += OnTerminalKeyDown;
        KeyUp += OnTerminalKeyUp;
        CharacterReceived += OnTerminalCharacterReceived;
        GotFocus += OnTerminalGotFocus;
        LostFocus += OnTerminalLostFocus;
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
            _hostHwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(
                root.ContentIslandEnvironment.AppWindowId);
            if (_hostHwnd == IntPtr.Zero)
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
            _terminal = _api.CreateTerminal(_hostHwnd, creationSettings);
            if (_terminal == IntPtr.Zero)
                throw new InvalidOperationException("Microsoft Terminal returned an empty terminal handle.");
            _api.RegisterEventCallback(_terminal, _eventCallback);
            _dpi = checked((int)GetDpiForWindow(_hostHwnd));
            ApplyNativeTheme();
            _initialized = true;
            _ruler.UpdateViewport(0, Rows, Rows, alternateBuffer: false);
            RefreshAnnotations();
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
        if (!_disposed && _inputEnabled)
            Focus(FocusState.Programmatic);
    }

    public override void SetInputEnabled(bool enabled)
    {
        _inputEnabled = enabled;
        _terminalPanel.IsHitTestVisible = enabled;
    }
    public override void ToggleCommandsPanel() => SetCommandsPanelOpen(!_commandsPanelOpen);

    public override void SetRulerPresentation(bool isSplit, bool isGroupFocused) =>
        _ruler.SetPresentation(isSplit, isGroupFocused);

    public override void SetPromptPlatform(string? platform) =>
        _promptPlatform = platform;

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

    public override Task<(string Context, string? Platform)?> RequestPromptContextAsync()
    {
        if (_terminal != IntPtr.Zero && _api is not null && !_alternateBufferActive)
        {
            try
            {
                var probe = _api.BeginPromptProbe(_terminal);
                var parsed = ParsePromptContextLine(probe.Text);
                if (probe.Id != 0 && !_promptProbes.ContainsKey(probe.Id))
                    _api.DiscardPromptProbe(_terminal, probe.Id);
                if (parsed is { } current)
                {
                    _promptContext = current.Context;
                    _promptContextPlatform = current.Platform;
                    return Task.FromResult<(string Context, string? Platform)?>(current);
                }
            }
            catch (Exception exception)
            {
                TraceHook?.Invoke($"native prompt context request failed: {exception.Message}");
            }
        }
        return Task.FromResult<(string Context, string? Platform)?>(
            string.IsNullOrEmpty(_promptContext) ? null : (_promptContext, _promptContextPlatform));
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        SubscribeToXamlRoot();
        UpdateBounds();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        UnsubscribeFromXamlRoot();
        // Moving a live tab between split groups can queue Unloaded after its new Loaded
        // event. Defer the decision so a stale event cannot detach the active surface.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
            {
                SubscribeToXamlRoot();
                UpdateBounds(force: true);
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
        if (!_initialized || _terminal == IntPtr.Zero || XamlRoot?.Content is not FrameworkElement rootContent)
            return;

        var visible = IsLoaded
            && Visibility == Visibility.Visible
            && ActualWidth > 0
            && ActualHeight > 0;
        _terminalPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;

        var findHeight = _findBar.Visibility == Visibility.Visible ? _findBar.Height : 0;
        var rulerWidth = _alternateBufferActive ? 0 : _ruler.Width;
        var commandsWidth = _commandsPanelOpen && !_alternateBufferActive
            ? Math.Min(NativeTerminalCommandsPanel.PreferredWidth, ActualWidth * 0.7)
            : 0;
        if (commandsWidth > 0)
            _commandsPanel.Width = commandsWidth;
        var chromeWidth = rulerWidth + commandsWidth;
        _terminalPanel.Margin = new Thickness(0, findHeight, chromeWidth, 0);
        _ruler.Margin = new Thickness(0, findHeight, 0, 0);
        _commandsPanel.Margin = new Thickness(0, findHeight, rulerWidth, 0);
        _findBar.Margin = new Thickness(0, 0, chromeWidth, 0);

        Windows.Foundation.Rect bounds;
        try
        {
            bounds = _terminalPanel.TransformToVisual(rootContent).TransformBounds(
                new Windows.Foundation.Rect(0, 0, _terminalPanel.ActualWidth, _terminalPanel.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var scale = XamlRoot.RasterizationScale;
        var x = checked((int)Math.Round(bounds.X * scale));
        var y = checked((int)Math.Round(bounds.Y * scale));
        var width = checked((int)Math.Round(Math.Max(0, bounds.Width) * scale));
        var height = checked((int)Math.Round(Math.Max(0, bounds.Height) * scale));
        if (width == 0 || height == 0)
            return;

        var screenOrigin = new NativePoint { X = x, Y = y };
        if (!ClientToScreen(_hostHwnd, ref screenOrigin))
            return;

        var newDpi = checked((int)GetDpiForWindow(_hostHwnd));
        if (newDpi != _dpi)
        {
            _dpi = newDpi;
            ApplyNativeTheme();
            force = true;
        }

        if (!force
            && screenOrigin.X == _lastX
            && screenOrigin.Y == _lastY
            && width == _lastWidth
            && height == _lastHeight)
        {
            return;
        }

        _lastX = screenOrigin.X;
        _lastY = screenOrigin.Y;
        _lastWidth = width;
        _lastHeight = height;
        TraceHook?.Invoke(
            $"NativeTerminal bounds dip=({bounds.X:F1},{bounds.Y:F1},{bounds.Width:F1},{bounds.Height:F1}) " +
            $"root=({rootContent.ActualWidth:F1},{rootContent.ActualHeight:F1}) scale={scale:F2} " +
            $"screen=({_lastX},{_lastY},{width},{height}) hwnd=0x{_hostHwnd.ToInt64():X}");
        var dimensions = _api!.SetBounds(_terminal, _lastX, _lastY, width, height);
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
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _titleEpoch++;
                        TitleChanged?.Invoke(title);
                    });
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
                {
                    var viewTop = checked((int)eventData.Value0);
                    var viewportHeight = checked((int)eventData.Value1);
                    var bufferHeight = checked((int)eventData.Value2);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _viewTop = viewTop;
                        _bufferHeight = bufferHeight;
                        _ruler.UpdateViewport(viewTop, viewportHeight, bufferHeight, _alternateBufferActive);
                        QueueAnnotationRefresh();
                        if (_findBar.Visibility == Visibility.Visible
                            && _findInput.Text.Length > 0
                            && !_searchRefreshPending)
                        {
                            _searchRefreshPending = true;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                _searchRefreshPending = false;
                                RunFind(forward: true, execute: false);
                            });
                        }
                    });
                    break;
                }
                case NativeTerminalApi.NativeEventType.AlternateBufferChanged:
                {
                    var active = (eventData.Flags & 1) != 0;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _alternateBufferActive = active;
                        _ruler.UpdateViewport(_viewTop, Rows, _bufferHeight, active);
                        _commandsPanel.Visibility = _commandsPanelOpen && !active
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                        UpdateBounds(force: true);
                        if (!active)
                            QueueAnnotationRefresh();
                    });
                    break;
                }
                case NativeTerminalApi.NativeEventType.ShellIntegrationMarkChanged:
                {
                    var action = _pendingShellMarkAction;
                    _pendingShellMarkAction = null;
                    var command = ReadEventText(in eventData);
                    if (command.Length > MaximumCommandLength)
                        command = command[..MaximumCommandLength];
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        CancelPromptProbes();
                        if (action == 'C' && !string.IsNullOrWhiteSpace(command))
                        {
                            CommandObserved?.Invoke(command);
                            CommandChanged?.Invoke(command);
                        }
                        RefreshAnnotations();
                    });
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
                case NativeTerminalApi.NativeEventType.SwapChainChanged:
                    DispatcherQueue.TryEnqueue(AttachSwapChainPanel);
                    break;
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
                _exactShellMarksSeen = _pendingShellMarkAction is not null;
                if (_pendingShellMarkAction == 'D')
                    DispatcherQueue.TryEnqueue(() => CommandChanged?.Invoke(string.Empty));
                break;
            }
            case 3008 when IsValidOscPayload(payload, 4096):
                DispatcherQueue.TryEnqueue(() =>
                {
                    ObserveOsc3008(payload);
                    ContextReported?.Invoke(payload);
                });
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

    private static bool CopyToClipboard(string text, string? html, string? rtf)
    {
        if (string.IsNullOrEmpty(text))
            return false;

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
            return true;
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native clipboard copy failed: {exception.Message}");
            return false;
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

    private void AttachSwapChainPanel()
    {
        if (_disposed || _terminal == IntPtr.Zero || _api is null)
            return;
        try
        {
            var nativePanel = ((IWinRTObject)_terminalPanel).NativeObject;
            _api.AttachSwapChainPanel(_terminal, nativePanel.ThisPtr);
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native swap chain attach failed: {exception.Message}");
        }
    }

    private void OnTerminalGotFocus(object sender, RoutedEventArgs args)
    {
        if (_terminal != IntPtr.Zero && _api is not null)
            _api.SetFocused(_terminal, true);
    }

    private void OnTerminalLostFocus(object sender, RoutedEventArgs args)
    {
        if (_terminal != IntPtr.Zero && _api is not null)
            _api.SetFocused(_terminal, false);
    }

    private void OnTerminalKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_disposed || !_inputEnabled || _terminal == IntPtr.Zero || _api is null)
            return;

        var virtualKey = checked((ushort)args.Key);
        if (TryHandleAppShortcut(virtualKey))
        {
            args.Handled = true;
            return;
        }
        if (args.Key == VirtualKey.Enter)
            BeginPromptDiscovery();
        _api.SendKeyEvent(
            _terminal,
            virtualKey,
            checked((ushort)args.KeyStatus.ScanCode),
            args.KeyStatus.IsExtendedKey ? (ushort)0x0100 : (ushort)0,
            keyDown: true);
        args.Handled = true;
    }

    private void OnTerminalKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (_disposed || !_inputEnabled || _terminal == IntPtr.Zero || _api is null)
            return;
        _api.SendKeyEvent(
            _terminal,
            checked((ushort)args.Key),
            checked((ushort)args.KeyStatus.ScanCode),
            args.KeyStatus.IsExtendedKey ? (ushort)0x0100 : (ushort)0,
            keyDown: false);
        args.Handled = true;
    }

    private void OnTerminalCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_disposed || !_inputEnabled || _terminal == IntPtr.Zero || _api is null)
            return;
        if (_suppressNextCharacter)
        {
            _suppressNextCharacter = false;
            args.Handled = true;
            return;
        }

        foreach (var character in char.ConvertFromUtf32(checked((int)args.Character)))
        {
            _api.SendCharEvent(
                _terminal,
                character,
                checked((ushort)args.KeyStatus.ScanCode),
                args.KeyStatus.IsExtendedKey ? (ushort)0x0100 : (ushort)0);
        }
        args.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        RequestHostFocus();
        Focus(FocusState.Pointer);
        SendPointerEvent(args, PointerMessage(args.GetCurrentPoint(_terminalPanel).Properties.PointerUpdateKind));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args) =>
        SendPointerEvent(args, PointerMessage(args.GetCurrentPoint(_terminalPanel).Properties.PointerUpdateKind));

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args) =>
        SendPointerEvent(args, WmMouseMove);

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_terminalPanel);
        SendPointerEvent(
            args,
            point.Properties.IsHorizontalMouseWheel ? WmMouseHorizontalWheel : WmMouseWheel,
            point);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args) =>
        SendPointerEvent(args, WmMouseLeave);

    private void SendPointerEvent(
        PointerRoutedEventArgs args,
        uint message,
        PointerPoint? currentPoint = null)
    {
        if (_disposed || !_inputEnabled || _terminal == IntPtr.Zero || _api is null || message == 0)
            return;
        try
        {
            var point = currentPoint ?? args.GetCurrentPoint(_terminalPanel);
            var properties = point.Properties;
            var buttons = PointerButtons(properties);
            var scale = XamlRoot?.RasterizationScale ?? 1;
            var result = _api.SendPointerEvent(
                _terminal,
                message,
                buttons,
                checked((int)Math.Round(point.Position.X * scale)),
                checked((int)Math.Round(point.Position.Y * scale)),
                checked((short)properties.MouseWheelDelta));
            ProtectedCursor = (result & PointerHandCursor) != 0
                ? _linkCursor
                : _textCursor;
            args.Handled = (result & PointerHandled) != 0;
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native pointer input failed: {exception.Message}");
        }
    }

    private static uint PointerMessage(PointerUpdateKind updateKind) =>
        updateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => WmLeftButtonDown,
            PointerUpdateKind.LeftButtonReleased => WmLeftButtonUp,
            PointerUpdateKind.RightButtonPressed => WmRightButtonDown,
            PointerUpdateKind.RightButtonReleased => WmRightButtonUp,
            PointerUpdateKind.MiddleButtonPressed => WmMiddleButtonDown,
            PointerUpdateKind.MiddleButtonReleased => WmMiddleButtonUp,
            PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed => WmXButtonDown,
            PointerUpdateKind.XButton1Released or PointerUpdateKind.XButton2Released => WmXButtonUp,
            _ => 0,
        };

    private static uint PointerButtons(PointerPointProperties properties)
    {
        var buttons = 0u;
        if (properties.IsLeftButtonPressed)
            buttons |= MkLeftButton;
        if (properties.IsRightButtonPressed)
            buttons |= MkRightButton;
        if (properties.IsMiddleButtonPressed)
            buttons |= MkMiddleButton;
        if (properties.IsXButton1Pressed)
            buttons |= MkXButton1;
        if (properties.IsXButton2Pressed)
            buttons |= MkXButton2;
        if ((GetKeyState(0x10) & 0x8000) != 0)
            buttons |= MkShift;
        if ((GetKeyState(0x11) & 0x8000) != 0)
            buttons |= MkControl;
        return buttons;
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
        if (shift && virtualKey == 0x4F)
        {
            _suppressNextCharacter = true;
            DispatcherQueue.TryEnqueue(ToggleCommandsPanel);
            return true;
        }
        if (shift && virtualKey == 0x4D)
        {
            _suppressNextCharacter = true;
            DispatcherQueue.TryEnqueue(ToggleBookmark);
            return true;
        }
        if (shift && virtualKey is 0x26 or 0x28)
        {
            DispatcherQueue.TryEnqueue(() => _ruler.JumpCommand(previous: virtualKey == 0x26));
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


    private void ConfigureRuler()
    {
        _ruler.ScrollRequested += viewTop =>
        {
            if (_terminal == IntPtr.Zero || _api is null)
                return;
            try
            {
                _api.UserScroll(_terminal, viewTop);
            }
            catch (Exception exception)
            {
                TraceHook?.Invoke($"native ruler scroll failed: {exception.Message}");
            }
        };
        _ruler.MarkRequested += ScrollToMark;
        _ruler.CopyRequested += CopyMarkOutput;
        Children.Add(_ruler);
    }

    private void ConfigureCommandsPanel()
    {
        _commandsPanel.CloseRequested += () => SetCommandsPanelOpen(false);
        _commandsPanel.JumpRequested += ScrollToMark;
        _commandsPanel.CopyRequested += markId => { CopyMarkOutput(markId); };
        Children.Add(_commandsPanel);
    }

    private void SetCommandsPanelOpen(bool open)
    {
        if (_disposed || _commandsPanelOpen == open)
            return;
        _commandsPanelOpen = open;
        _commandsPanel.Visibility = open && !_alternateBufferActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (open)
            _commandsFingerprint = ulong.MaxValue;
        if (open)
            RefreshAnnotations();
        UpdateBounds(force: true);
        CommandsPanelOpenChanged?.Invoke(open);
        if (!open)
            FocusTerminal();
    }

    private void RefreshAnnotations()
    {
        if (_terminal == IntPtr.Zero || _api is null)
            return;
        try
        {
            _marks = _api.GetMarks(_terminal);
            var searchRows = _api.GetSearchRows(_terminal);
            _ruler.UpdateAnnotations(_marks, searchRows, MarkLabel);
            var fingerprint = CommandFingerprint(_marks);
            if (_commandsPanelOpen && fingerprint != _commandsFingerprint)
            {
                _commandsFingerprint = fingerprint;
                _commandsPanel.SetCommands(_marks, MarkLabel);
            }
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native annotation refresh failed: {exception.Message}");
        }
    }

    private void QueueAnnotationRefresh()
    {
        if (_annotationRefreshPending || _alternateBufferActive)
            return;
        _annotationRefreshPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _annotationRefreshPending = false;
            RefreshAnnotations();
        });
    }

    private void ScrollToMark(ulong markId)
    {
        if (_terminal == IntPtr.Zero || _api is null)
            return;
        try
        {
            _api.ScrollToMark(_terminal, markId);
            FocusTerminal();
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native mark jump failed: {exception.Message}");
            RefreshAnnotations();
        }
    }

    private static ulong CommandFingerprint(IReadOnlyList<NativeTerminalApi.MarkRecord> marks)
    {
        var fingerprint = 1469598103934665603UL;
        foreach (var mark in marks)
        {
            if (mark.Kind is not (NativeTerminalApi.MarkKind.ExactCommand or NativeTerminalApi.MarkKind.ApplicationCommand))
                continue;
            fingerprint = unchecked((fingerprint ^ mark.Id) * 1099511628211UL);
            fingerprint = unchecked((fingerprint ^ mark.Generation) * 1099511628211UL);
            fingerprint = unchecked((fingerprint ^ unchecked((ulong)mark.Row)) * 1099511628211UL);
            fingerprint = unchecked((fingerprint ^ unchecked((ulong)(mark.ExitCode ?? int.MinValue))) * 1099511628211UL);
        }
        return fingerprint;
    }

    private string MarkLabel(ulong markId)
    {
        if (_terminal == IntPtr.Zero || _api is null)
            return string.Empty;
        try
        {
            return _api.GetMarkText(_terminal, markId, includeOutput: false);
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native mark text failed: {exception.Message}");
            return string.Empty;
        }
    }

    private bool CopyMarkOutput(ulong markId)
    {
        if (_terminal == IntPtr.Zero || _api is null)
            return false;
        try
        {
            var text = _api.GetMarkText(_terminal, markId, includeOutput: true);
            return CopyToClipboard(text, null, null);
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native command output failed: {exception.Message}");
            return false;
        }
    }

    private void ToggleBookmark()
    {
        if (_terminal == IntPtr.Zero || _api is null || _alternateBufferActive)
            return;
        try
        {
            var existing = _marks
                .Where(mark => mark.Kind == NativeTerminalApi.MarkKind.Bookmark)
                .Select(mark => mark.Id)
                .ToHashSet();
            var color = NativeTerminalThemeCatalog.Find(_theme).ColorTable[6];
            var bookmarkId = _api.AddBookmark(_terminal, -1, color);
            if (existing.Contains(bookmarkId))
                _api.RemoveBookmark(_terminal, bookmarkId);
            RefreshAnnotations();
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native bookmark failed: {exception.Message}");
        }
    }

    private void BeginPromptDiscovery()
    {
        if (_terminal == IntPtr.Zero || _api is null || _exactShellMarksSeen || _alternateBufferActive)
            return;
        try
        {
            var probe = _api.BeginPromptProbe(_terminal);
            if (probe.Id == 0)
                return;
            var cancellation = new CancellationTokenSource();
            _promptProbes[probe.Id] = cancellation;
            _probeCommands.Clear();
            _probeExitCodes.Clear();
            _osc3008Probes.Clear();
            _lastPromptProbeId = probe.Id;
            var epoch = _titleEpoch;
            var immediate = ParseCommand(probe.Text);
            if (immediate is not null)
            {
                ReportPromptContext(probe.Text);
                _probeCommands[probe.Id] = immediate;
                CommandObserved?.Invoke(immediate);
                CommandChanged?.Invoke(immediate);
            }
            _ = SettlePromptProbeAsync(probe.Id, epoch, cancellation.Token);
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native prompt probe failed: {exception.Message}");
        }
    }

    private async Task SettlePromptProbeAsync(ulong probeId, long epoch, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await Task.Delay(attempt == 0 ? 300 : 900, cancellationToken);
                if (_disposed || _terminal == IntPtr.Zero || _api is null || _exactShellMarksSeen)
                    return;
                if (epoch != _titleEpoch)
                    break;
                var line = _api.GetMarkText(_terminal, probeId, includeOutput: false);
                var command = ParseCommand(line);
                if (command is null)
                    continue;
                ReportPromptContext(line);
                if (!_probeCommands.ContainsKey(probeId))
                {
                    _probeCommands[probeId] = command;
                    CommandObserved?.Invoke(command);
                    CommandChanged?.Invoke(command);
                }
                _probeCommands[probeId] = command;
                _probeExitCodes.TryGetValue(probeId, out var exitCode);
                _api.CreateApplicationMark(_terminal, probeId, command, exitCode);
                if (_promptProbes.Remove(probeId, out var completedProbe))
                    completedProbe.Dispose();
                RefreshAnnotations();
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                TraceHook?.Invoke($"native prompt discovery failed: {exception.Message}");
                break;
            }
        }

        try
        {
            if (_terminal != IntPtr.Zero && _api is not null)
                _api.DiscardPromptProbe(_terminal, probeId);
        }
        catch (Exception exception)
        {
            TraceHook?.Invoke($"native prompt probe cleanup failed: {exception.Message}");
        }
        finally
        {
            if (_promptProbes.Remove(probeId, out var abandonedProbe))
                abandonedProbe.Dispose();
            if (_lastPromptProbeId == probeId)
                _lastPromptProbeId = 0;
        }
    }

    private static string? ParseCommand(string line)
    {
        var match = PromptCommandPattern.Match(line, 0, Math.Min(line.Length, PromptParseWindow));
        if (!match.Success)
            return null;
        var command = match.Groups["command"].Value.TrimEnd();
        return command.Length > MaximumCommandLength ? command[..MaximumCommandLength] : command;
    }

    private void ReportPromptContext(string line)
    {
        var parsed = ParsePromptContextLine(line);
        if (parsed is not { } context
            || (string.Equals(context.Context, _promptContext, StringComparison.Ordinal)
                && string.Equals(context.Platform, _promptContextPlatform, StringComparison.Ordinal)))
        {
            return;
        }
        _promptContext = context.Context;
        _promptContextPlatform = context.Platform;
        PromptContextChanged?.Invoke(context.Context, context.Platform);
    }

    private (string Context, string? Platform)? ParsePromptContextLine(string line)
    {
        var matchLength = Math.Min(line.Length, PromptParseWindow);
        var commandMatch = PromptCommandPattern.Match(line, 0, matchLength);
        var prompt = commandMatch.Success ? commandMatch.Groups["prompt"].Value : line[..matchLength].Trim();
        return ParsePromptContext(prompt);
    }

    private (string Context, string? Platform)? ParsePromptContext(string prompt)
    {
        var match = WindowsPromptContextPattern.Match(prompt);
        if (!match.Success)
            match = UnixBracketedPromptContextPattern.Match(prompt);
        if (!match.Success)
            match = UnixSpacedPromptContextPattern.Match(prompt);
        if (match.Success)
            return (match.Groups["context"].Value.Trim(), null);
        if (_promptPlatform == "cisco")
        {
            var vendorMatch = CiscoPromptContextPattern.Match(prompt);
            if (vendorMatch.Success)
            {
                var mode = vendorMatch.Groups["mode"].Value;
                var context = string.IsNullOrEmpty(mode)
                    ? vendorMatch.Groups["terminator"].Value == ">" ? "user EXEC" : "privileged EXEC"
                    : mode == "config" ? "configure" : mode.Replace('-', ' ');
                return (context, "cisco");
            }
        }
        if (_promptPlatform == "juniper" && JuniperPromptContextPattern.IsMatch(prompt))
            return ("operational", "juniper");
        if (_promptPlatform == "nokia" && NokiaPromptContextPattern.IsMatch(prompt))
            return ("MD-CLI", "nokia");
        return null;
    }

    private void CancelPromptProbes()
    {
        foreach (var cancellation in _promptProbes.Values)
            cancellation.Cancel();
        if (_terminal != IntPtr.Zero && _api is not null)
        {
            foreach (var probeId in _promptProbes.Keys)
            {
                try { _api.DiscardPromptProbe(_terminal, probeId); }
                catch { }
            }
        }
        foreach (var cancellation in _promptProbes.Values)
            cancellation.Dispose();
        _promptProbes.Clear();
        _probeCommands.Clear();
        _probeExitCodes.Clear();
        _osc3008Probes.Clear();
        _lastPromptProbeId = 0;
    }

    private void ObserveOsc3008(string payload)
    {
        var fields = payload.Split(';');
        if (fields.Length == 0)
            return;
        var separator = fields[0].IndexOf('=');
        if (separator <= 0)
            return;
        var action = fields[0][..separator];
        var id = UnescapeOsc3008(fields[0][(separator + 1)..]);
        if (id is null)
            return;

        if (action == "start")
        {
            var probeId = _lastPromptProbeId;
            if (probeId != 0 && (_promptProbes.ContainsKey(probeId) || _probeCommands.ContainsKey(probeId)))
                _osc3008Probes[id] = probeId;
            return;
        }
        if (action != "end" || !_osc3008Probes.Remove(id, out var associatedProbe))
            return;

        int? exitCode = null;
        foreach (var field in fields.Skip(1))
        {
            var split = field.IndexOf('=');
            if (split <= 0)
                continue;
            var key = field[..split];
            var value = field[(split + 1)..];
            if (key == "status" && int.TryParse(value, out var status))
                exitCode = status;
            else if (key == "exit" && value == "success")
                exitCode = 0;
        }
        _probeExitCodes[associatedProbe] = exitCode;
        if (_terminal != IntPtr.Zero
            && _api is not null
            && _probeCommands.TryGetValue(associatedProbe, out var command)
            && !_promptProbes.ContainsKey(associatedProbe))
        {
            try
            {
                _api.CreateApplicationMark(_terminal, associatedProbe, command, exitCode);
                RefreshAnnotations();
                _probeCommands.Remove(associatedProbe);
                _probeExitCodes.Remove(associatedProbe);
                if (_lastPromptProbeId == associatedProbe)
                    _lastPromptProbeId = 0;
            }
            catch (Exception exception)
            {
                TraceHook?.Invoke($"native OSC 3008 association failed: {exception.Message}");
            }
        }
    }

    private static string? UnescapeOsc3008(string value)
    {
        if (value.Length is 0 or > 256)
            return null;
        var result = value.Replace("\\x3b", ";", StringComparison.Ordinal)
            .Replace("\\x5c", "\\", StringComparison.Ordinal);
        return result.IndexOf('\\') >= 0 || result.Any(character => character is < ' ' or > '~')
            ? null
            : result;
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
        RefreshAnnotations();
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
            RefreshAnnotations();
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
        RefreshAnnotations();
    }

    private void ApplyNativeTheme()
    {
        if (_terminal == IntPtr.Zero || _api is null)
            return;
        var theme = NativeTerminalThemeCatalog.Find(_theme);
        _api.SetTheme(
            _terminal,
            theme,
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
        _initialized = false;
    }

    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelPromptProbes();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        LayoutUpdated -= OnLayoutUpdated;
        UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
        UnsubscribeFromXamlRoot();
        DestroyNativeTerminal();
        KeyDown -= OnTerminalKeyDown;
        KeyUp -= OnTerminalKeyUp;
        CharacterReceived -= OnTerminalCharacterReceived;
        GotFocus -= OnTerminalGotFocus;
        LostFocus -= OnTerminalLostFocus;
        _terminalPanel.PointerPressed -= OnPointerPressed;
        _terminalPanel.PointerReleased -= OnPointerReleased;
        _terminalPanel.PointerMoved -= OnPointerMoved;
        _terminalPanel.PointerWheelChanged -= OnPointerWheelChanged;
        _terminalPanel.PointerExited -= OnPointerExited;
        lock (_outputGate)
        {
            _pendingOutput.SetLength(0);
            _outputDispatchPending = false;
        }
        _pendingOutput.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
