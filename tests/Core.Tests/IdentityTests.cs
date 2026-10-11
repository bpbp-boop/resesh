using System.Text.Json;
using Resesh.Core.Identities;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class IdentityTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("identity-test");

    private string PathOf(string name) => Path.Combine(_dir.FullName, name);

    private (SessionStore Sessions, IdentityStore Identities, LoginResolver Resolver) NewStores()
    {
        var sessions = new SessionStore(PathOf("sessions.json"));
        sessions.Load();
        var identities = new IdentityStore(PathOf("identities.json"));
        identities.Load();
        return (sessions, identities, new LoginResolver(sessions, identities));
    }

    [Fact]
    public void LegacySessionJson_IsOwnLogin_AndNewFieldsAreOmittedWhenUnused()
    {
        var session = JsonSerializer.Deserialize<Session>("""{"name":"old","host":"h","username":"alice"}""")!;
        Assert.Equal(SessionLogin.Own, session.Login);

        var (sessions, _, _) = NewStores();
        sessions.Add(new Session { Name = "own", Host = "h", Username = "bob" });
        var json = File.ReadAllText(PathOf("sessions.json"));
        Assert.DoesNotContain("identityId", json);
        Assert.DoesNotContain("inheritLogin", json);
        Assert.DoesNotContain("folderSettings\": [", json);
    }

    [Fact]
    public void IdentityAndFolderSettings_RoundTripThroughStores()
    {
        var (sessions, identities, _) = NewStores();
        var tacacs = identities.Add(new Identity { Name = "TACACS", Username = "ops", Proof = IdentityProof.Ask });
        sessions.SetFolderIdentity("Branch/", tacacs.Id);
        sessions.Add(new Session { Name = "r1", Host = "10.0.0.1", FolderPath = "Branch", InheritLogin = true });

        var (reloadedSessions, reloadedIdentities, resolver) = NewStores();
        var identity = Assert.Single(reloadedIdentities.Identities);
        Assert.Equal(IdentityProof.Ask, identity.Proof);
        Assert.Equal(tacacs.Id, reloadedSessions.FolderIdentity("branch"));
        var login = resolver.Resolve(Assert.Single(reloadedSessions.Sessions));
        Assert.Equal(LoginSource.Folder, login.Source);
        Assert.Equal("ops", login.Username);
        Assert.Equal("Branch", login.FolderPath);
    }

    [Fact]
    public void Resolve_UsesNearestFolderThenAncestorsThenRoot()
    {
        var (sessions, identities, resolver) = NewStores();
        var root = identities.Add(new Identity { Name = "root", Username = "admin" });
        var dc = identities.Add(new Identity { Name = "dc", Username = "dcops" });
        var rack = identities.Add(new Identity { Name = "rack", Username = "rackops" });
        sessions.SetFolderIdentity("", root.Id);
        sessions.SetFolderIdentity("DC", dc.Id);
        sessions.SetFolderIdentity("DC/Rack 4", rack.Id);

        Session At(string folder) => new() { Host = "h", FolderPath = folder, InheritLogin = true };

        Assert.Equal(rack.Id, resolver.Resolve(At("DC/Rack 4")).Identity!.Id);
        Assert.Equal(dc.Id, resolver.Resolve(At("DC/Rack 5/Top")).Identity!.Id);
        Assert.Equal(root.Id, resolver.Resolve(At("Lab")).Identity!.Id);
        Assert.Equal(root.Id, resolver.Resolve(At("")).Identity!.Id);
        Assert.Equal("DC", resolver.Resolve(At("DC/Rack 5")).FolderPath);
    }

    [Fact]
    public void Resolve_OwnIgnoresFolders_ExplicitIdentityWins_MissingFolderIdentityPrompts()
    {
        var (sessions, identities, resolver) = NewStores();
        var folder = identities.Add(new Identity { Name = "folder", Username = "f" });
        var chosen = identities.Add(new Identity { Name = "chosen", Username = "c" });
        sessions.SetFolderIdentity("Branch", folder.Id);

        var own = resolver.Resolve(new Session { FolderPath = "Branch", Username = "me", AuthMethod = AuthMethod.None });
        Assert.Equal(LoginSource.Session, own.Source);
        Assert.Equal("me", own.Username);
        Assert.Equal(AuthMethod.None, own.AuthMethod);
        Assert.Null(own.CredentialId);

        var explicitLogin = resolver.Resolve(new Session { FolderPath = "Branch", IdentityId = chosen.Id });
        Assert.Equal(LoginSource.SessionIdentity, explicitLogin.Source);
        Assert.Equal(chosen.Id, explicitLogin.CredentialId);

        var nothing = resolver.Resolve(new Session { FolderPath = "Elsewhere", InheritLogin = true });
        Assert.Equal(LoginSource.None, nothing.Source);
        Assert.False(nothing.IdentityMissing);

        var gone = resolver.Resolve(new Session { FolderPath = "Branch", IdentityId = Guid.NewGuid() });
        Assert.Equal(LoginSource.None, gone.Source);
        Assert.True(gone.IdentityMissing);
    }

    [Fact]
    public void Resolve_UsernameOverrideKeepsTheIdentityProof()
    {
        var (sessions, identities, resolver) = NewStores();
        var keyId = Guid.NewGuid();
        var cloud = identities.Add(new Identity { Name = "cloud", Username = "ubuntu", Proof = IdentityProof.Key, KeyId = keyId });
        sessions.SetFolderIdentity("Cloud", cloud.Id);

        var login = resolver.Resolve(new Session { FolderPath = "Cloud", InheritLogin = true, Username = " ec2-user " });
        Assert.Equal("ec2-user", login.Username);
        Assert.True(login.UsernameOverridden);
        Assert.Equal(AuthMethod.PrivateKey, login.AuthMethod);
        Assert.Equal(keyId, login.KeyId);
        Assert.Null(login.CredentialId);

        var applied = login.ApplyTo(new Session { Host = "h", InheritLogin = true, Username = "ec2-user" });
        Assert.Equal("ec2-user", applied.Username);
        Assert.Equal(AuthMethod.PrivateKey, applied.AuthMethod);
        Assert.Equal(keyId, applied.PrivateKeyId);
    }

    [Fact]
    public void RenameFolder_CarriesSettingsAndDestinationKeepsItsOwn()
    {
        var (sessions, identities, _) = NewStores();
        var a = identities.Add(new Identity { Name = "a", Username = "a" });
        var b = identities.Add(new Identity { Name = "b", Username = "b" });
        var c = identities.Add(new Identity { Name = "c", Username = "c" });
        sessions.SetFolderIdentity("Old", a.Id);
        sessions.SetFolderIdentity("Old/Sub", b.Id);
        sessions.SetFolderIdentity("Target", c.Id);
        sessions.CreateFolder("Old/Sub");
        sessions.CreateFolder("Target");

        sessions.RenameFolder("Old", "New");
        Assert.Null(sessions.FolderIdentity("Old"));
        Assert.Equal(a.Id, sessions.FolderIdentity("New"));
        Assert.Equal(b.Id, sessions.FolderIdentity("New/Sub"));

        sessions.RenameFolder("New", "Target");
        Assert.Equal(c.Id, sessions.FolderIdentity("Target"));
        Assert.Equal(b.Id, sessions.FolderIdentity("Target/Sub"));
        Assert.Equal(2, sessions.FolderSettings.Count);
    }

    [Fact]
    public void LocalRename_DoesNotTouchRemoteFolderSettings()
    {
        var (sessions, identities, _) = NewStores();
        var a = identities.Add(new Identity { Name = "a", Username = "a" });
        sessions.SetFolderIdentity("Shared", a.Id);
        sessions.CreateFolder("Shared", SessionKind.Local);

        sessions.RenameFolder("Shared", "Moved", SessionKind.Local);

        Assert.Equal(a.Id, sessions.FolderIdentity("Shared"));
        Assert.Null(sessions.FolderIdentity("Moved"));
    }

    [Fact]
    public void DeleteFolder_DropsSettingsBelowIt()
    {
        var (sessions, identities, _) = NewStores();
        var a = identities.Add(new Identity { Name = "a", Username = "a" });
        sessions.SetFolderIdentity("", a.Id);
        sessions.SetFolderIdentity("Gone", a.Id);
        sessions.SetFolderIdentity("Gone/Sub", a.Id);
        sessions.SetFolderIdentity("Gonna", a.Id);

        sessions.DeleteFolder("Gone");

        Assert.Equal(["", "Gonna"], sessions.FolderSettings.Select(f => f.Path).Order().ToArray());
    }

    [Fact]
    public void DetachIdentity_ClearsFoldersAndReturnsChosenSessionsToInherit()
    {
        var (sessions, identities, resolver) = NewStores();
        var a = identities.Add(new Identity { Name = "a", Username = "a" });
        sessions.SetFolderIdentity("Branch", a.Id);
        var chooser = new Session { Host = "h", IdentityId = a.Id };
        var own = new Session { Host = "h", Username = "me" };
        sessions.Add(chooser);
        sessions.Add(own);

        Assert.Equal(2, sessions.DetachIdentity(a.Id));

        Assert.Empty(sessions.FolderSettings);
        Assert.Equal(SessionLogin.Inherit, sessions.Find(chooser.Id)!.Login);
        Assert.Equal(SessionLogin.Own, sessions.Find(own.Id)!.Login);
    }

    [Fact]
    public void UsersOf_CountsResolvedRemoteSessionsOnly()
    {
        var (sessions, identities, resolver) = NewStores();
        var a = identities.Add(new Identity { Name = "a", Username = "a" });
        sessions.SetFolderIdentity("Branch", a.Id);
        sessions.Add(new Session { Host = "1", FolderPath = "Branch", InheritLogin = true });
        sessions.Add(new Session { Host = "2", FolderPath = "Branch/Sub", InheritLogin = true });
        sessions.Add(new Session { Host = "3", FolderPath = "Branch", Username = "own" });
        sessions.Add(new Session { Host = "4", IdentityId = a.Id });
        sessions.Add(new Session { Host = "5", Kind = SessionKind.Telnet, FolderPath = "Branch", InheritLogin = true });

        Assert.Equal(["1", "2", "4"], resolver.UsersOf(a.Id, sessions.Sessions).Select(s => s.Host).Order().ToArray());
    }

    [Fact]
    public void IdentityStore_UniqueNamesAndKeyOnlyForKeyProof()
    {
        var (_, identities, _) = NewStores();
        var first = identities.Add(new Identity { Name = "ops", Username = "ops" });
        var second = identities.Add(new Identity { Name = "OPS", Username = "ops", KeyId = Guid.NewGuid() });
        var unnamed = identities.Add(new Identity { Username = " admin " });

        Assert.Equal("ops", first.Name);
        Assert.Equal("OPS (2)", second.Name);
        Assert.Null(second.KeyId);
        Assert.Equal("admin", unnamed.Name);
        Assert.Equal("admin", unnamed.Username);
    }

    [Fact]
    public void IdentityStore_MergeImportKeepsExistingIdsAndRemapsKeys()
    {
        var (_, identities, _) = NewStores();
        var existing = identities.Add(new Identity { Name = "ops", Username = "ops" });
        var oldKey = Guid.NewGuid();
        var newKey = Guid.NewGuid();
        var incoming = new Identity { Name = "ops", Username = "other", Proof = IdentityProof.Key, KeyId = oldKey };

        var map = identities.MergeImport([existing with { Username = "changed" }, incoming], id => id == oldKey ? newKey : null);

        Assert.Equal(existing.Id, map[existing.Id]);
        Assert.Equal("ops", identities.Find(existing.Id)!.Username);
        var added = identities.Find(map[incoming.Id])!;
        Assert.Equal("ops (2)", added.Name);
        Assert.Equal(newKey, added.KeyId);
    }

    [Fact]
    public void AskedSecretCache_HoldsUntilForgotten()
    {
        var cache = new AskedSecretCache();
        var id = Guid.NewGuid();
        Assert.Null(cache.Get(id));
        cache.Set(id, "pw");
        Assert.Equal("pw", cache.Get(id));
        cache.Forget(id);
        Assert.Null(cache.Get(id));
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
