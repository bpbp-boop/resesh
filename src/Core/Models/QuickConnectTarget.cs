namespace Resesh.Core.Models;

/// <summary>
/// Parses quick-connect text into an ad-hoc (unsaved) session: "ssh user@host",
/// "user@host:2222", "telnet host", "telnet host:2003" or "telnet host 2003" (the classic
/// telnet command form — terminal servers put console lines on high ports, so the port
/// must be easy to type). A bare hostname only counts with an explicit "ssh "/"telnet "
/// prefix, so plain words keep meaning "search my saved sessions".
/// </summary>
public static class QuickConnectTarget
{
    public static bool TryParse(string input, string defaultUsername,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Session? session)
    {
        session = null;
        var text = input.Trim();
        if (text.StartsWith("telnet ", StringComparison.OrdinalIgnoreCase))
            return TryParseTelnet(text[7..].Trim(), out session);

        var explicitSsh = text.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase);
        if (explicitSsh)
            text = text[4..].Trim();
        if (text.Length == 0 || text.Contains(' ') || (!explicitSsh && !text.Contains('@')))
            return false;

        var user = "";
        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            user = text[..at];
            text = text[(at + 1)..];
        }
        if (!TrySplitPort(text, 22, out var host, out var port) || at == 0)
            return false;

        var username = user.Length > 0 ? user : defaultUsername;
        session = new Session
        {
            Name = $"{username}@{host}",
            Host = host,
            Port = port,
            Username = username,
            AuthMethod = AuthMethod.Password,
        };
        return true;
    }

    private static bool TryParseTelnet(string text, out Session? session)
    {
        session = null;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string host;
        int port;
        switch (parts.Length)
        {
            case 1:
                if (!TrySplitPort(parts[0], 23, out host, out port))
                    return false;
                break;
            case 2:
                host = parts[0];
                if (host.Contains(':') || !int.TryParse(parts[1], out port) || port is < 1 or > 65535)
                    return false;
                break;
            default:
                return false;
        }
        if (host.Contains('@'))
            return false; // telnet has no username in the target; the server prompts

        session = new Session
        {
            Kind = SessionKind.Telnet,
            Name = port == 23 ? host : $"{host}:{port}",
            Host = host,
            Port = port,
            AuthMethod = AuthMethod.None,
        };
        return true;
    }

    private static bool TrySplitPort(string text, int defaultPort, out string host, out int port)
    {
        host = text;
        port = defaultPort;
        var colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535)
                return false;
            host = text[..colon];
        }
        return host.Length > 0;
    }
}
