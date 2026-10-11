using System.Security.Cryptography;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class SshKeyStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("ssh-key-store-test");

    [Fact]
    public void MergeImport_RoundTripsMetadataAndDeduplicatesFingerprint()
    {
        var path = Path.Combine(_dir.FullName, "ssh-keys.json");
        var store = new SshKeyStore(path);
        store.Load();
        var first = new SshKeyReference
        {
            Name = "Operations",
            Path = Path.Combine(_dir.FullName, "id_ed25519"),
            Algorithm = "ssh-ed25519",
            Fingerprint = "SHA256:test",
            IsEncrypted = true,
        };
        var duplicate = first with { Id = Guid.NewGuid(), Name = "Duplicate", Path = Path.Combine(_dir.FullName, "other") };

        var map = store.MergeImport([first, duplicate]);

        Assert.Equal(first.Id, map[first.Id]);
        Assert.Equal(first.Id, map[duplicate.Id]);
        Assert.Single(store.Keys);

        var reloaded = new SshKeyStore(path);
        reloaded.Load();
        var key = Assert.Single(reloaded.Keys);
        Assert.Equal("Operations", key.Name);
        Assert.Equal("SHA256:test", key.Fingerprint);
    }

    [Fact]
    public void MigrateLegacySessions_RegistersExternalPathAndCopiesPassphrase()
    {
        var sessions = new SessionStore(Path.Combine(_dir.FullName, "sessions.json"));
        sessions.Load();
        var legacy = new Session
        {
            Name = "Legacy",
            Host = "host.example",
            Username = "alice",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = Path.Combine(_dir.FullName, "missing-key"),
            PassphraseRequired = true,
        };
        sessions.Add(legacy);
        var credentials = new FakeCredentials();
        credentials.Write(legacy.Id, "key passphrase");
        var keys = new SshKeyStore(Path.Combine(_dir.FullName, "ssh-keys.json"));
        keys.Load();

        Assert.Equal(1, keys.MigrateLegacySessions(sessions, credentials));

        var migrated = sessions.Find(legacy.Id)!;
        Assert.NotNull(migrated.PrivateKeyId);
        Assert.Null(migrated.PrivateKeyPath);
        Assert.False(migrated.PassphraseRequired);
        var key = Assert.Single(keys.Keys);
        Assert.Equal(Path.GetFullPath(legacy.PrivateKeyPath!), key.Path);
        Assert.Equal("key passphrase", credentials.ReadKey(key.Id));
        Assert.False(key.IsAvailable);
    }

    [Theory]
    [InlineData("register")]
    [InlineData("rename")]
    [InlineData("relocate")]
    [InlineData("validate")]
    [InlineData("remove")]
    [InlineData("merge")]
    public void FailedSave_DoesNotPublishOrLeakIntoNextSave(string mutation)
    {
        var path = Path.Combine(_dir.FullName, "ssh-keys.json");
        var keyPath = Path.Combine(_dir.FullName, "original-key");
        using var rsa = RSA.Create(2048);
        File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());
        var relocatedPath = Path.Combine(_dir.FullName, "relocated-key");
        File.Copy(keyPath, relocatedPath);
        var store = new SshKeyStore(path);
        store.Load();
        SshKeyReference original;
        if (mutation == "validate")
        {
            original = new SshKeyReference { Name = "Original", Path = keyPath };
            store.MergeImport([original]);
        }
        else
            original = store.RegisterExternal(keyPath, "Original");
        var newPath = Path.Combine(_dir.FullName, "new-key");
        var rejected = new SshKeyReference { Name = "New", Path = newPath, Fingerprint = "SHA256:rejected" };
        var snapshot = store.Keys.ToArray();
        var saved = File.ReadAllText(path);
        Directory.CreateDirectory(path + ".tmp");

        var failure = Record.Exception(() =>
        {
            switch (mutation)
            {
                case "register": store.RegisterExternal(newPath, "New", allowMissing: true); break;
                case "rename": store.Rename(original.Id, "Changed"); break;
                case "relocate": store.Relocate(original.Id, relocatedPath); break;
                case "validate": store.Validate(original.Id, null); break;
                case "remove": store.Remove(original.Id); break;
                case "merge":
                    store.MergeImport([rejected, new SshKeyReference
                    {
                        Name = "New",
                        Path = Path.Combine(_dir.FullName, "another-key"),
                        Fingerprint = "SHA256:also-rejected",
                    }]);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });

        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal(snapshot, store.Keys.ToArray());
        Assert.Equal(original, store.Find(original.Id));
        Assert.Null(store.Find(rejected.Id));
        Assert.Equal(saved, File.ReadAllText(path));

        Directory.Delete(path + ".tmp");
        var intended = store.RegisterExternal(newPath, "New", allowMissing: true);
        Assert.Equal("New", intended.Name);
        Assert.Null(intended.Fingerprint);
        var reloaded = new SshKeyStore(path);
        reloaded.Load();
        Assert.Equal(new[] { intended, original }, reloaded.Keys.ToArray());
        Assert.Null(reloaded.Find(rejected.Id));
    }

    [Fact]
    public void FailedSave_AfterRecoveryPreservesBackupUntilSuccessfulSave()
    {
        var path = Path.Combine(_dir.FullName, "ssh-keys.json");
        var originalStore = new SshKeyStore(path);
        originalStore.Load();
        var original = originalStore.RegisterExternal(Path.Combine(_dir.FullName, "original-key"), "Original", allowMissing: true);
        originalStore.RegisterExternal(Path.Combine(_dir.FullName, "discarded-key"), "Discarded", allowMissing: true);
        var backup = File.ReadAllText(path + ".bak");
        File.WriteAllText(path, "{ unreadable");
        var store = new SshKeyStore(path);
        store.Load();
        Assert.NotNull(store.LoadWarning);
        Directory.CreateDirectory(path + ".tmp");

        var failure = Record.Exception(() => store.Rename(original.Id, "Rejected"));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal(original, Assert.Single(store.Keys));
        Assert.Equal(backup, File.ReadAllText(path + ".bak"));

        Directory.Delete(path + ".tmp");
        store.Rename(original.Id, "Intended");
        Assert.Equal(backup, File.ReadAllText(path + ".bak"));
        var firstSuccessfulSave = File.ReadAllText(path);
        store.Rename(original.Id, "Next successful save");
        Assert.Equal(firstSuccessfulSave, File.ReadAllText(path + ".bak"));
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private sealed class FakeCredentials : ICredentialService
    {
        private readonly Dictionary<Guid, string> _sessions = [];
        private readonly Dictionary<Guid, string> _keys = [];
        public string? Read(Guid sessionId) => _sessions.GetValueOrDefault(sessionId);
        public void Write(Guid sessionId, string secret) => _sessions[sessionId] = secret;
        public void Delete(Guid sessionId) => _sessions.Remove(sessionId);
        public string? ReadKey(Guid keyId) => _keys.GetValueOrDefault(keyId);
        public void WriteKey(Guid keyId, string secret) => _keys[keyId] = secret;
        public void DeleteKey(Guid keyId) => _keys.Remove(keyId);
    }
}
