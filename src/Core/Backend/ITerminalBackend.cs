namespace Resesh.Core.Backend;

public delegate void TerminalOutputHandler(ReadOnlySpan<byte> data);

/// <summary>
/// The input/output/resize/teardown surface a terminal tab needs from whatever hosts
/// the shell — an SSH shell stream or a local ConPTY process. Connection setup stays
/// kind-specific (credentials and host keys have no local counterpart); this contract
/// covers everything after the shell is live. All members are safe from any thread;
/// events fire on background threads.
/// </summary>
public interface ITerminalBackend : IDisposable
{
    /// <summary>Raw bytes from the shell (already UTF-8/VT — fed to the terminal unmodified).</summary>
    event TerminalOutputHandler? OutputReceived;

    void Write(byte[] data);

    /// <summary>Tells the shell its new size. The pixel size is the text area's (0 when
    /// unknown); transports without one ignore it. Returns false when the size did not
    /// reach the shell, so the caller tries again with the next resize instead of
    /// believing it was applied.</summary>
    bool Resize(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0);

    /// <summary>Ends the shell without raising the backend's closed/exited event:
    /// a user-initiated stop is reported by the caller, not the backend.</summary>
    void Stop();
}

/// <summary>A backend whose transport has a break signal (telnet BREAK; later a serial
/// line break). Kept off <see cref="ITerminalBackend"/> because SSH and ConPTY have none.</summary>
public interface IBreakSender
{
    void SendBreak();
}
