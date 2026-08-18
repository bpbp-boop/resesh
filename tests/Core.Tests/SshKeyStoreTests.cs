using Sessions.Core.Credentials;
using Sessions.Core.Models;
using Sessions.Core.Storage;

namespace Sessions.Core.Tests;

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
