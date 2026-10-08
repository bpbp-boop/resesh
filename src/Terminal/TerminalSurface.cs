using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;

namespace Resesh.Terminal;

/// <summary>
/// Live terminal surface contract. Backends remain responsible for transport and emit raw bytes;
/// the selected surface owns parsing, rendering, input, and viewport sizing.
/// </summary>
public abstract class TerminalSurface : Grid, IDisposable
{
    /// <summary>Debug diagnostics sink (same pattern as SshTerminalSession.TraceHook).</summary>
    public static Action<string>? TraceHook { get; set; }

    public abstract event Action<byte[]>? InputReceived;
    public abstract event Action<int, int>? Resized;
    public abstract event TerminalOutputObservedHandler? OutputObserved;
    public abstract event Action<ReadOnlyMemory<byte>, int, int, long>? KeyframeCaptured;
    public abstract event Action? ReconnectRequested;

    /// <summary>A window shortcut pressed while this terminal had focus: the binding id and
    /// the index of the chord that matched. Raised on the UI thread.</summary>
    public abstract event Action<string, int>? ShortcutRequested;
    public abstract event Action<int, int>? Ready;
    public abstract event Action<string>? TitleChanged;
    public abstract event Action<string, bool>? CommandChanged;
    public abstract event Action<TerminalCommandExecution>? CommandExecutionChanged;
    public abstract event Action<string, string?>? PromptContextChanged;
    public abstract event Action<string>? WorkingDirectoryReported;
    public abstract event Action<string>? WindowsWorkingDirectoryReported;
    public abstract event Action<string>? ContextReported;
    public abstract event Action<int, string>? AgentOscReceived;
    public abstract event Action? BellReceived;
    public abstract event Action<string>? CommandObserved;
    public abstract event Action<bool>? CommandsPanelOpenChanged;

    /// <summary>A finished command while history capture is on. Never raised by playback.</summary>
    public abstract event Action<TerminalCommandRecord>? CommandRecorded;

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

    /// <summary>Pastes text as the user's input, honoring the shell's bracketed-paste mode.</summary>
    public abstract void PasteText(string text);

    /// <summary>Turns command-history capture on or off for this live terminal.</summary>
    public abstract void SetHistoryCapture(bool enabled);

    /// <summary>Records the command still running now, with the output it has so far.</summary>
    public abstract void FlushHistory();

    /// <summary>Runs a terminal-scope shortcut (copy, zoom, clear, ...) as if its key was
    /// pressed. Returns false when this surface does not support it.</summary>
    public abstract bool InvokeShortcut(string id);

    private static IReadOnlyList<TerminalShortcut> _shortcuts = [];

    /// <summary>The app's shortcut table, set once at startup before any terminal is created.
    /// Terminals handle the <see cref="TerminalShortcut.Forward"/> = false entries themselves
    /// and raise <see cref="ShortcutRequested"/> for the rest.</summary>
    public static IReadOnlyList<TerminalShortcut> Shortcuts
    {
        get => _shortcuts;
        set => _shortcuts = value ?? [];
    }

    /// <summary>The shortcut a key press in a terminal matches, if any.</summary>
    internal static (TerminalShortcut Shortcut, int ChordIndex)? MatchShortcut(
        int virtualKey, bool control, bool shift, bool alt)
    {
        foreach (var shortcut in _shortcuts)
        {
            for (var i = 0; i < shortcut.Chords.Count; i++)
            {
                var chord = shortcut.Chords[i];
                if (chord.Key == virtualKey && chord.Ctrl == control && chord.Shift == shift && chord.Alt == alt)
                    return (shortcut, i);
            }
        }
        return null;
    }
    public abstract void ScrollToCommand(long id);
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
        bool readOnly = false,
        bool fixed80Columns = false);
    public abstract void ApplyOptions(
        int? fontSize = null,
        string? fontFamily = null,
        string? theme = null,
        bool? copyOnSelect = null,
        bool? rightClickPaste = null,
        int? scrollback = null);
    public abstract void ApplyHighlights(IReadOnlyList<object> rules);
    public abstract Task<(string Context, string? Platform)?> RequestPromptContextAsync();
    public abstract Task ShowReplayAsync(int columns, int rows, ReadOnlyMemory<byte> keyframe, IReadOnlyList<TerminalReplayEvent> events);
    public abstract Task LoadPlaybackAsync(int columns, int rows, IReadOnlyList<TerminalTimedReplayEvent> events);
    public abstract Task SeekPlaybackAsync(double time);
    public abstract void Dispose();
}

/// <summary>One key combination as the terminal surfaces match it: a Windows virtual-key code
/// (as WinUI reports it) plus exact modifier state.</summary>
public sealed record TerminalKeyChord(int Key, bool Ctrl, bool Shift, bool Alt);

/// <summary>A shortcut the terminal must recognize. <paramref name="Forward"/> shortcuts go to the
/// window; the others are terminal actions the surface runs itself. <paramref name="WhenSplit"/>
/// shortcuts only take the key while the window is split; otherwise the shell receives it.</summary>
public sealed record TerminalShortcut(
    string Id,
    bool Forward,
    bool WhenSplit,
    IReadOnlyList<TerminalKeyChord> Chords);

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

public delegate void TerminalOutputObservedHandler(ReadOnlySpan<byte> data, long unixTimeMilliseconds);
public sealed record TerminalReplayEvent(string Type, string Data);
public sealed record TerminalTimedReplayEvent(double Time, string Type, string Data);

public static class TerminalSurfaceFactory
{
    /// <summary>Live tabs, rewind and recording playback all draw in-process with
    /// libghostty-vt (<see cref="Native.NativeTerminalSurface"/>).</summary>
    public static TerminalSurface CreateLive() => Create();

    public static TerminalSurface CreatePlayback() => Create();

    private static TerminalSurface Create() =>
        Native.NativeTerminalSurface.UnavailableReason is { } reason
            ? throw new InvalidOperationException(
                $"The terminal libraries could not be loaded ({reason}). Reinstall Resesh, or for a development "
                + "build run eng/build-ghostty-vt.ps1.")
            : new Native.NativeTerminalSurface();
}
