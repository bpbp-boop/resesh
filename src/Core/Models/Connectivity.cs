using System.Text.Json.Serialization;

namespace Resesh.Core.Models;

public enum ProxyKind
{
    Socks5,
    Socks4,
    Http,
}

/// <summary>
/// A SOCKS or HTTP CONNECT proxy between this machine and an SSH host. The password, if
/// any, lives in Windows Credential Manager keyed by the session id, never here.
/// </summary>
public sealed record ProxySettings
{
    public ProxyKind Kind { get; set; } = ProxyKind.Socks5;

    public string Host { get; set; } = "";

    public int Port { get; set; } = DefaultPort(ProxyKind.Socks5);

    /// <summary>Empty = the proxy needs no login.</summary>
    public string Username { get; set; } = "";

    public static int DefaultPort(ProxyKind kind) => kind == ProxyKind.Http ? 8080 : 1080;

    [JsonIgnore]
    public string Label => Kind switch
    {
        ProxyKind.Socks4 => "SOCKS4",
        ProxyKind.Http => "HTTP",
        _ => "SOCKS5",
    } + $" proxy {Host}:{Port}";
}

public enum TunnelKind
{
    /// <summary>Listen on this machine; connections leave from the SSH host (ssh -L).</summary>
    Local,

    /// <summary>Listen on the SSH host; connections leave from this machine (ssh -R).</summary>
    Remote,

    /// <summary>A SOCKS proxy on this machine whose connections leave from the SSH host (ssh -D).</summary>
    Dynamic,
}

/// <summary>One port forward carried by an SSH session's connection.</summary>
public sealed record Tunnel
{
    public const string Loopback = "127.0.0.1";

    public Guid Id { get; set; } = Guid.NewGuid();

    public TunnelKind Kind { get; set; } = TunnelKind.Local;

    /// <summary>The address listened on: this machine's for Local and Dynamic, the SSH
    /// host's for Remote. Loopback unless the user chose to expose the port.</summary>
    public string BindAddress { get; set; } = Loopback;

    public int BindPort { get; init; }

    /// <summary>Where connections are sent; unused for Dynamic, whose clients choose.</summary>
    public string DestinationHost { get; set; } = "";

    public int DestinationPort { get; init; }

    /// <summary>Why the tunnel cannot be started as written, or null when it can.</summary>
    public string? Validate()
    {
        if (BindPort is < 1 or > 65535)
            return "Enter a listening port between 1 and 65535.";
        if (string.IsNullOrWhiteSpace(BindAddress))
            return "Enter the address to listen on.";
        if (Kind == TunnelKind.Dynamic)
            return null;
        if (string.IsNullOrWhiteSpace(DestinationHost))
            return "Enter the destination host.";
        return DestinationPort is < 1 or > 65535 ? "Enter a destination port between 1 and 65535." : null;
    }

    /// <summary>A one-line description in the direction traffic flows.</summary>
    public string Describe() => Kind switch
    {
        TunnelKind.Remote => $"Remote {BindAddress}:{BindPort} → {DestinationHost}:{DestinationPort}",
        TunnelKind.Dynamic => $"SOCKS {BindAddress}:{BindPort}",
        _ => $"Local {BindAddress}:{BindPort} → {DestinationHost}:{DestinationPort}",
    };
}
