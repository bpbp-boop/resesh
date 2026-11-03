namespace Resesh.Core.Sftp;

/// <summary>
/// One tab's evidence of the shell's current folder, and the single order in which every
/// reader (the tab subtitle, command history, the file pane and File Explorer) uses it.
/// <list type="number">
/// <item>The shell's own report: OSC 7, OSC 9;9 or OSC 3008, whichever came last, from
/// the connection's own host (<see cref="Osc7WorkingDirectoryTracker"/> does the host
/// check). A report goes stale when a later prompt shows a different place from the first
/// prompt drawn after it: the shell moved without saying so (cmd started from PowerShell,
/// <c>Set-Location HKLM:</c>, an ssh to a host without shell integration).</item>
/// <item>A live query the caller can make of the backend (a persistent session's pane
/// folder, the remote /proc probe). Not kept here; callers slot it in at this point.</item>
/// <item>The folder in the newest recognised prompt (not a network CLI's mode).</item>
/// <item>The local process's own folder, only when there is no evidence at all: cmd keeps
/// it current, PowerShell does not.</item>
/// </list>
/// While another host is reporting, nothing on the screen describes this connection's host,
/// so neither the report nor the prompt is used.
/// </summary>
public sealed class WorkingDirectoryTracker
{
    private readonly Osc7WorkingDirectoryTracker _hosts = new();
    private string? _report;
    private string? _promptAtReport;
    private bool _reportStale;

    /// <summary>The newest report from the shell that no later prompt contradicts; null
    /// when there is none, it went stale, or another host is reporting.</summary>
    public string? ReportedPath => _reportStale ? null : _report;

    /// <summary>The folder the newest recognised prompt shows, or null (none yet, or a
    /// network CLI prompt, whose label is a mode rather than a folder).</summary>
    public string? PromptPath { get; private set; }

    /// <summary>The last report came from a host other than this connection's.</summary>
    public bool HostMismatch => _hosts.HostMismatch;

    /// <summary>True once the shell has said anything about where it is.</summary>
    public bool HasEvidence => HostMismatch || _report is not null || PromptPath is not null;

    /// <summary>Names the connection's own host goes by; see
    /// <see cref="Osc7WorkingDirectoryTracker.SetKnownHosts"/>.</summary>
    public void SetKnownHosts(IEnumerable<string?> hosts) => _hosts.SetKnownHosts(hosts);

    /// <summary>A new connection: nothing seen on the old one describes the new shell.</summary>
    public void Reset()
    {
        _hosts.Reset();
        _report = null;
        _promptAtReport = null;
        _reportStale = false;
        PromptPath = null;
    }

    /// <summary>A report from the shell (OSC 7, OSC 9;9 or OSC 3008). Returns true when
    /// <see cref="ReportedPath"/> changed.</summary>
    public bool ObserveReport(Osc7WorkingDirectory report)
    {
        var before = ReportedPath;
        _hosts.Observe(report);
        _report = _hosts.Path;
        _promptAtReport = null;
        _reportStale = false;
        return before != ReportedPath;
    }

    /// <summary>A recognised idle prompt. <paramref name="platform"/> is the network CLI the
    /// prompt belongs to, or null for a shell prompt showing a folder. Returns true when
    /// <see cref="ReportedPath"/> changed (a prompt can only make the report stale).</summary>
    public bool ObservePrompt(string? label, string? platform)
    {
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        platform = string.IsNullOrWhiteSpace(platform) ? null : platform;
        PromptPath = platform is null ? label : null;
        if (label is null || _report is null || _reportStale)
            return false;
        // The first prompt after a report spells the same place its own way ("~/src" for
        // "/home/me/src"); only a different prompt after that means the shell moved.
        var key = (platform ?? "") + "\n" + label;
        if (_promptAtReport is null)
        {
            _promptAtReport = key;
            return false;
        }
        if (_promptAtReport == key)
            return false;
        _reportStale = true;
        return true;
    }

    /// <summary>The policy's steps 1 and 3, for callers that cannot query the backend
    /// (command history, which must read the folder synchronously). Null under a host
    /// mismatch.</summary>
    public string? Known(bool includePrompt = true) =>
        HostMismatch ? null : ReportedPath ?? (includePrompt ? PromptPath : null);
}
