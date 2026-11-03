namespace Resesh.Core.Models;

/// <summary>How a local profile's shell reads where a remote session shows its host.</summary>
public enum LocalShellDisplay
{
    /// <summary>"pwsh.exe"</summary>
    FileName,

    /// <summary>"pwsh"</summary>
    FileNameWithoutExtension,

    /// <summary>The executable as saved, path and all.</summary>
    FullPath,
}

/// <summary>Choices for <see cref="SessionEndpoint.Format(Session, EndpointStyle)"/>. The default
/// is the compact form: no user, no default port, no protocol prefix.</summary>
public readonly record struct EndpointStyle
{
    /// <summary>The login name to show as "user@" on an SSH target. Null or empty shows none,
    /// so a session that asks for its login never reads "@host".</summary>
    public string? Username { get; init; }

    /// <summary>Shows the port even when it is the protocol's default.</summary>
    public bool ShowDefaultPort { get; init; }

    /// <summary>Prefixes telnet and serial targets with their protocol ("telnet host",
    /// "serial COM3 9600 8N1"), for surfaces with no other cue to the session's kind.</summary>
    public bool ShowProtocol { get; init; }

    /// <summary>A serial target shows only its port name, not its line settings.</summary>
    public bool SerialPortOnly { get; init; }

    public LocalShellDisplay LocalShell { get; init; }
}

/// <summary>
/// Where a session connects, as text. Every surface that shows a host (the tree, tabs, quick
/// connect, history, import and backup review, the sessions manager) formats it here, so they
/// agree on default ports, an unset port, protocol prefixes and the fallbacks for missing values.
/// </summary>
public static class SessionEndpoint
{
    public const int DefaultSshPort = 22;
    public const int DefaultTelnetPort = 23;

    public static int DefaultPort(SessionKind kind) =>
        kind == SessionKind.Telnet ? DefaultTelnetPort : DefaultSshPort;

    /// <summary>The port a connection uses: an unset (zero or negative) port means the default.</summary>
    public static int EffectivePort(SessionKind kind, int port) => port > 0 ? port : DefaultPort(kind);

    public static string Format(Session session, EndpointStyle style = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Format(session.Kind, session.Host, session.Port, session.Serial, session.Local?.Executable, style);
    }

    /// <summary>Formats a target that is not a saved session yet, such as an import candidate.</summary>
    public static string Format(
        SessionKind kind,
        string? host,
        int port,
        SerialTarget? serial,
        string? executable,
        EndpointStyle style = default)
    {
        switch (kind)
        {
            case SessionKind.Local:
                return LocalShell(executable, style.LocalShell);
            case SessionKind.Serial:
                var line = serial is null ? "" : style.SerialPortOnly ? serial.PortName : serial.Describe();
                if (string.IsNullOrWhiteSpace(line))
                    return "serial";
                return style.ShowProtocol ? $"serial {line}" : line;
            case SessionKind.Telnet:
                var address = Address(kind, host, port, style.ShowDefaultPort);
                return style.ShowProtocol ? $"telnet {address}" : address;
            default:
                var target = Address(kind, host, port, style.ShowDefaultPort);
                return string.IsNullOrEmpty(style.Username) ? target : $"{style.Username}@{target}";
        }
    }

    private static string Address(SessionKind kind, string? host, int port, bool showDefaultPort)
    {
        var effective = EffectivePort(kind, port);
        return showDefaultPort || effective != DefaultPort(kind) ? $"{host}:{effective}" : host ?? "";
    }

    private static string LocalShell(string? executable, LocalShellDisplay display)
    {
        var path = executable?.Trim() ?? "";
        if (path.Length == 0)
            return "local shell";
        if (display == LocalShellDisplay.FullPath)
            return path;
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (name.Length == 0)
            return path;
        return display == LocalShellDisplay.FileNameWithoutExtension ? Path.GetFileNameWithoutExtension(name) : name;
    }
}
