namespace Sessions.Core.Models;

public enum AuthMethod
{
    Password,
    PrivateKey,
    None,
}

/// <summary>
/// A saved SSH session. Secrets (password / key passphrase) are never stored here;
/// they live in Windows Credential Manager keyed by <see cref="Id"/>.
/// </summary>
public sealed record Session
{
    public Guid Id { get; init; } = Guid.NewGuid();

    // Note: no `required` members — the WinUI XAML type-info generator needs a
    // parameterless activator for types used as x:DataType.
    public string Name { get; init; } = "";

    /// <summary>Forward-slash separated folder path, e.g. "Datacenter/Rack 4". Empty = root.</summary>
    public string FolderPath { get; init; } = "";

    public string Host { get; init; } = "";

    public int Port { get; init; } = 22;

    public string Username { get; init; } = "";

    public AuthMethod AuthMethod { get; init; } = AuthMethod.Password;

    public string? PrivateKeyPath { get; init; }

    public bool PassphraseRequired { get; init; }

    public string TerminalType { get; init; } = "xterm-256color";

    /// <summary>Run the remote shell inside tmux so it survives disconnects (requires tmux on the host).</summary>
    public bool Persistent { get; init; }

    public string Notes { get; init; } = "";

    /// <summary>Optional accent color for the tab, as #RRGGBB. Null = none.</summary>
    public string? ColorTag { get; init; }

    /// <summary>Set for imported sessions whose credential has not been captured yet.</summary>
    public bool CredentialNeeded { get; init; }
}
