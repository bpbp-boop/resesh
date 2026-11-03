using System.Globalization;

namespace Resesh.Core.Models;

/// <summary>
/// Reads "host", "host:port", "[v6]", "[v6]:port" and a bare IPv6 literal the way OpenSSH
/// does: a colon is a port separator only when it is the text's single colon, so "::1" and
/// "2001:db8::22" are hosts, and a port on an IPv6 literal needs brackets. Hosts come back
/// without brackets: they are stored that way, and code that needs "[v6]:port" (known_hosts,
/// endpoint text) adds the brackets itself.
/// </summary>
public static class HostPort
{
    /// <summary>Splits <paramref name="text"/> into a host and, when one is written, a port
    /// (1–65535). False for an empty host, a malformed bracket or a port that is not a number
    /// in range.</summary>
    public static bool TrySplit(string? text, out string host, out int? port)
    {
        host = "";
        port = null;
        var value = text?.Trim() ?? "";
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0)
                return false;
            host = value[1..close];
            var rest = value[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':' || !TryParsePort(rest[1..], out var bracketedPort))
                    return false;
                port = bracketedPort;
            }
        }
        else
        {
            var colon = value.IndexOf(':');
            if (colon >= 0 && colon == value.LastIndexOf(':'))
            {
                if (!TryParsePort(value[(colon + 1)..], out var hostPort))
                    return false;
                host = value[..colon];
                port = hostPort;
            }
            else
            {
                host = value;
            }
        }
        return host.Length > 0 && host.IndexOfAny(['[', ']']) < 0;
    }

    /// <summary>The host as stored: trimmed and without the brackets of "[v6]".</summary>
    public static string Unbracket(string? host)
    {
        var value = host?.Trim() ?? "";
        return value.Length > 2 && value[0] == '[' && value[^1] == ']' ? value[1..^1] : value;
    }

    /// <summary>The host as written before ":port": an IPv6 literal in brackets.</summary>
    public static string WithPort(string? host, int port)
    {
        var value = host ?? "";
        return value.Contains(':') && !value.StartsWith('[') ? $"[{value}]:{port}" : $"{value}:{port}";
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}
