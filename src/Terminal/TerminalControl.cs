using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Sessions.Terminal;

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
    private MemoryStream _pendingOutput = new();
    private DispatcherQueueTimer? _flushTimer;
    private bool _pageReady;
    private bool _disposed;
    private bool _rulerIsSplit;
    private bool _rulerIsGroupFocused = true;

    /// <summary>Debug diagnostics sink (same pattern as SshTerminalSession.TraceHook).</summary>
    public static Action<string>? TraceHook { get; set; }

    public event Action<byte[]>? InputReceived;
    public event Action<int, int>? Resized;
    public event Action? ReconnectRequested;

    /// <summary>Ctrl+F4 pressed inside the terminal page.</summary>
    public event Action? CloseTabRequested;

    /// <summary>Ctrl+Shift+\ pressed inside the terminal page.</summary>
    public event Action? SplitRequested;

    /// <summary>Ctrl+Shift+E pressed inside the terminal page (toggle file pane).</summary>
    public event Action? FilePaneRequested;

    /// <summary>Fires once when the xterm page is loaded and measured (initial cols/rows).</summary>
    public event Action<int, int>? Ready;

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
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sessions", "WebView2");
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
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "Sessions.Terminal", "wwwroot");
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
                case "pageError":
                    if (root.TryGetProperty("message", out var err))
                        TraceHook?.Invoke($"pageError: {err.GetString()}");
                    break;
            }
        }
    }

    // ---- SSH -> UI (batched; callable from any thread) ----

    public void WriteOutput(byte[] data)
    {
        if (_disposed)
            return;

        bool flushNow;
        lock (_outputGate)
        {
            _pendingOutput.Write(data);
            flushNow = _pendingOutput.Length >= FlushThresholdBytes;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (flushNow)
            {
                FlushOutput();
            }
            else
            {
                _flushTimer ??= CreateFlushTimer();
                if (!_flushTimer.IsRunning)
                    _flushTimer.Start();
            }
        });
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
        byte[] chunk;
        lock (_outputGate)
        {
            if (_pendingOutput.Length == 0)
                return;
            chunk = _pendingOutput.ToArray();
            _pendingOutput = new MemoryStream();
        }
        Post(new { type = "output", data = Convert.ToBase64String(chunk) });
    }

    // ---- control messages (UI thread) ----

    public void NotifyConnected() => Post(new { type = "connected" });

    public void NotifyDisconnected(string message)
    {
        FlushOutput();
        Post(new { type = "disconnected", message });
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

    /// <summary>Options the page's terminal is constructed with. Must be set before
    /// <see cref="InitializeAsync"/>; later changes go through <see cref="ApplyOptions"/>.</summary>
    public void SetInitialOptions(
        int fontSize, string fontFamily, string theme,
        bool copyOnSelect, bool rightClickPaste, int scrollback,
        IReadOnlyList<object>? highlights = null)
        => _initialOptions = new
        {
            type = "initOptions", fontSize, fontFamily, theme, copyOnSelect, rightClickPaste, scrollback, highlights,
        };

    public void ApplyOptions(
        int? fontSize = null, string? fontFamily = null, string? theme = null,
        bool? copyOnSelect = null, bool? rightClickPaste = null, int? scrollback = null)
    {
        TraceHook?.Invoke($"ApplyOptions theme={theme} fontSize={fontSize} pageReady={_pageReady}");
        Post(new { type = "setOptions", fontSize, fontFamily, theme, copyOnSelect, rightClickPaste, scrollback });
    }

    /// <summary>Replaces the page's active highlight rule set (enabled rules only,
    /// already resolved for the session). The page recompiles and rescans the viewport.</summary>
    public void ApplyHighlights(IReadOnlyList<object> rules) =>
        Post(new { type = "setHighlights", rules });

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
