using Renci.SshNet;

namespace Resesh.Core.Ssh;

/// <summary>What a live connection agreed on with the host. Ciphers and MACs are chosen per
/// direction; they almost always match, and are shown once when they do.</summary>
public sealed record SshNegotiated(
    string? KeyExchange,
    string? ServerCipher,
    string? ClientCipher,
    string? ServerMac,
    string? ClientMac,
    string? HostKey)
{
    public static SshNegotiated? From(ConnectionInfo? info) => info?.CurrentKeyExchangeAlgorithm is null
        ? null
        : new SshNegotiated(info.CurrentKeyExchangeAlgorithm, info.CurrentServerEncryption, info.CurrentClientEncryption,
            info.CurrentServerHmacAlgorithm, info.CurrentClientHmacAlgorithm, info.CurrentHostKeyAlgorithm);

    private IEnumerable<string?> Used =>
    [
        KeyExchange, ServerCipher, ClientCipher,
        IsAead(ServerCipher) ? null : ServerMac, IsAead(ClientCipher) ? null : ClientMac,
        HostKey,
    ];

    /// <summary>True when the connection runs on something marked legacy, which the user
    /// may want to know about (and could turn off for hosts that have better).</summary>
    public bool UsesLegacy => Used.Any(name => name is not null && SshAlgorithmPolicy.IsLegacy(name));

    /// <summary>One line per kind, legacy ones marked, for a tooltip.</summary>
    public string Details(string? hostKeyFingerprint)
    {
        static string Mark(string? name) => name is null ? "unknown"
            : SshAlgorithmPolicy.IsLegacy(name) ? $"{name} (legacy)" : name;
        string Pair(string? server, string? client) =>
            server == client || client is null ? Mark(server) : $"{Mark(server)} in, {Mark(client)} out";
        var mac = IsAead(ServerCipher) && IsAead(ClientCipher)
            ? "built into the cipher"
            : Pair(IsAead(ServerCipher) ? null : ServerMac, IsAead(ClientCipher) ? null : ClientMac);
        var hostKey = Mark(HostKey);
        if (!string.IsNullOrEmpty(hostKeyFingerprint))
        {
            // The fingerprint string starts with the key type; keep just the hash.
            var hash = hostKeyFingerprint[(hostKeyFingerprint.LastIndexOf(' ') + 1)..];
            hostKey += $", {hash}";
        }
        return string.Join('\n',
            $"Key exchange: {Mark(KeyExchange)}",
            $"Cipher: {Pair(ServerCipher, ClientCipher)}",
            $"MAC: {mac}",
            $"Host key: {hostKey}");
    }

    /// <summary>Ciphers that authenticate as well as encrypt, so no separate MAC is used.</summary>
    public static bool IsAead(string? cipher) =>
        cipher is not null && (cipher.Contains("-gcm", StringComparison.Ordinal)
            || cipher.StartsWith("chacha20-poly1305", StringComparison.Ordinal));
}
