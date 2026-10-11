using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class IdentityStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("identity-store-test");
    private string StorePath => Path.Combine(_dir.FullName, "identities.json");

    [Fact]
    public void Mutations_NormalizeNamesAndPublishBeforeChanged()
    {
        var store = new IdentityStore(StorePath);
        store.Load();
        var changes = 0;
        store.Changed += () =>
        {
            changes++;
            var reloaded = new IdentityStore(StorePath);
            reloaded.Load();
            Assert.Equal(store.Identities.ToArray(), reloaded.Identities.ToArray());
        };

        var first = store.Add(new Identity { Name = " Login ", Username = " alice ", KeyId = Guid.NewGuid() });
        Assert.Equal("Login", first.Name);
        Assert.Equal("alice", first.Username);
        Assert.Null(first.KeyId);
        var second = store.Add(new Identity { Name = "login", Username = "bob" });
        Assert.Equal("login (2)", second.Name);
        var updated = store.Update(second with { Name = "Login", Notes = "updated" });
        Assert.Equal("Login (2)", updated.Name);
        Assert.Equal(updated, store.Find(second.Id));
        Assert.True(store.Remove(first.Id));
        Assert.False(store.Remove(first.Id));
        Assert.Equal(4, changes);
    }

    [Fact]
    public void MergeImport_DeduplicatesAndMapsKeysWithinCandidate()
    {
        var store = new IdentityStore(StorePath);
        store.Load();
        var existing = store.Add(new Identity { Name = "Login", Username = "alice" });
        var keyId = Guid.NewGuid();
        var mappedKeyId = Guid.NewGuid();
        var imported = new Identity { Name = "Login", Username = " bob ", Proof = IdentityProof.Key, KeyId = keyId };
        var second = new Identity { Name = "Login", Username = "carol" };
        var changes = 0;
        store.Changed += () => changes++;

        var map = store.MergeImport([existing with { Username = "ignored" }, imported, imported, second],
            id => id == keyId ? mappedKeyId : null);

        Assert.Equal(existing.Id, map[existing.Id]);
        Assert.Equal(imported.Id, map[imported.Id]);
        Assert.Equal(existing, store.Find(existing.Id));
        Assert.Equal("Login (2)", store.Find(imported.Id)!.Name);
        Assert.Equal("bob", store.Find(imported.Id)!.Username);
        Assert.Equal(mappedKeyId, store.Find(imported.Id)!.KeyId);
        Assert.Equal("Login (3)", store.Find(second.Id)!.Name);
        Assert.Equal(1, changes);
        store.MergeImport([existing, imported]);
        Assert.Equal(1, changes);
        var reloaded = new IdentityStore(StorePath);
        reloaded.Load();
        Assert.Equal(store.Identities.ToArray(), reloaded.Identities.ToArray());
    }

    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("remove")]
    [InlineData("merge")]
    public void FailedSave_DoesNotPublishOrLeakIntoNextSave(string mutation)
    {
        var store = new IdentityStore(StorePath);
        store.Load();
        var original = store.Add(new Identity { Name = "Original", Username = "alice" });
        var rejected = new Identity { Name = "New", Username = "rejected" };
        var snapshot = store.Identities.ToArray();
        var saved = File.ReadAllText(StorePath);
        var changes = 0;
        store.Changed += () => changes++;
        Directory.CreateDirectory(StorePath + ".tmp");

        var failure = Record.Exception(() =>
        {
            switch (mutation)
            {
                case "add": store.Add(rejected); break;
                case "update": store.Update(original with { Name = "Changed", Username = "rejected" }); break;
                case "remove": store.Remove(original.Id); break;
                case "merge": store.MergeImport([rejected, new Identity { Name = "New", Username = "also rejected" }]); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });

        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal(snapshot, store.Identities.ToArray());
        Assert.Equal(original, store.Find(original.Id));
        Assert.Null(store.Find(rejected.Id));
        Assert.Equal(saved, File.ReadAllText(StorePath));
        Assert.Equal(0, changes);

        Directory.Delete(StorePath + ".tmp");
        var intended = store.Add(new Identity { Name = "New", Username = "intended" });
        Assert.Equal("New", intended.Name);
        Assert.Equal(1, changes);
        var reloaded = new IdentityStore(StorePath);
        reloaded.Load();
        Assert.Equal(new[] { intended, original }, reloaded.Identities.ToArray());
        Assert.Null(reloaded.Find(rejected.Id));
    }

    [Fact]
    public void FailedSave_AfterRecoveryPreservesBackupUntilSuccessfulSave()
    {
        var originalStore = new IdentityStore(StorePath);
        originalStore.Load();
        var original = originalStore.Add(new Identity { Name = "Original", Username = "alice" });
        originalStore.Add(new Identity { Name = "Discarded", Username = "bob" });
        var backup = File.ReadAllText(StorePath + ".bak");
        File.WriteAllText(StorePath, "{ unreadable");
        var store = new IdentityStore(StorePath);
        store.Load();
        Assert.NotNull(store.LoadWarning);
        Directory.CreateDirectory(StorePath + ".tmp");

        var failure = Record.Exception(() => store.Add(new Identity { Name = "Rejected", Username = "rejected" }));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal(original, Assert.Single(store.Identities));
        Assert.Equal(backup, File.ReadAllText(StorePath + ".bak"));

        Directory.Delete(StorePath + ".tmp");
        store.Add(new Identity { Name = "Intended", Username = "intended" });
        Assert.Equal(backup, File.ReadAllText(StorePath + ".bak"));
        var firstSuccessfulSave = File.ReadAllText(StorePath);
        store.Update(original with { Notes = "next successful save" });
        Assert.Equal(firstSuccessfulSave, File.ReadAllText(StorePath + ".bak"));
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
