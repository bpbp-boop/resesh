using System.Security.Cryptography;
using Resesh.Core.Import;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class ImportedSshKeyTests
{
    [Fact]
    public void Commit_RegistersSharedKeyAndPersistsSessionLinks()
    {
        var dir = Directory.CreateTempSubdirectory("import-key-test");
        try
        {
            using var rsa = RSA.Create(2048);
            var path = Path.Combine(dir.FullName, "identity");
            File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
            var store = new SessionStore(Path.Combine(dir.FullName, "sessions.json"));
            var keys = new SshKeyStore(Path.Combine(dir.FullName, "keys.json"));
            var candidates = new[]
            {
                new ImportCandidate { Name = "one", Host = "one", PrivateKeyPath = path },
                new ImportCandidate { Name = "two", Host = "two", PrivateKeyPath = path },
            };
            Assert.Equal((2, 0), SecureCrtImporter.Commit(store, candidates, keys));
            store.Load();
            keys.Load();
            var key = Assert.Single(keys.Keys);
            Assert.False(key.IsEncrypted);
            Assert.NotNull(key.Fingerprint);
            Assert.All(store.Sessions, session =>
            {
                Assert.Equal(AuthMethod.PrivateKey, session.AuthMethod);
                Assert.Equal(key.Id, session.PrivateKeyId);
                Assert.Null(session.PrivateKeyPath);
                Assert.False(session.CredentialNeeded);
            });
            Assert.Equal((0, 2), SecureCrtImporter.Commit(store, candidates, keys));
            Assert.Single(keys.Keys);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Commit_EncryptedKeyRegistersWithoutPassphrase()
    {
        var dir = Directory.CreateTempSubdirectory("import-encrypted-key-test");
        try
        {
            using var rsa = RSA.Create(2048);
            var path = Path.Combine(dir.FullName, "encrypted.pem");
            File.WriteAllText(path, rsa.ExportEncryptedPkcs8PrivateKeyPem("test-secret",
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)));
            var store = new SessionStore(Path.Combine(dir.FullName, "sessions.json"));
            var keys = new SshKeyStore(Path.Combine(dir.FullName, "keys.json"));
            SecureCrtImporter.Commit(store, [new ImportCandidate { Name = "encrypted", PrivateKeyPath = path }], keys);
            var key = Assert.Single(keys.Keys);
            Assert.True(key.IsEncrypted);
            Assert.Throws<Resesh.Core.Ssh.SshKeyPassphraseException>(() => keys.Validate(key.Id, "wrong"));
            Assert.NotNull(keys.Validate(key.Id, "test-secret").Fingerprint);
            Assert.Equal(AuthMethod.PrivateKey, Assert.Single(store.Sessions).AuthMethod);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Commit_MissingKeyKeepsKeyAuthentication()
    {
        var dir = Directory.CreateTempSubdirectory("import-missing-key-test");
        try
        {
            var store = new SessionStore(Path.Combine(dir.FullName, "sessions.json"));
            var keys = new SshKeyStore(Path.Combine(dir.FullName, "keys.json"));
            SecureCrtImporter.Commit(store,
                [new ImportCandidate { Name = "missing", PrivateKeyPath = Path.Combine(dir.FullName, "missing") }], keys);
            Assert.False(Assert.Single(keys.Keys).IsAvailable);
            Assert.Equal(AuthMethod.PrivateKey, Assert.Single(store.Sessions).AuthMethod);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Putty_ReadsPrivateKeyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "key with spaces.ppk");
        var candidate = PuttyRegistryImporter.ParseSession("test",
            new Dictionary<string, object?> { ["PublicKeyFile"] = path });
        Assert.Equal(path, candidate.PrivateKeyPath);
    }

    [Theory]
    [InlineData("Identity Filename")]
    [InlineData("Identity Filename V2")]
    public void SecureCrt_ReadsIdentity(string field)
    {
        var candidate = SecureCrtImporter.Parse($"S:\"{field}\"=test-key", "test", "", "test.ini");
        Assert.Equal("test-key", candidate.PrivateKeyPath);
    }

    [Fact]
    public void SecureCrt_UsesPersonalGlobalIdentityAndResolvesConfigVariables()
    {
        var dir = Directory.CreateTempSubdirectory("import-global-key-test");
        try
        {
            var config = Directory.CreateDirectory(Path.Combine(dir.FullName, "Config"));
            var personal = Directory.CreateDirectory(Path.Combine(dir.FullName, "Personal"));
            Directory.CreateDirectory(Path.Combine(config.FullName, "Sessions"));
            Directory.CreateDirectory(Path.Combine(personal.FullName, "Sessions"));
            File.WriteAllText(Path.Combine(config.FullName, "Sessions", "test.ini"),
                "S:\"Protocol Name\"=SSH2\nD:\"Use Global Public Key\"=00000001\nS:\"Identity Filename V2\"=ignored");
            File.WriteAllText(Path.Combine(personal.FullName, "Global.ini"),
                "S:\"Identity Filename V2\"=${VDS_USER_DATA_PATH}/identity.pub");
            var candidate = Assert.Single(SecureCrtImporter.ScanDefault(
                new ConfigSource(config.FullName, personal.FullName)).Importable);
            Assert.Equal(Path.Combine(personal.FullName, "identity"), candidate.PrivateKeyPath);
        }
        finally { dir.Delete(true); }
    }

    private sealed class ConfigSource(string config, string personal) : ISecureCrtConfigSource
    {
        public SecureCrtConfigSettings GetSettings() => new(config, true, personal);
    }

    [Fact]
    public void OpenSsh_InheritsMatchingIdentityAndExpandsTokens()
    {
        var scan = OpenSshConfigImporter.Parse("Host prod\n HostName server\n User ops\nHost * !blocked\n IdentityFile ~/.ssh/%h-%r\nHost blocked\n");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "server-ops"),
            scan.Importable.Single(c => c.Name == "prod").PrivateKeyPath);
        Assert.Null(scan.Importable.Single(c => c.Name == "blocked").PrivateKeyPath);
    }

    [Fact]
    public void OpenSsh_FirstIdentityWinsAndMatchDoesNotLeak()
    {
        var scan = OpenSshConfigImporter.Parse("Host prod\n IdentityFile \"~/.ssh/key one\"\n IdentityFile ~/.ssh/key-two\nMatch user other\n IdentityFile ~/.ssh/wrong\nHost next\n");
        Assert.EndsWith("key one", scan.Importable.Single(c => c.Name == "prod").PrivateKeyPath);
        Assert.Null(scan.Importable.Single(c => c.Name == "next").PrivateKeyPath);
    }
}
