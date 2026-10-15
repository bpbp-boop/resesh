using System.Text.Json.Serialization;

namespace Resesh.Core.Models;

/// <summary>How an identity proves who it is.</summary>
public enum IdentityProof
{
    /// <summary>A password kept in Windows Credential Manager under the identity's id.</summary>
    Password,

    /// <summary>A registered SSH key (<see cref="Identity.KeyId"/>); its passphrase stays with the key.</summary>
    Key,

    /// <summary>A password typed when connecting, kept in memory until the app closes.</summary>
    Ask,
}

/// <summary>
/// A named login: a username plus one way to prove it. Attached to folders (inherited by
/// every session inside) or chosen by a session. Secrets never live here.
/// </summary>
public sealed record Identity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public IdentityProof Proof { get; set; } = IdentityProof.Password;

    /// <summary>The registered SSH key; set exactly when <see cref="Proof"/> is Key.</summary>
    public Guid? KeyId { get; init; }

    public string Notes { get; set; } = "";

    /// <summary>The session auth method this identity connects with.</summary>
    [JsonIgnore]
    public AuthMethod AuthMethod => Proof == IdentityProof.Key ? AuthMethod.PrivateKey : AuthMethod.Password;
}

/// <summary>Per-folder settings in the remote session tree, keyed by normalized folder path.
/// The empty path is the tree root: its identity applies to every inheriting session that
/// has no closer folder identity.</summary>
public sealed record FolderSettings
{
    public string Path { get; set; } = "";
    public Guid? IdentityId { get; init; }
}
