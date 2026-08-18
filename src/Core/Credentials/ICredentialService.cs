namespace Sessions.Core.Credentials;

/// <summary>
/// Stores per-session secrets (password or key passphrase) outside the JSON store.
/// Implemented against Windows Credential Manager; abstracted for tests.
/// </summary>
public interface ICredentialService
{
    /// <summary>Returns the stored secret for a session, or null if none.</summary>
    string? Read(Guid sessionId);

    void Write(Guid sessionId, string secret);

    /// <summary>Deletes the secret; no-op if absent.</summary>
    void Delete(Guid sessionId);

    /// <summary>Returns the passphrase shared by every session that uses this SSH key.</summary>
    string? ReadKey(Guid keyId) => Read(keyId);

    void WriteKey(Guid keyId, string secret) => Write(keyId, secret);

    /// <summary>Deletes a key passphrase; no-op if absent.</summary>
    void DeleteKey(Guid keyId) => Delete(keyId);
}
