using System.Text.Json.Serialization;

namespace Sessions.Core.Models;

public enum AuthMethod
{
    Password,
    PrivateKey,
    None,
}

/// <summary>
/// Per-session terminal appearance overrides. Null members inherit the app-wide
/// setting, so a default instance is equivalent to no overrides at all.
/// </summary>
public sealed record TerminalOverrides
{
    public string? Theme { get; init; }
    public string? FontFamily { get; init; }
    public int? FontSize { get; init; }
    public int? Scrollback { get; init; }

    /// <summary>Highlight rules force-enabled for this session (delta against the global
    /// state, by rule id — never a copy of the rule).</summary>
    public IReadOnlyList<string>? EnabledRules { get; init; }

    /// <summary>Highlight rules force-disabled for this session (delta, by rule id).</summary>
    public IReadOnlyList<string>? DisabledRules { get; init; }

    [JsonIgnore]
    public bool IsEmpty =>
        Theme is null && FontFamily is null && FontSize is null && Scrollback is null
        && (EnabledRules is null || EnabledRules.Count == 0)
        && (DisabledRules is null || DisabledRules.Count == 0);
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

    /// <summary>
    /// Icon key: a built-in key from <see cref="SessionIcons.BuiltIn"/> ("ubuntu"), or the
    /// filename of a user icon in %APPDATA%\Sessions\icons\ ("router-lab.png").
    /// Null = unset (an icon may be auto-suggested on first connect);
    /// <see cref="SessionIcons.None"/> = explicitly no icon, never suggest.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>Set for imported sessions whose credential has not been captured yet.</summary>
    public bool CredentialNeeded { get; init; }

    /// <summary>Terminal appearance overrides for this session; null = use app settings.</summary>
    public TerminalOverrides? Overrides { get; init; }
}
