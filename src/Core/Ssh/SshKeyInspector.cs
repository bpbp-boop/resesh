using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Sessions.Core.Ssh;

public sealed record SshKeyInspection(
    string? Algorithm,
    int? KeyLength,
    string? Fingerprint,
    bool? IsEncrypted,
    string? PublicKey);

public sealed class SshKeyPassphraseException : Exception
{
    public SshKeyPassphraseException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class SshKeyChangedException : Exception
{
    public string KeyName { get; }
    public string PreviousFingerprint { get; }
    public string CurrentFingerprint { get; }

    public SshKeyChangedException(string keyName, string previousFingerprint, string currentFingerprint)
        : base($"The registered SSH key '{keyName}' has changed. Verify its new public fingerprint before using it.")
    {
        KeyName = keyName;
        PreviousFingerprint = previousFingerprint;
        CurrentFingerprint = currentFingerprint;
    }
}

/// <summary>Reads public metadata from an SSH private key or its adjacent .pub file.</summary>
public static class SshKeyInspector
{
    public static SshKeyInspection Inspect(string path, string? passphrase = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A private-key path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("The private-key file was not found.", path);
        if (path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select the private-key file, not its .pub file.");

        var publicInspection = TryInspectPublicFile(path + ".pub");
        var encrypted = DetectEncryption(path);
        try
        {
            using var keyFile = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(path)
                : new PrivateKeyFile(path, passphrase);
            var hostAlgorithm = keyFile.HostKeyAlgorithms.First();
            var publicBlob = hostAlgorithm.Data;
            var algorithm = hostAlgorithm.Name;
            return new SshKeyInspection(
                algorithm,
                keyFile.Key.KeyLength,
                Fingerprint(publicBlob),
                encrypted ?? !string.IsNullOrEmpty(passphrase),
                $"{algorithm} {Convert.ToBase64String(publicBlob)}");
        }
        catch (SshPassPhraseNullOrEmptyException) when (string.IsNullOrEmpty(passphrase))
        {
            return publicInspection is null
                ? new SshKeyInspection(null, null, null, true, null)
                : publicInspection with { IsEncrypted = true };
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException or ArgumentException
            or CryptographicException)
        {
            if (string.IsNullOrEmpty(passphrase) && encrypted == true)
            {
                return publicInspection is null
                    ? new SshKeyInspection(null, null, null, true, null)
                    : publicInspection with { IsEncrypted = true };
            }
            if (!string.IsNullOrEmpty(passphrase) || encrypted == true)
                throw new SshKeyPassphraseException("The private-key passphrase was not accepted.", ex);
            throw new InvalidDataException("The selected file is not a supported SSH private key.", ex);
        }
    }

    private static SshKeyInspection? TryInspectPublicFile(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var fields = File.ReadAllText(path).Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
                return null;
            var blob = Convert.FromBase64String(fields[1]);
            return new SshKeyInspection(fields[0], null, Fingerprint(blob), null,
                $"{fields[0]} {fields[1]}" + (fields.Length == 3 ? $" {fields[2]}" : ""));
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Fingerprint(byte[] publicBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicBlob)).TrimEnd('=');

    private static bool? DetectEncryption(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        var text = Encoding.ASCII.GetString(bytes);
        if (text.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.Ordinal)
            || text.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal))
            return true;
        if (text.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal))
        {
            var encryption = text.Split('\n').FirstOrDefault(line => line.StartsWith("Encryption:", StringComparison.Ordinal));
            return encryption is null ? null : !encryption.Trim().Equals("Encryption: none", StringComparison.OrdinalIgnoreCase);
        }
        if (!text.Contains("BEGIN OPENSSH PRIVATE KEY", StringComparison.Ordinal))
            return null;

        try
        {
            var body = string.Concat(text.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("-----", StringComparison.Ordinal)));
            var decoded = Convert.FromBase64String(body);
            ReadOnlySpan<byte> magic = "openssh-key-v1\0"u8;
            if (!decoded.AsSpan().StartsWith(magic))
                return null;
            var offset = magic.Length;
            if (decoded.Length < offset + 4)
                return null;
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(offset, 4)));
            offset += 4;
            if (length < 0 || decoded.Length < offset + length)
                return null;
            var cipher = Encoding.ASCII.GetString(decoded, offset, length);
            return !cipher.Equals("none", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
