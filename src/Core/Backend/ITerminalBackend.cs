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
    /// <summary>Raw bytes from the shell (already UTF-8/VT — fed to xterm.js unmodified).</summary>
    event TerminalOutputHandler? OutputReceived;

    void Write(byte[] data);

    void Resize(int columns, int rows);

    /// <summary>Ends the shell without raising the backend's closed/exited event:
    /// a user-initiated stop is reported by the caller, not the backend.</summary>
    void Stop();
}
