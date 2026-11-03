namespace Resesh.Core.Models;

/// <summary>
/// Parses quick-connect text into an ad-hoc (unsaved) session: "ssh user@host",
/// "user@host:2222", "ssh [2001:db8::1]:2222" (IPv6 as in OpenSSH, see
/// <see cref="HostPort"/>), "telnet host", "telnet host:2003" or "telnet host 2003" (the classic
/// telnet command form — terminal servers put console lines on high ports, so the port
/// must be easy to type), or "serial COM5", "serial COM5 115200", "serial COM5:115200 7E1".
/// A bare hostname or port only counts with an explicit "ssh "/"telnet "/"serial " prefix,
/// so plain words keep meaning "search my saved sessions".
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
        if (text.StartsWith("serial ", StringComparison.OrdinalIgnoreCase))
            return TryParseSerial(text[7..].Trim(), out session);

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
        if (!HostPort.TrySplit(text, out var host, out var explicitPort) || at == 0)
            return false;

        var username = user.Length > 0 ? user : defaultUsername;
        session = new Session
        {
            Host = host,
            Port = explicitPort ?? SessionEndpoint.DefaultSshPort,
            Username = username,
            AuthMethod = AuthMethod.Password,
        };
        session = session with { Name = SessionEndpoint.Format(session, new() { Username = username }) };
        return true;
    }

    private static bool TryParseTelnet(string text, out Session? session)
    {
        session = null;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !HostPort.TrySplit(parts[0], out var host, out var explicitPort))
            return false;
        var port = explicitPort ?? SessionEndpoint.DefaultTelnetPort;
        // "telnet host 2003": the port is the second word, so the host must not carry one too.
        if (parts.Length == 2 && (explicitPort is not null
                || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535))
            return false;
        if (host.Contains('@'))
            return false; // telnet has no username in the target; the server prompts

        session = new Session
        {
            Kind = SessionKind.Telnet,
            Host = host,
            Port = port,
            AuthMethod = AuthMethod.None,
        };
        session = session with { Name = SessionEndpoint.Format(session) };
        return true;
    }

    private static bool TryParseSerial(string text, out Session? session)
    {
        session = null;
        var parts = text.Replace(':', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 3 || SerialTarget.NormalizePortName(parts[0]) is not { } portName)
            return false;
        var target = new SerialTarget { PortName = portName };
        foreach (var part in parts.Skip(1))
        {
            if (int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var baud)
                && baud is >= 50 and <= 4_000_000)
                target = target with { BaudRate = baud };
            else if (TryParseFraming(part, target, out var framed))
                target = framed;
            else
                return false;
        }

        session = new Session
        {
            Kind = SessionKind.Serial,
            Name = portName,
            Serial = target,
            Port = 0,
            AuthMethod = AuthMethod.None,
        };
        return true;
    }

    /// <summary>"8N1", "7E1", "8N2": data bits, parity letter, stop bits.</summary>
    private static bool TryParseFraming(string text, SerialTarget target, out SerialTarget framed)
    {
        framed = target;
        if (text.Length != 3 || text[0] is < '5' or > '8' || text[2] is not ('1' or '2'))
            return false;
        SerialParity? parity = char.ToUpperInvariant(text[1]) switch
        {
            'N' => SerialParity.None,
            'O' => SerialParity.Odd,
            'E' => SerialParity.Even,
            'M' => SerialParity.Mark,
            'S' => SerialParity.Space,
            _ => null,
        };
        if (parity is null)
            return false;
        framed = target with
        {
            DataBits = text[0] - '0',
            Parity = parity.Value,
            StopBits = text[2] == '2' ? SerialStopBits.Two : SerialStopBits.One,
        };
        return true;
    }
}
