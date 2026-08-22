using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Resesh.Terminal;

public delegate void TerminalOutputObservedHandler(ReadOnlySpan<byte> data, long unixTimeMilliseconds);
public sealed record TerminalReplayEvent(string Type, string Data);
public sealed record TerminalTimedReplayEvent(double Time, string Type, string Data);


/// <summary>
/// WebView2 hosting the bundled xterm.js page and marshalling bytes both ways.
/// SSH→UI writes are batched (flushed every ~16 ms or at 32 KB, whichever first)
/// so large outputs don't drown the message channel.
/// </summary>
public sealed class TerminalControl : Grid, IDisposable
{
    private const string VirtualHost = "app.local";
    private const int FlushThresholdBytes = 32 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly WebView2 _webView = new();
    private object? _initialOptions;
    private readonly object _outputGate = new();
    private readonly MemoryStream _pendingOutput = new(FlushThresholdBytes);
    private List<OutputIngest> _pendingIngest = [];
    private DispatcherQueueTimer? _flushTimer;
    private bool _flushDispatchPending;
    private bool _flushTimerPending;
    private bool _pageReady;
    private bool _disposed;
    private bool _rulerIsSplit;
    private bool _rulerIsGroupFocused = true;
    private string? _promptPlatform;

    /// <summary>Debug diagnostics sink (same pattern as SshTerminalSession.TraceHook).</summary>
    public static Action<string>? TraceHook { get; set; }

    public event Action<byte[]>? InputReceived;
    public event Action<int, int>? Resized;
    /// <summary>Raw host bytes with their pre-batch arrival time, for recording and rewind.</summary>
    public event TerminalOutputObservedHandler? OutputObserved;

    /// <summary>Full serialized xterm state captured after output parsing.</summary>
    public event Action<string, int, int, long>? KeyframeCaptured;
    public event Action? ReconnectRequested;

    /// <summary>Ctrl+F4 pressed inside the terminal page.</summary>
    public event Action? CloseTabRequested;

    /// <summary>Ctrl+Shift+\ pressed inside the terminal page.</summary>
    public event Action? SplitRequested;

    /// <summary>Ctrl+Shift+E pressed inside the terminal page (toggle file pane).</summary>
    public event Action? FilePaneRequested;

    /// <summary>Ctrl+Shift+T pressed inside the terminal page (open default local profile).</summary>
    public event Action? NewLocalTabRequested;

    /// <summary>Ctrl+Shift+P pressed inside the terminal page.</summary>
    public event Action? CommandPaletteRequested;

    /// <summary>Fires once when the xterm page is loaded and measured (initial cols/rows).</summary>
    public event Action<int, int>? Ready;

    /// <summary>OSC 0/2 window title set by the remote shell or a full-screen program.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>Command the page saw start (ruler discovery / OSC 133); "" = it ended.
    /// Drives the tab's subtitle; the page epoch-gates it against the title stream.</summary>
    public event Action<string>? CommandChanged;

    /// <summary>Current location read from a known idle prompt, plus an optional detected
    /// platform key. Examples: a Windows directory or a Nokia MD-CLI cli-path.</summary>
    public event Action<string, string?>? PromptContextChanged;

    /// <summary>Raw OSC 7 payload. The app validates it before it can select an SFTP path.</summary>
    public event Action<string>? WorkingDirectoryReported;

    /// <summary>Raw OSC 3008 payload. The app validates this auxiliary context signal.</summary>
    public event Action<string>? ContextReported;

    // ---- agent-awareness evidence (Phase 6.2); raw, unmapped, from this tab's page only ----

    /// <summary>An OSC sequence we watch for agent events: the code and its payload.</summary>
    public event Action<int, string>? AgentOscReceived;

    /// <summary>The terminal rang the bell.</summary>
    public event Action? BellReceived;

    /// <summary>A command was marked at a shell prompt (OSC 133 or Enter-gated discovery).
    /// Every mark, never an end and never epoch-gated — unlike <see cref="CommandChanged"/>,
    /// which the subtitle needs. <see cref="TitleChanged"/> feeds agent tracking as well.</summary>
    public event Action<string>? CommandObserved;

