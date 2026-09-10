namespace Resesh.Core.Local;

/// <summary>Parses OSC 9;9 Windows directory reports, not OSC 7 file URIs.</summary>
public static class Osc9WorkingDirectory
{
    public const int MaxPayloadLength = 4096;

    public static bool TryParse(string? payload, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength
            || !payload.StartsWith("9;", StringComparison.Ordinal))
            return false;
        var path = payload[2..];
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path[1..^1];
        if (path.Length == 0)
            return false;
        foreach (var ch in path)
            if (char.IsControl(ch) || ch is '"' or '<' or '>' or '|' or '*' or '?')
                return false;

        // Drive-relative/root-relative paths, URI paths, and device namespaces are not cwd reports.
        var drive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
        var unc = path.StartsWith(@"\\", StringComparison.Ordinal)
            && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
            && !path.StartsWith(@"\\?\", StringComparison.Ordinal);
        if (!drive && !unc)
            return false;
        if (path.AsSpan(drive ? 2 : 0).Contains(':'))
            return false;
        if (unc)
        {
            var segments = path[2..].Split(new[] { '\\', '/' });
            if (segments.Length < 2 || string.IsNullOrWhiteSpace(segments[0])
                || string.IsNullOrWhiteSpace(segments[1]) || segments[0] is "." or ".."
                || segments[1] is "." or "..")
                return false;
        }
        directory = path;
        return true;
    }
}
