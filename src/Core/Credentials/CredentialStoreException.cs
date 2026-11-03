namespace Resesh.Core.Credentials;

/// <summary>
/// A password or passphrase could not be read, saved or deleted. An <see cref="IOException"/>
/// so the handlers that already report failed store writes report this too, instead of a
/// raw Win32Exception escaping them.
/// </summary>
public sealed class CredentialStoreException : IOException
{
    /// <summary>Credential Manager rejects a blob over CRED_MAX_CREDENTIAL_BLOB_SIZE (5 × 512
    /// bytes); secrets are stored as UTF-16, two bytes per char.</summary>
    public const int MaxSecretLength = 5 * 512 / 2;

    public CredentialStoreException(string message, Exception? inner = null) : base(message, inner)
    {
    }

    /// <summary>Throws before anything is saved when the secret is too long for Credential
    /// Manager. Every backend applies it, so a scratch profile accepts what the real one does.</summary>
    public static void ThrowIfTooLong(string secret)
    {
        if (secret.Length > MaxSecretLength)
        {
            throw new CredentialStoreException(
                $"The password is too long to save: Windows Credential Manager holds at most {MaxSecretLength} characters.");
        }
    }
}