    /// <summary>Raised when the page's commands panel opens or closes — from the host's
    /// toggle button, Ctrl+Shift+O, or the panel's own close button — so the native
    /// toggle button can mirror the true state.</summary>
    public event Action<bool>? CommandsPanelOpenChanged;

    public int Columns { get; private set; } = 80;
    public int Rows { get; private set; } = 24;

    public TerminalControl()
    {
        // WebView2 paints white until terminal.html renders; match its dark
        // background so opening a tab doesn't flash.
        _webView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C);
        Children.Add(_webView);
    }

    public async Task InitializeAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Resesh", "WebView2");
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            null, userDataFolder, new CoreWebView2EnvironmentOptions());
        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true; // Ctrl+Shift+I in debug builds
#else
        core.Settings.AreDevToolsEnabled = false;
#endif

        // WinUI library content lands under "<ProjectName>\wwwroot" in the app output.
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "Resesh.Terminal", "wwwroot");
        if (!Directory.Exists(wwwroot))
            wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessageReceived;
        // Chromium heuristic-caches virtual-host files (no Cache-Control headers), which can
        // serve a stale terminal.html/addon after an app update; the assets are local, so a
        // fresh read costs nothing.
        await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
        core.Navigate($"https://{VirtualHost}/terminal.html");
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(args.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp))
                return;

            switch (typeProp.GetString())
            {
                case "init":
                    // The page waits for these before constructing the terminal, so it is
                    // born with the right theme/fonts instead of restyling after the fact.
                    _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(
                        _initialOptions ?? new { type = "initOptions" }, JsonOptions));
                    break;
                case "ready":
                    Columns = root.GetProperty("cols").GetInt32();
                    Rows = root.GetProperty("rows").GetInt32();
                    _pageReady = true;
                    PostRulerPresentation();
                    PostPromptPlatform();
                    Ready?.Invoke(Columns, Rows);
                    break;
                case "input":
                    if (root.TryGetProperty("data", out var data) && data.GetString() is { } b64)
                        InputReceived?.Invoke(Convert.FromBase64String(b64));
                    break;
                case "resize":
                    Columns = root.GetProperty("cols").GetInt32();
                    Rows = root.GetProperty("rows").GetInt32();
                    Resized?.Invoke(Columns, Rows);
                    break;
                case "keyframe":
                    if (root.TryGetProperty("data", out var keyframeData) &&
                        root.TryGetProperty("cols", out var keyframeColumns) &&
                        root.TryGetProperty("rows", out var keyframeRows) &&
                        root.TryGetProperty("unixMs", out var keyframeTime))
                    {
                        try
                        {
                            var state = Encoding.UTF8.GetString(
                                Convert.FromBase64String(keyframeData.GetString() ?? ""));
                            KeyframeCaptured?.Invoke(
                                state, keyframeColumns.GetInt32(), keyframeRows.GetInt32(), keyframeTime.GetInt64());
                        }
                        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
                        {
                            TraceHook?.Invoke($"keyframe decode failed: {exception.Message}");
                        }
                    }
                    break;
                case "reconnect":
                    ReconnectRequested?.Invoke();
                    break;
                case "closeTab":
                    CloseTabRequested?.Invoke();
                    break;
                case "splitTab":
                    SplitRequested?.Invoke();
                    break;
                case "filePane":
                    FilePaneRequested?.Invoke();
                    break;
                case "newLocalTab":
                    NewLocalTabRequested?.Invoke();
                    break;
                case "commandPalette":
                    CommandPaletteRequested?.Invoke();
                    break;
                case "openLink":
                    if (root.TryGetProperty("uri", out var uriProperty) &&
                        Uri.TryCreate(uriProperty.GetString(), UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                        }
                        catch (Exception exception)
                        {
                            TraceHook?.Invoke($"openLink failed: {exception.Message}");
                        }
                    }
                    break;
                case "agentOsc":
                    if (root.TryGetProperty("code", out var oscCode))
                    {
                        AgentOscReceived?.Invoke(
                            oscCode.GetInt32(),
                            root.TryGetProperty("data", out var oscData) ? oscData.GetString() ?? "" : "");
                    }
                    break;
                case "agentBell":
                    BellReceived?.Invoke();
                    break;
                case "title":
                    if (root.TryGetProperty("text", out var title))
                    {
                        // Traced because "the tab's second line never changes" is
                        // indistinguishable from "the host never sent a title" without it.
                        var titleText = title.GetString() ?? "";
                        TraceHook?.Invoke($"title: {titleText}");
                        TitleChanged?.Invoke(titleText);
                    }
                    break;
                case "command":
                    if (root.TryGetProperty("text", out var command))
                        CommandObserved?.Invoke(command.GetString() ?? "");
                    break;
                case "runningCommand":
                    if (root.TryGetProperty("text", out var running))
                    {
                        var runningText = running.GetString() ?? "";
                        TraceHook?.Invoke($"runningCommand: {runningText}");
                        CommandChanged?.Invoke(runningText);
                    }
                    break;
                case "promptContext":
                    if (root.TryGetProperty("text", out var promptContext))
                    {
                        var platform = root.TryGetProperty("platform", out var platformElement)
                            ? platformElement.GetString()
                            : null;
                        PromptContextChanged?.Invoke(promptContext.GetString() ?? "", platform);
                    }
                    break;
                case "workingDirectory":
                    if (root.TryGetProperty("data", out var workingDirectory))
                        WorkingDirectoryReported?.Invoke(workingDirectory.GetString() ?? "");
                    break;
                case "osc3008":
                    if (root.TryGetProperty("data", out var context))
                        ContextReported?.Invoke(context.GetString() ?? "");
                    break;
                case "commandsPanel":
                    if (root.TryGetProperty("open", out var panelOpen))
                        CommandsPanelOpenChanged?.Invoke(panelOpen.ValueKind == JsonValueKind.True);
                    break;
                case "pageError":
                    if (root.TryGetProperty("message", out var err))
                        TraceHook?.Invoke($"pageError: {err.GetString()}");
                    break;
            }
        }
    }

    // ---- SSH -> UI (batched; callable from any thread) ----

    public void WriteOutput(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty)
            return;

        // Capture arrival time before the UI batch combines independent backend reads.
        // The page uses the byte offsets to restore each read's time while parsing.
        var unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            OutputObserved?.Invoke(data, unixMs);
        }
        catch (Exception exception)
        {
            // Capture must never interrupt the live terminal data path.
            TraceHook?.Invoke($"output observer failed: {exception.Message}");
        }
        bool queueDispatch;
        lock (_outputGate)
        {
            if (_pendingIngest.Count == 0 || _pendingIngest[^1].UnixMs != unixMs)
                _pendingIngest.Add(new OutputIngest((int)_pendingOutput.Length, unixMs));
            _pendingOutput.Write(data);
            var thresholdReached = _pendingOutput.Length >= FlushThresholdBytes;
            queueDispatch = !_flushDispatchPending && (!_flushTimerPending || thresholdReached);
            if (queueDispatch)
                _flushDispatchPending = true;
        }

        if (queueDispatch && !DispatcherQueue.TryEnqueue(ScheduleOutputFlush))
        {
            lock (_outputGate)
                _flushDispatchPending = false;
        }
    }

    private void ScheduleOutputFlush()
    {
        bool flushNow;
        lock (_outputGate)
        {
            _flushDispatchPending = false;
            if (_pendingOutput.Length == 0)
                return;
            flushNow = _pendingOutput.Length >= FlushThresholdBytes;
            if (!flushNow)
                _flushTimerPending = true;
        }

        if (flushNow)
        {
            FlushOutput();
            return;
        }

        _flushTimer ??= CreateFlushTimer();
        if (!_flushTimer.IsRunning)
            _flushTimer.Start();
    }

    private DispatcherQueueTimer CreateFlushTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => FlushOutput();
        return timer;
    }

    private void FlushOutput()
    {
        _flushTimer?.Stop();
        string data;
        List<OutputIngest> ingest;
        lock (_outputGate)
        {
            _flushDispatchPending = false;
            _flushTimerPending = false;
            if (_pendingOutput.Length == 0)
                return;

            data = Convert.ToBase64String(
                _pendingOutput.GetBuffer(), 0, checked((int)_pendingOutput.Length));
            _pendingOutput.SetLength(0);
            _pendingOutput.Position = 0;
            ingest = _pendingIngest;
            _pendingIngest = [];
        }
        Post(new { type = "output", data, ingest });
    }

    private readonly record struct OutputIngest(int Offset, long UnixMs);

    // ---- control messages (UI thread) ----

    public void NotifyConnected() => Post(new { type = "connected" });

    /// <summary>Shell-over notice. <paramref name="action"/> is the verb in the
    /// "Press Enter to …" hint ("reconnect"/"restart"); <paramref name="neutral"/> renders
    /// the message dimmed instead of warning-yellow (clean local exits are not errors).</summary>
    public void NotifyDisconnected(string message, string action = "reconnect", bool neutral = false)
    {
        FlushOutput();
        Post(new { type = "disconnected", message, action, severity = neutral ? "info" : "warn" });
    }

    public void WriteDivider() => Post(new { type = "divider" });

    public void WriteNotice(string message) => Post(new { type = "notice", message });

    public void FocusTerminal()
    {
        // XAML focus must land on the WebView2 first, or keystrokes go to whatever
        // control had focus (e.g. the search box); then focus xterm inside the page.
        _ = Microsoft.UI.Xaml.Input.FocusManager.TryFocusAsync(_webView, FocusState.Programmatic);
        Post(new { type = "focus" });
    }

    /// <summary>Blocks or restores pointer input to the page (used by session lock).
    /// Callers must also move keyboard focus off the terminal (the lock overlay does).</summary>
    public void SetInputEnabled(bool enabled) => _webView.IsHitTestVisible = enabled;

    /// <summary>Opens or closes the page's commands panel (the annotated scrollbar's
    /// command-mark list). Same action as Ctrl+Shift+O inside the terminal.</summary>
    public void ToggleCommandsPanel() => Post(new { type = "toggleCommands" });

    /// <summary>Uses the quieter ruler presentation while two terminal groups are visible.
    /// The inactive group is dimmer, but pointer hover restores the full presentation.</summary>
    public void SetRulerPresentation(bool isSplit, bool isGroupFocused)
    {
        if (_rulerIsSplit == isSplit && _rulerIsGroupFocused == isGroupFocused)
            return;

        _rulerIsSplit = isSplit;
        _rulerIsGroupFocused = isGroupFocused;
        if (_pageReady)
            PostRulerPresentation();
    }

    private void PostRulerPresentation() => Post(new
    {
        type = "setRulerPresentation",
        isSplit = _rulerIsSplit,
        isGroupFocused = _rulerIsGroupFocused,
    });

    /// <summary>Gives the page a vendor hint from trusted session metadata or the SSH
    /// version banner. The page still requires a matching prompt before it reports context.</summary>
    public void SetPromptPlatform(string? platform)
    {
        _promptPlatform = platform;
        if (_pageReady)
            PostPromptPlatform();
    }

    private void PostPromptPlatform() => Post(new { type = "setPromptPlatform", platform = _promptPlatform });

    /// <summary>Options the page's terminal is constructed with. Must be set before
    /// <see cref="InitializeAsync"/>; later changes go through <see cref="ApplyOptions"/>.</summary>
    public void SetInitialOptions(
        int fontSize, string fontFamily, string theme,
        bool copyOnSelect, bool rightClickPaste, int scrollback,
        IReadOnlyList<object>? highlights = null,
        bool readOnly = false)
    {
        _webView.DefaultBackgroundColor = ThemeBackground(theme);
        _initialOptions = new
        {
            type = "initOptions", fontSize, fontFamily, theme, copyOnSelect, rightClickPaste, scrollback, highlights,
            readOnly,
        };
    }

    public void ApplyOptions(
        int? fontSize = null, string? fontFamily = null, string? theme = null,
        bool? copyOnSelect = null, bool? rightClickPaste = null, int? scrollback = null)
    {
        if (theme is not null)
            _webView.DefaultBackgroundColor = ThemeBackground(theme);
        TraceHook?.Invoke($"ApplyOptions theme={theme} fontSize={fontSize} pageReady={_pageReady}");
        Post(new { type = "setOptions", fontSize, fontFamily, theme, copyOnSelect, rightClickPaste, scrollback });
    }

    private static Windows.UI.Color ThemeBackground(string theme) => theme.ToLowerInvariant() switch
    {
        "light" => Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF),
        "solarized-dark" => Windows.UI.Color.FromArgb(255, 0x00, 0x2B, 0x36),
        "solarized-light" => Windows.UI.Color.FromArgb(255, 0xFD, 0xF6, 0xE3),
        "dracula" => Windows.UI.Color.FromArgb(255, 0x28, 0x2A, 0x36),
        "one-dark" => Windows.UI.Color.FromArgb(255, 0x28, 0x2C, 0x34),
        "nord" => Windows.UI.Color.FromArgb(255, 0x2E, 0x34, 0x40),
        "gruvbox-dark" => Windows.UI.Color.FromArgb(255, 0x28, 0x28, 0x28),
        "monokai" => Windows.UI.Color.FromArgb(255, 0x27, 0x28, 0x22),
        "tokyo-night" => Windows.UI.Color.FromArgb(255, 0x1A, 0x1B, 0x26),
        "catppuccin-mocha" => Windows.UI.Color.FromArgb(255, 0x1E, 0x1E, 0x2E),
        "phthalo-green" => Windows.UI.Color.FromArgb(255, 0x12, 0x35, 0x24),
        _ => Windows.UI.Color.FromArgb(255, 0x0C, 0x0C, 0x0C),
    };

    /// <summary>Replaces the page's active highlight rule set (enabled rules only,
    /// already resolved for the session). The page recompiles and rescans the viewport.</summary>
    public void ApplyHighlights(IReadOnlyList<object> rules) =>
        Post(new { type = "setHighlights", rules });

    /// <summary>Asks the live terminal buffer to identify its current idle prompt.
    /// This catches cmd and PowerShell locations even when no later output triggered a scan.</summary>
    public async Task<(string Context, string? Platform)?> RequestPromptContextAsync()
    {
        if (_disposed || !_pageReady || _webView.CoreWebView2 is null)
            return null;

        var completion = new TaskCompletionSource<(string, string?)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Reported(string context, string? platform) => completion.TrySetResult((context, platform));
        PromptContextChanged += Reported;
        try
        {
            Post(new { type = "requestPromptContext" });
            var completed = await Task.WhenAny(completion.Task, Task.Delay(500));
            if (completed != completion.Task)
                return null;
            var (context, platform) = await completion.Task;
            return (context, platform);
        }
        finally
        {
            PromptContextChanged -= Reported;
        }
    }

    /// <summary>Atomically resets a read-only terminal and replays one state slice.</summary>
    public void ShowReplay(
        int columns,
        int rows,
        string? keyframe,
        IReadOnlyList<TerminalReplayEvent> events)
    {
        static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        Post(new
        {
            type = "showReplay",
            columns,
            rows,
            keyframe = keyframe is null ? null : Encode(keyframe),
            events = events.Select(item => new
            {
                type = item.Type,
                data = item.Type == "o" ? Encode(item.Data) : item.Data,
            }).ToArray(),
        });
    }

    /// <summary>Loads a complete asciicast stream; the page builds seek keyframes once.</summary>
    public void LoadPlayback(
        int columns,
        int rows,
        IReadOnlyList<TerminalTimedReplayEvent> events)
    {
        Post(new
        {
            type = "loadPlayback",
            columns,
            rows,
            events = events.Select(item => new
            {
                time = item.Time,
                type = item.Type,
                data = item.Type == "o"
                    ? Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Data))
                    : item.Data,
            }).ToArray(),
        });
    }

    public void SeekPlayback(double time) => Post(new { type = "seekPlayback", time });

    private void Post(object message)
    {
        if (_disposed || !_pageReady || _webView.CoreWebView2 is null)
        {
            TraceHook?.Invoke(
                $"Post DROPPED: disposed={_disposed} pageReady={_pageReady} core={(_webView.CoreWebView2 is null ? "null" : "ok")}");
            return;
        }
        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _flushTimer?.Stop();
        _webView.Close();
    }
}
