using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;

namespace Resesh.Terminal;

/// <summary>
/// Live terminal surface contract. Backends remain responsible for transport and emit raw bytes;
/// the selected surface owns parsing, rendering, input, and viewport sizing.
/// </summary>
public abstract class TerminalSurface : Grid, IDisposable
{
    public abstract event Action<byte[]>? InputReceived;
    public abstract event Action<int, int>? Resized;
    public abstract event TerminalOutputObservedHandler? OutputObserved;
    public abstract event Action<ReadOnlyMemory<byte>, int, int, long>? KeyframeCaptured;
    public abstract event Action? ReconnectRequested;
    public abstract event Action? CloseTabRequested;
    public abstract event Action? SplitRequested;
    public abstract event Action? FilePaneRequested;
    public abstract event Action? NewLocalTabRequested;
    public abstract event Action? CommandPaletteRequested;
    public abstract event Action? QuickConnectRequested;
    public abstract event Action<int, int>? Ready;
    public abstract event Action<string>? TitleChanged;
    public abstract event Action<string>? CommandChanged;
    public abstract event Action<string, string?>? PromptContextChanged;
    public abstract event Action<string>? WorkingDirectoryReported;
    public abstract event Action<string>? ContextReported;
    public abstract event Action<int, string>? AgentOscReceived;
    public abstract event Action? BellReceived;
    public abstract event Action<string>? CommandObserved;
    public abstract event Action<bool>? CommandsPanelOpenChanged;

    /// <summary>Requests host-level pane focus when a native child window receives pointer input.</summary>
    public event Action? HostFocusRequested;

    protected void RequestHostFocus() => HostFocusRequested?.Invoke();

    public abstract bool SupportsRewindCapture { get; }

    public abstract int Columns { get; protected set; }
    public abstract int Rows { get; protected set; }

    public abstract Task InitializeAsync();
    public abstract void WriteOutput(ReadOnlySpan<byte> data);
    public abstract void NotifyConnected();
    public abstract void NotifyDisconnected(string message, string action = "reconnect", bool neutral = false);
    public abstract void WriteDivider();
    public abstract void WriteNotice(string message);
    public abstract void FocusTerminal();
    public abstract void SetInputEnabled(bool enabled);
    public abstract void ToggleCommandsPanel();
    public abstract void SetRulerPresentation(bool isSplit, bool isGroupFocused);
    public abstract void SetPromptPlatform(string? platform);
    public abstract void SetInitialOptions(
        int fontSize,
        string fontFamily,
        string theme,
        bool copyOnSelect,
        bool rightClickPaste,
        int scrollback,
        IReadOnlyList<object>? highlights = null,
        bool readOnly = false);
    public abstract void ApplyOptions(
        int? fontSize = null,
        string? fontFamily = null,
        string? theme = null,
        bool? copyOnSelect = null,
        bool? rightClickPaste = null,
        int? scrollback = null);
    public abstract void ApplyHighlights(IReadOnlyList<object> rules);
    public abstract Task<(string Context, string? Platform)?> RequestPromptContextAsync();
    public abstract void Dispose();
}

internal static class TerminalLinkPolicy
{
    internal static void Open(string? value, Action<string>? trace = null)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            trace?.Invoke($"openLink failed: {exception.Message}");
        }
    }
}

public static class TerminalSurfaceFactory
{
    public const string SurfaceEnvironmentVariable = "RESESH_TERMINAL_SURFACE";

    /// <summary>Creates the selected live terminal surface.</summary>
    public static TerminalSurface CreateLive() =>
        string.Equals(
            Environment.GetEnvironmentVariable(SurfaceEnvironmentVariable),
            "native",
            StringComparison.OrdinalIgnoreCase)
            ? new NativeTerminalSurface()
            : new TerminalControl();

    /// <summary>Playback always uses the exact-snapshot native terminal.</summary>
    public static TerminalSurface CreatePlayback() => new NativeTerminalSurface();
}
