namespace Resesh.Core.Sftp;

/// <summary>A validated OSC 7 file URI reported by a shell.</summary>
public sealed record Osc7WorkingDirectory(string Host, string Path);

/// <summary>Parses OSC 7 without giving URI query or fragment characters special meaning.
/// Shells commonly write the current path as-is, so '?' and '#' can be file-name characters.</summary>
public static class Osc7WorkingDirectoryParser
{
    public const int MaxPayloadLength = 2048;

    public static bool TryParse(string? payload, out Osc7WorkingDirectory? workingDirectory)
    {
        workingDirectory = null;
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength ||
            !payload.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return false;

        var authorityAndPath = payload[7..];
        var pathStart = authorityAndPath.IndexOf('/');
        if (pathStart < 0)
            return false;

        var host = authorityAndPath[..pathStart];
        var encodedPath = authorityAndPath[pathStart..];
        if (!ValidHost(host) || !ValidPercentEncoding(encodedPath))
            return false;

        string path;
        try
        {
            path = Uri.UnescapeDataString(encodedPath);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (RemotePath.FromUntrusted(path) is not { } directory)
            return false;

        workingDirectory = new Osc7WorkingDirectory(host, directory);
        return true;
    }

    private static bool ValidHost(string host) =>
        !host.Any(character => char.IsControl(character) || character is '@' or '\\' or '%');

    private static bool ValidPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
                continue;
            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
                return false;
            index += 2;
        }
        return true;
    }

}

/// <summary>Keeps one tab's last trusted report. A host change clears the path until the
/// trusted host reports again. This prevents a nested SSH shell from steering the pane.
/// <para>
/// The trusted host is the first one to report, unless the tab says which names the
/// connection's own host goes by (<see cref="SetKnownHosts"/>): then a report from one of
/// those names takes over from a first report that was not, since that first report may
/// have come from a nested shell (a reconnect straight back into ssh-in-tmux, say). Known
/// names are a hint, not a requirement: a host often calls itself something other than the
/// address dialled (an IP, an alias), so an unknown name is still trusted when it is first.
/// </para>
/// </summary>
public sealed class Osc7WorkingDirectoryTracker
{
    private string? _host;
    private bool _hostKnown;
    private string[] _knownHosts = [];

    public string? Path { get; private set; }
    public bool HostMismatch { get; private set; }

    /// <summary>Names the connection's own host goes by (the session's host, aliases).
    /// Kept across <see cref="Reset"/>, which ends a connection, not the session.</summary>
    public void SetKnownHosts(IEnumerable<string?> hosts) =>
        _knownHosts = [.. hosts.Where(host => !string.IsNullOrWhiteSpace(host)).Select(host => NormalizeHost(host!.Trim()))];

    public void Reset()
    {
        _host = null;
        _hostKnown = false;
        Path = null;
        HostMismatch = false;
    }

    public void Observe(Osc7WorkingDirectory report)
    {
        var reportedHost = NormalizeHost(report.Host);
        if (HostMismatch && reportedHost.Length == 0)
            return;
        if (reportedHost.Length > 0)
        {
            if (_host is null || !_hostKnown && IsKnown(reportedHost))
            {
                _host = reportedHost;
                _hostKnown = IsKnown(reportedHost);
            }
            else if (!string.Equals(reportedHost, _host, StringComparison.OrdinalIgnoreCase)
                && !(_hostKnown && IsKnown(reportedHost)))
            {
                Path = null;
                HostMismatch = true;
                return;
            }
        }

        Path = report.Path;
        HostMismatch = false;
    }

    /// <summary>A known name, or the same machine named with or without its domain
    /// ("web01" and "web01.example.com"), which is how hostnames and SSH targets differ.</summary>
    private bool IsKnown(string host)
    {
        foreach (var known in _knownHosts)
        {
            if (string.Equals(host, known, StringComparison.OrdinalIgnoreCase))
                return true;
            if (System.Net.IPAddress.TryParse(host, out _) || System.Net.IPAddress.TryParse(known, out _))
                continue;
            if (host.Contains('.') != known.Contains('.') &&
                string.Equals(FirstLabel(host), FirstLabel(known), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string FirstLabel(string host) => host.Split('.', 2)[0];

    private static string NormalizeHost(string host) => host.TrimEnd('.');
}
