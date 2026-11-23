using Resesh.Core.Models;

namespace Resesh.Core.Import;

/// <summary>One session candidate found by an importer (SecureCRT, PuTTY, OpenSSH, etc.).</summary>
public sealed record ImportCandidate
{
    // Defaulted rather than `required`: the WinUI XAML type-info generator needs a
    // parameterless activator for types reachable from x:DataType templates.
    public string Name { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = SessionEndpoint.DefaultSshPort;
    public string Username { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string? PrivateKeyPath { get; init; }

    /// <summary>The name of another candidate or saved session this one is reached through
    /// (OpenSSH ProxyJump, PuTTY's SSH proxy). Linked after import when a session has that name.</summary>
    public string? JumpHostName { get; init; }

    /// <summary>A SOCKS or HTTP proxy the source connects through. Its password is not imported.</summary>
    public ProxySettings? Proxy { get; init; }

    /// <summary>
    /// Opaque, in-memory-only token equal for sessions whose source stored the same password
    /// (see <see cref="SecureCrtImporter.PasswordFingerprint"/>). Never decrypted, never saved;
    /// null when no password is stored or the format can't be compared.
    /// </summary>
    public string? PasswordFingerprint { get; init; }

    /// <summary>The serial line, for a serial session (<see cref="SerialProtocol"/>).</summary>
    public SerialTarget? Serial { get; init; }

    /// <summary>Algorithms the source turns off for this host (from OpenSSH KexAlgorithms etc.).</summary>
    public SshAlgorithms? Algorithms { get; init; }

    /// <summary>Plain telnet (not SecureCRT's Telnet/SSL, which needs TLS we don't speak).</summary>
    public bool IsTelnet => Protocol == TelnetProtocol;

    /// <summary>A serial port with a usable port name.</summary>
    public bool IsSerial => Protocol == SerialProtocol && Serial is not null;

    public bool IsSsh => Protocol is "SSH2" or "SSH1";

    /// <summary>SSH, plain telnet and serial are importable; rlogin, raw etc. are listed as skipped.</summary>
    public bool IsSupported => IsSsh || IsTelnet || IsSerial;

    /// <summary>The normalized protocol name both importers use for telnet.</summary>
    public const string TelnetProtocol = "TELNET";

    /// <summary>The normalized protocol name importers use for serial.</summary>
    public const string SerialProtocol = "SERIAL";

    /// <summary>The port to assume when the source stores none.</summary>
    public static int DefaultPort(string protocol) =>
        SessionEndpoint.DefaultPort(protocol == TelnetProtocol ? SessionKind.Telnet : SessionKind.Ssh);
}

public sealed record ImportScanResult
{
    public required IReadOnlyList<ImportCandidate> Importable { get; init; }
    public required IReadOnlyList<ImportCandidate> Skipped { get; init; }
}
