using Resesh.Core.Models;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;

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
        var previous = SshAlgorithmPolicy.AppDefaultsSource;
        try
        {
            SshAlgorithmPolicy.AppDefaultsSource = () => new SshAlgorithms { DisabledMacs = ["hmac-sha1", "hmac-sha1-etm@openssh.com"] };
            var info = Connect(Host(new SshAlgorithms { DisabledCiphers = ["aes128-ctr", "3des-cbc"] }));

            var ciphers = SshAlgorithmPolicy.Supported(SshAlgorithmKind.Cipher).Where(c => c is not ("aes128-ctr" or "3des-cbc"));
            Assert.Equal(ciphers, info.Encryptions.Keys);
            Assert.DoesNotContain(info.HmacAlgorithms.Keys, name => name.StartsWith("hmac-sha1", StringComparison.Ordinal));
            Assert.Equal(SshAlgorithmPolicy.Supported(SshAlgorithmKind.HostKey), info.HostKeyAlgorithms.Keys);
        }
        finally
        {
            SshAlgorithmPolicy.AppDefaultsSource = previous;
        }
    }

    [Fact]
    public void Apply_ReadsTheSavedSettingsWhenEachConnectionStarts()
    {
        // A backup import saves settings without going through the settings page; the next
        // connection must still follow them, with no copy for the importer to refresh.
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "resesh-tests", Guid.NewGuid().ToString("N"), "settings.json"));
        var previous = SshAlgorithmPolicy.AppDefaultsSource;
        try
        {
            SshAlgorithmPolicy.AppDefaultsSource = () => settings.Current.SshAlgorithms;
            Assert.Contains("hmac-sha1", Connect(Host(null)).HmacAlgorithms.Keys);

            settings.Save(settings.Current with { SshAlgorithms = new SshAlgorithms { DisabledMacs = ["hmac-sha1"] } });

            Assert.DoesNotContain("hmac-sha1", Connect(Host(null)).HmacAlgorithms.Keys);
        }
        finally
        {
            SshAlgorithmPolicy.AppDefaultsSource = previous;
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
    // ---- OpenSSH options (SSHFS) ----

    private static IReadOnlyList<string> OpenSshOptions(
        Session session, string? trusted, Func<SshAlgorithmKind, IReadOnlyCollection<string>?> known)
    {
        var previous = SshAlgorithmPolicy.AppDefaultsSource;
        try
        {
            SshAlgorithmPolicy.AppDefaultsSource = null;
            return SshAlgorithmPolicy.OpenSshOptions(session, trusted, known);
        }
        finally
        {
            SshAlgorithmPolicy.AppDefaultsSource = previous;
        }
    }

    private static IReadOnlyCollection<string> Everything(SshAlgorithmKind kind) => SshAlgorithmPolicy.Supported(kind);

    private static string Option(IReadOnlyList<string> options, string name) =>
        Assert.Single(options, option => option.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];

    [Fact]
    public void OpenSshOptions_OfferWhatOurConnectionsOffer_InSshNetOrder()
    {
        var session = new Session { Name = "router", Host = "router", Algorithms = new SshAlgorithms { DisabledCiphers = ["aes128-ctr"] } };

        var options = OpenSshOptions(session, null, Everything);

        Assert.Equal(string.Join(',', SshAlgorithmPolicy.Offered(SshAlgorithmKind.Cipher, session.Algorithms, null)), Option(options, "Ciphers"));
        Assert.DoesNotContain("aes128-ctr", Option(options, "Ciphers").Split(','));
        // Legacy algorithms stay on by default, as for the terminal.
        Assert.Contains("ssh-rsa", Option(options, "HostKeyAlgorithms").Split(','));
        Assert.Equal(string.Join(',', SshAlgorithmPolicy.Supported(SshAlgorithmKind.KeyExchange)), Option(options, "KexAlgorithms"));
        Assert.Equal(string.Join(',', SshAlgorithmPolicy.Supported(SshAlgorithmKind.Mac)), Option(options, "MACs"));
    }

    [Fact]
    public void OpenSshOptions_DropNamesTheClientDoesNotKnow()
    {
        var session = new Session { Name = "web", Host = "web" };
        IReadOnlyCollection<string>? Known(SshAlgorithmKind kind) => kind == SshAlgorithmKind.KeyExchange
            ? ["curve25519-sha256", "diffie-hellman-group14-sha256", "not-in-ssh-net"]
            : Everything(kind);

        var kex = Option(OpenSshOptions(session, null, Known), "KexAlgorithms").Split(',');

        Assert.Equal(["curve25519-sha256", "diffie-hellman-group14-sha256"],
            SshAlgorithmPolicy.Supported(SshAlgorithmKind.KeyExchange).Intersect(kex));
        Assert.Equal(2, kex.Length);
    }

    [Fact]
    public void OpenSshOptions_WithoutTheClientsList_OnlyRemoveWhatIsOff()
    {
        var session = new Session { Name = "web", Host = "web", Algorithms = new SshAlgorithms { DisabledMacs = ["hmac-sha1"] } };

        var options = OpenSshOptions(session, null, _ => null);

        Assert.Equal(["MACs=-hmac-sha1"], options);
    }

    [Fact]
    public void OpenSshOptions_OfferOnlyTheTrustedHostKeyType()
    {
        var session = new Session { Name = "web", Host = "web" };

        var hostKeys = Option(OpenSshOptions(session, "ssh-rsa", Everything), "HostKeyAlgorithms").Split(',');

        Assert.NotEmpty(hostKeys);
        Assert.All(hostKeys, name => Assert.True(SshHostKeyTypes.SameKey(name, "ssh-rsa"), name));
        Assert.Contains("rsa-sha2-512", hostKeys);
    }

    [Fact]
    public void OpenSshOptions_FromAKeyTypeList_StillOfferRsaSha2()
    {
        // An OpenSSH without "ssh -Q key-sig" answers "ssh -Q key" with key types only.
        IReadOnlyCollection<string>? Known(SshAlgorithmKind kind) => kind == SshAlgorithmKind.HostKey
            ? SshHostKeyTypes.AlgorithmsForKeyTypes(["ssh-ed25519", "ssh-rsa", "ssh-rsa-cert-v01@openssh.com"])
            : Everything(kind);
        var session = new Session { Name = "web", Host = "web" };

        var hostKeys = Option(OpenSshOptions(session, "ssh-rsa", Known), "HostKeyAlgorithms").Split(',');
        Assert.Contains("rsa-sha2-512", hostKeys);
        Assert.Contains("rsa-sha2-256", hostKeys);

        var noSha1 = session with { Algorithms = new SshAlgorithms { DisabledHostKeys = ["ssh-rsa"] } };
        Assert.Equal(["rsa-sha2-256", "rsa-sha2-512"],
            Option(OpenSshOptions(noSha1, "ssh-rsa", Known), "HostKeyAlgorithms").Split(',').Order(StringComparer.Ordinal));

        Assert.Contains("rsa-sha2-512-cert-v01@openssh.com",
            SshHostKeyTypes.AlgorithmsForKeyTypes(["ssh-rsa-cert-v01@openssh.com"]));
        Assert.Equal(["ssh-ed25519"], SshHostKeyTypes.AlgorithmsForKeyTypes(["ssh-ed25519"]));
    }

    [Fact]
    public void OpenSshOptions_RefuseWhenTheTrustedTypeIsTurnedOff()
    {
        var off = SshAlgorithmPolicy.Supported(SshAlgorithmKind.HostKey).Where(name => SshHostKeyTypes.SameKey(name, "ssh-ed25519")).ToList();
        var session = new Session { Name = "web", Host = "web", Algorithms = new SshAlgorithms { DisabledHostKeys = off } };

        var failure = Assert.Throws<SshSessionException>(() => OpenSshOptions(session, "ssh-ed25519", Everything));
        Assert.Equal(SshFailureKind.HostKeyRejected, failure.Kind);
    }

    [Fact]
    public void OpenSshOptions_RefuseWhenTheClientKnowsNoneLeftOn()
    {
        var session = new Session { Name = "web", Host = "web" };

        Assert.Throws<SshSessionException>(() => OpenSshOptions(
            session, null, kind => kind == SshAlgorithmKind.Cipher ? ["arcfour"] : Everything(kind)));
    }
}
