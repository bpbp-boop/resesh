using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class SshAlgorithmPolicyTests
{
    private static readonly string[] Defaults = ["curve25519-sha256", "ecdh-sha2-nistp256", "diffie-hellman-group14-sha1", "diffie-hellman-group1-sha1"];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyList_KeepsDefaults(string? spec) =>
        Assert.Equal(Defaults, SshAlgorithmPolicy.Resolve(Defaults, spec).Algorithms);

    [Fact]
    public void PlainList_ReplacesDefaultsInItsOwnOrder()
    {
        var list = SshAlgorithmPolicy.Resolve(Defaults, "diffie-hellman-group14-sha1, curve25519-sha256");
        Assert.Equal(["diffie-hellman-group14-sha1", "curve25519-sha256"], list.Algorithms);
        Assert.Empty(list.Unsupported);
    }

    [Fact]
    public void Caret_MovesListedToTheFront()
    {
        var list = SshAlgorithmPolicy.Resolve(Defaults, "^diffie-hellman-group1-sha1");
        Assert.Equal(["diffie-hellman-group1-sha1", "curve25519-sha256", "ecdh-sha2-nistp256", "diffie-hellman-group14-sha1"], list.Algorithms);
    }

    [Fact]
    public void Minus_RemovesWithWildcards()
    {
        var list = SshAlgorithmPolicy.Resolve(Defaults, "-diffie-hellman-*-sha1");
        Assert.Equal(["curve25519-sha256", "ecdh-sha2-nistp256"], list.Algorithms);
        Assert.Empty(list.Unsupported);
    }

    [Fact]
    public void Plus_OfAnAlgorithmAlreadyOffered_KeepsDefaults()
    {
        // The usual legacy line in ~/.ssh/config: SSH.NET offers it already.
        var list = SshAlgorithmPolicy.Resolve(Defaults, "+diffie-hellman-group1-sha1");
        Assert.Equal(Defaults, list.Algorithms);
        Assert.Empty(list.Unsupported);
    }

    [Fact]
    public void UnsupportedNames_AreReportedAndLeftOut()
    {
        var list = SshAlgorithmPolicy.Resolve(Defaults, "+ssh-dss");
        Assert.Equal(Defaults, list.Algorithms);
        Assert.Equal(["ssh-dss"], list.Unsupported);
        Assert.Null(list.Error);

        var replaced = SshAlgorithmPolicy.Resolve(Defaults, "arcfour,curve25519-sha256");
        Assert.Equal(["curve25519-sha256"], replaced.Algorithms);
        Assert.Equal(["arcfour"], replaced.Unsupported);
    }

    [Fact]
    public void ListLeavingNothing_IsAnError()
    {
        Assert.NotNull(SshAlgorithmPolicy.Resolve(Defaults, "arcfour").Error);
        Assert.NotNull(SshAlgorithmPolicy.Resolve(Defaults, "-*").Error);
    }

    [Theory]
    [InlineData("aes128-ctr", "aes*-ctr", true)]
    [InlineData("aes128-cbc", "aes*-ctr", false)]
    [InlineData("hmac-sha1", "hmac-sha?", true)]
    [InlineData("hmac-sha2-256", "hmac-sha?", false)]
    [InlineData("anything", "*", true)]
    public void Wildcards_MatchLikeOpenSsh(string text, string pattern, bool expected) =>
        Assert.Equal(expected, SshAlgorithmPolicy.Matches(text, pattern));

    [Fact]
    public void Supported_ListsSshNetDefaults_IncludingLegacyAlgorithms()
    {
        // Old network gear depends on these; a library update that drops one should be noticed.
        Assert.Contains("diffie-hellman-group1-sha1", SshAlgorithmPolicy.Supported(SshAlgorithmKind.KeyExchange));
        Assert.Contains("diffie-hellman-group14-sha1", SshAlgorithmPolicy.Supported(SshAlgorithmKind.KeyExchange));
        Assert.Contains("aes128-cbc", SshAlgorithmPolicy.Supported(SshAlgorithmKind.Cipher));
        Assert.Contains("3des-cbc", SshAlgorithmPolicy.Supported(SshAlgorithmKind.Cipher));
        Assert.Contains("hmac-sha1", SshAlgorithmPolicy.Supported(SshAlgorithmKind.Mac));
        Assert.Contains("ssh-rsa", SshAlgorithmPolicy.Supported(SshAlgorithmKind.HostKey));
    }

    private static Renci.SshNet.ConnectionInfo Connect(Session session)
    {
        var auth = SshConnectionFactory.BuildAuthMethods(session, null);
        return SshConnectionFactory.CreateConnectionInfo(session, auth, TimeSpan.FromSeconds(5), null, null);
    }

    private static Session Host(SshAlgorithms? algorithms = null) =>
        new() { Name = "old-switch", Host = "h", Username = "u", AuthMethod = AuthMethod.None, Algorithms = algorithms };

    [Fact]
    public void SessionChoices_OverrideAppChoices_KindByKind()
    {
        var app = new SshAlgorithms { DisabledCiphers = ["3des-cbc"], DisabledMacs = ["hmac-sha1"] };
        var session = new SshAlgorithms { DisabledCiphers = [], DisabledKeyExchange = ["diffie-hellman-group1-sha1"] };

        Assert.Contains("3des-cbc", SshAlgorithmPolicy.Offered(SshAlgorithmKind.Cipher, session, app));
        Assert.DoesNotContain("hmac-sha1", SshAlgorithmPolicy.Offered(SshAlgorithmKind.Mac, session, app));
        Assert.DoesNotContain("diffie-hellman-group1-sha1", SshAlgorithmPolicy.Offered(SshAlgorithmKind.KeyExchange, session, app));
        Assert.Equal(SshAlgorithmPolicy.Supported(SshAlgorithmKind.HostKey), SshAlgorithmPolicy.Offered(SshAlgorithmKind.HostKey, session, app));
    }

    [Fact]
    public void Apply_RemovesWhatIsTurnedOff_AndKeepsSshNetOrder()
    {
        var previous = SshAlgorithmPolicy.AppDefaults;
        try
        {
            SshAlgorithmPolicy.AppDefaults = new SshAlgorithms { DisabledMacs = ["hmac-sha1", "hmac-sha1-etm@openssh.com"] };
            var info = Connect(Host(new SshAlgorithms { DisabledCiphers = ["aes128-ctr", "3des-cbc"] }));

            var ciphers = SshAlgorithmPolicy.Supported(SshAlgorithmKind.Cipher).Where(c => c is not ("aes128-ctr" or "3des-cbc"));
            Assert.Equal(ciphers, info.Encryptions.Keys);
            Assert.DoesNotContain(info.HmacAlgorithms.Keys, name => name.StartsWith("hmac-sha1", StringComparison.Ordinal));
            Assert.Equal(SshAlgorithmPolicy.Supported(SshAlgorithmKind.HostKey), info.HostKeyAlgorithms.Keys);
        }
        finally
        {
            SshAlgorithmPolicy.AppDefaults = previous;
        }
    }

    [Fact]
    public void Apply_EverythingOff_FailsTheConnectAndSaysWhere()
    {
        var all = SshAlgorithmPolicy.Supported(SshAlgorithmKind.Cipher);
        var error = Assert.Throws<SshSessionException>(() => Connect(Host(new SshAlgorithms { DisabledCiphers = all })));
        Assert.Contains("cipher", error.Message);
        Assert.Contains("Advanced", error.Message);
    }

    [Fact]
    public void With_KeepsKnownNamesInOrder_AndNullWhenNothingIsSet()
    {
        var set = SshAlgorithmPolicy.With(null, SshAlgorithmKind.Cipher, ["3des-cbc", "not-a-cipher", "aes128-cbc"]);
        Assert.Equal(["aes128-cbc", "3des-cbc"], set!.DisabledCiphers);
        Assert.Null(SshAlgorithmPolicy.With(set, SshAlgorithmKind.Cipher, null));
    }

    [Fact]
    public void FromOpenSsh_TurnsListsIntoOffChoices()
    {
        var choices = SshAlgorithmPolicy.FromOpenSsh("diffie-hellman-group14-sha1", "-*-cbc", "+hmac-sha1", "arcfour")!;

        Assert.Equal(["diffie-hellman-group14-sha1"], SshAlgorithmPolicy.Offered(SshAlgorithmKind.KeyExchange, choices, null));
        Assert.DoesNotContain(SshAlgorithmPolicy.Offered(SshAlgorithmKind.Cipher, choices, null), name => name.EndsWith("-cbc", StringComparison.Ordinal));
        Assert.Empty(choices.DisabledMacs!);
        // Nothing SSH.NET has: left unset rather than turning every host key type off.
        Assert.Null(choices.DisabledHostKeys);
        Assert.Null(SshAlgorithmPolicy.FromOpenSsh(null, " ", null, null));
    }

    [Theory]
    [InlineData("diffie-hellman-group1-sha1", true)]
    [InlineData("aes256-cbc", true)]
    [InlineData("ssh-rsa", true)]
    [InlineData("rsa-sha2-512", false)]
    [InlineData("curve25519-sha256", false)]
    public void Legacy_MarksOldAlgorithms(string name, bool legacy) =>
        Assert.Equal(legacy, SshAlgorithmPolicy.IsLegacy(name));
}
