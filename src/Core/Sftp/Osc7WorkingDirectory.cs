namespace Sessions.Core.Sftp;

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

        if (!path.StartsWith('/') || path.Any(IsControl))
            return false;

        workingDirectory = new Osc7WorkingDirectory(host, RemotePath.Normalize(path));
        return true;
    }

    private static bool ValidHost(string host) =>
        !host.Any(character => IsControl(character) || character is '@' or '\\' or '%');

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

    private static bool IsControl(char character) => character < ' ' || character == '\u007f';
}

/// <summary>Keeps one tab's last trusted report. A host change clears the path until the
/// original host reports again. This prevents a nested SSH shell from steering the pane.</summary>
public sealed class Osc7WorkingDirectoryTracker
{
    private string? _host;

    public string? Path { get; private set; }
    public bool HostMismatch { get; private set; }

    public void Reset()
    {
        _host = null;
        Path = null;
        HostMismatch = false;
    }

    public void Observe(Osc7WorkingDirectory report)
    {
        var reportedHost = NormalizeHost(report.Host);
        if (HostMismatch && reportedHost.Length == 0)
            return;
        if (reportedHost.Length > 0 && _host is null)
            _host = reportedHost;

        if (reportedHost.Length > 0 && _host is not null &&
            !string.Equals(reportedHost, _host, StringComparison.OrdinalIgnoreCase))
        {
            Path = null;
            HostMismatch = true;
            return;
        }

        Path = report.Path;
        HostMismatch = false;
    }

    private static string NormalizeHost(string host) => host.TrimEnd('.');
}
