using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class SshHostKeyTypesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("resesh-hostkey-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private KnownHostsStore NewStore()
    {
        var store = new KnownHostsStore(Path.Combine(_dir, "known_hosts.json"));
        store.Load();
        return store;
    }

    private static Session Host(SshAlgorithms? algorithms = null) =>
        new() { Name = "edge", Host = "edge.example", Port = 22, Username = "u", AuthMethod = AuthMethod.None, Algorithms = algorithms };

    private static Renci.SshNet.ConnectionInfo Info(Session session, KnownHostsStore store) =>
        SshConnectionFactory.CreateConnectionInfo(session, SshConnectionFactory.BuildAuthMethods(session, null),
            TimeSpan.FromSeconds(5), null, null, store);

    [Theory]
    [InlineData("rsa-sha2-512", "ssh-rsa", true)]
    [InlineData("rsa-sha2-256", "rsa-sha2-512", true)]
    [InlineData("rsa-sha2-512-cert-v01@openssh.com", "ssh-rsa-cert-v01@openssh.com", true)]
    [InlineData("ssh-rsa", "ssh-rsa-cert-v01@openssh.com", false)]
    [InlineData("ssh-ed25519", "ecdsa-sha2-nistp256", false)]
    public void SameKey_GroupsRsaSignatures(string a, string b, bool same) =>
        Assert.Equal(same, SshHostKeyTypes.SameKey(a, b));

    [Fact]
    public void TrustedRsaKey_MatchesWhicheverSignatureTheHostUses()
    {
        // The same RSA key, trusted when an old host only signed with ssh-rsa, seen again
        // after a firmware update adds rsa-sha2-512: not a changed key.
        var store = NewStore();
        store.Accept("edge.example", 22, "ssh-rsa", "abc");

        Assert.Equal(HostKeyVerdict.Match, store.Check("edge.example", 22, "rsa-sha2-512", "abc"));
        Assert.Equal(HostKeyVerdict.Mismatch, store.Check("edge.example", 22, "rsa-sha2-512", "def"));
        Assert.Equal(HostKeyVerdict.Mismatch, store.Check("edge.example", 22, "ssh-ed25519", "abc"));
    }

    [Fact]
    public void ConnectionOffersTheTrustedTypeFirst()
    {
        var store = NewStore();
        store.Accept("edge.example", 22, "rsa-sha2-512", "abc");

        var offered = Info(Host(), store).HostKeyAlgorithms.Keys.ToList();

        Assert.True(SshHostKeyTypes.SameKey(offered[0], "ssh-rsa"));
        Assert.Equal(["rsa-sha2-512", "rsa-sha2-256", "ssh-rsa"], offered.Take(3));
        Assert.Equal(SshAlgorithmPolicy.Supported(SshAlgorithmKind.HostKey).Order(), offered.Order());
    }

    [Fact]
    public void UnknownHost_KeepsSshNetOrder()
    {
        Assert.Equal(SshAlgorithmPolicy.Supported(SshAlgorithmKind.HostKey), Info(Host(), NewStore()).HostKeyAlgorithms.Keys);
    }

    [Fact]
    public void TrustedTypeTurnedOff_SaysSoInsteadOfReportingAChangedKey()
    {
        var store = NewStore();
        store.Accept("edge.example", 22, "ecdsa-sha2-nistp256", "abc");
        var session = Host(new SshAlgorithms { DisabledHostKeys = ["ecdsa-sha2-nistp256"] });
        var info = Info(session, store);

        var failure = SshHostKeyTypes.TrustedTypeTurnedOff(info, session, store.Lookup("edge.example", 22)!, "ssh-ed25519");

        Assert.NotNull(failure);
        Assert.Equal(SshFailureKind.HostKeyRejected, failure.Kind);
        Assert.Contains("ECDSA P-256", failure.Message);
        Assert.Contains("Ed25519", failure.Message);
        // Offered, so a different key is a real change and gets the usual warning.
        Assert.Null(SshHostKeyTypes.TrustedTypeTurnedOff(Info(Host(), store), Host(), store.Lookup("edge.example", 22)!, "ssh-ed25519"));
    }

    [Fact]
    public void Negotiated_MarksLegacyAndFoldsAeadMacs()
    {
        var modern = new SshNegotiated("curve25519-sha256", "aes256-gcm@openssh.com", "aes256-gcm@openssh.com",
            "hmac-sha1", "hmac-sha1", "ssh-ed25519");
        Assert.False(modern.UsesLegacy);
        Assert.Equal(
            "Key exchange: curve25519-sha256\nCipher: aes256-gcm@openssh.com\nMAC: built into the cipher\nHost key: ssh-ed25519, SHA256:xyz",
            modern.Details("ssh-ed25519 SHA256:xyz"));

        var old = new SshNegotiated("diffie-hellman-group1-sha1", "aes128-cbc", "aes128-cbc", "hmac-sha1", "hmac-sha1", "ssh-rsa");
        Assert.True(old.UsesLegacy);
        Assert.Contains("Cipher: aes128-cbc (legacy)", old.Details(null));
    }
}
