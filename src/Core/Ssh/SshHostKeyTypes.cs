using Renci.SshNet;

namespace Resesh.Core.Ssh;

/// <summary>
/// Host key algorithm names grouped by the key they verify. SSH.NET reports the negotiated
/// signature algorithm, so one RSA host key arrives as <c>rsa-sha2-512</c>, <c>rsa-sha2-256</c>
/// or <c>ssh-rsa</c> (same blob, same fingerprint) depending on what both sides offered.
/// Trust is about the key, so it compares families.
/// </summary>
public static class SshHostKeyTypes
{
    private const string RsaCert = "ssh-rsa-cert-v01@openssh.com";

    /// <summary>The key type <paramref name="algorithm"/> verifies with: <c>ssh-rsa</c> for
    /// every RSA signature, otherwise the name itself.</summary>
    public static string Family(string algorithm) => algorithm.ToLowerInvariant() switch
    {
        "ssh-rsa" or "rsa-sha2-256" or "rsa-sha2-512" => "ssh-rsa",
        RsaCert or "rsa-sha2-256-cert-v01@openssh.com" or "rsa-sha2-512-cert-v01@openssh.com" => RsaCert,
        var other => other,
    };

    public static bool SameKey(string algorithm, string other) =>
        string.Equals(Family(algorithm), Family(other), StringComparison.Ordinal);

    /// <summary>"RSA", "Ed25519", "ECDSA P-256", with " certificate" for certificates.</summary>
    public static string Describe(string algorithm)
    {
        var family = Family(algorithm);
        var certificate = family.EndsWith("-cert-v01@openssh.com", StringComparison.Ordinal);
        var key = certificate ? family[..^"-cert-v01@openssh.com".Length] : family;
        var name = key switch
        {
            "ssh-rsa" => "RSA",
            "ssh-ed25519" => "Ed25519",
            "ecdsa-sha2-nistp256" => "ECDSA P-256",
            "ecdsa-sha2-nistp384" => "ECDSA P-384",
            "ecdsa-sha2-nistp521" => "ECDSA P-521",
            "ssh-dss" => "DSA",
            _ => key,
        };
        return certificate ? $"{name} certificate" : name;
    }

    /// <summary>Like OpenSSH, offers the algorithms for the key already trusted for this host
    /// first, so the host shows that key rather than another one it also has. Without this,
    /// turning one type off (or a host adding a stronger one) reads as a changed key.</summary>
    internal static void PreferTrusted(ConnectionInfo info, KnownHostEntry? trusted)
    {
        if (trusted is null)
            return;
        var algorithms = info.HostKeyAlgorithms;
        var entries = algorithms.ToList();
        var preferred = entries.Where(entry => SameKey(entry.Key, trusted.KeyType)).ToList();
        if (preferred.Count == 0 || entries.Take(preferred.Count).SequenceEqual(preferred))
            return;
        algorithms.Clear();
        foreach (var (name, factory) in preferred.Concat(entries.Except(preferred)))
            algorithms.Add(name, factory);
    }

    /// <summary>When the trusted key's type is turned off, the host can only show another key,
    /// which says nothing about whether it is the same host. That is reported as a setting to
    /// change, not as a changed key. Null when the trusted type is offered.</summary>
    internal static SshSessionException? TrustedTypeTurnedOff(
        ConnectionInfo info, Models.Session session, KnownHostEntry trusted, string presented)
    {
        if (SameKey(presented, trusted.KeyType) || info.HostKeyAlgorithms.Keys.Any(name => SameKey(name, trusted.KeyType)))
            return null;
        var type = Describe(trusted.KeyType);
        return new SshSessionException(SshFailureKind.HostKeyRejected,
            $"{session.Host} is trusted by its {type} host key, but {type} host keys are turned off for this "
            + $"session, so the host showed its {Describe(presented)} key instead, which cannot be checked "
            + "against the trusted one. Turn the trusted type back on in the session's Advanced settings or in "
            + "Settings › Identities & Keys › SSH algorithms.");
    }
}
