namespace Resesh.Core.Models;

/// <summary>
/// SSH algorithms turned off, by kind: on the app settings they are the defaults, on a session
/// they override those defaults kind by kind. A null list sets nothing (a session follows the
/// app; the app turns nothing off); an empty list turns everything on. Lists name what is off
/// rather than what is on, so an algorithm a later SSH.NET adds is offered without an edit.
/// Applied by <c>Resesh.Core.Ssh.SshAlgorithmPolicy</c>.
/// </summary>
public sealed record SshAlgorithms
{
    public IReadOnlyList<string>? DisabledKeyExchange { get; set; }
    public IReadOnlyList<string>? DisabledCiphers { get; set; }
    public IReadOnlyList<string>? DisabledMacs { get; set; }
    public IReadOnlyList<string>? DisabledHostKeys { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty =>
        DisabledKeyExchange is null && DisabledCiphers is null && DisabledMacs is null && DisabledHostKeys is null;
}
