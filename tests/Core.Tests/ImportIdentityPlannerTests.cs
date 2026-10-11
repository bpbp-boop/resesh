using Resesh.Core.Identities;
using Resesh.Core.Import;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class ImportIdentityPlannerTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("import-identity-test");

    private static ImportCandidate Ssh(string name, string folder, string user, string? key = null) => new()
    {
        Name = name,
        FolderPath = folder,
        Host = name + ".example",
        Username = user,
        Protocol = "SSH2",
        PrivateKeyPath = key,
    };

    private static ImportIdentityPlan Plan(IReadOnlyList<ImportCandidate> candidates,
        IReadOnlyList<Identity>? existing = null, Func<string, Guid?>? folderIdentity = null,
        IReadOnlySet<string>? declined = null) =>
        ImportIdentityPlanner.Plan(candidates, existing ?? [], folderIdentity ?? (_ => null), _ => null, declined);

    [Fact]
    public void RepeatedUsername_BecomesOneIdentityAttachedWhereItDominates()
    {
        var a = Ssh("a", "DC/Rack1", "netops");
        var b = Ssh("b", "DC/Rack1", "netops");
        var c = Ssh("c", "DC/Rack2", "netops");
        var local = Ssh("d", "DC/Rack2", "localadmin");
        var lone = Ssh("e", "Lab", "boden");

        var plan = Plan([a, b, c, local, lone]);

        var suggestion = Assert.Single(plan.Suggestions);
        Assert.Equal("netops", suggestion.Name);
        Assert.Equal(3, suggestion.SessionCount);
        Assert.Equal(["DC"], suggestion.Folders);
        Assert.Equal(ImportLoginKind.Inherit, plan.Choices[a].Kind);
        Assert.Equal(ImportLoginKind.Inherit, plan.Choices[c].Kind);
        Assert.Equal(ImportLoginKind.Own, plan.Choices[local].Kind);
        Assert.Equal(ImportLoginKind.Own, plan.Choices[lone].Kind);
    }

    [Fact]
    public void MinorityLoginInAFolderIsChosenExplicitly_AndSubfolderCanAttachItsOwn()
    {
        var plan = Plan([
            Ssh("a", "Branch", "netops"), Ssh("b", "Branch", "netops"), Ssh("c", "Branch", "netops"),
            Ssh("d", "Branch", "netops"), Ssh("x", "Branch", "admin"),
            Ssh("y", "Branch/Lab", "admin"), Ssh("z", "Branch/Lab", "admin"),
        ]);

        var admin = plan.Suggestions.Single(s => s.Login.Username == "admin");
        Assert.Equal(["Branch/Lab"], admin.Folders);
        Assert.Equal(3, admin.SessionCount);
        var x = plan.Choices.Single(pair => pair.Key.Name == "x").Value;
        Assert.Equal(ImportLoginKind.Identity, x.Kind);
        Assert.Contains(("Branch", plan.Suggestions.Single(s => s.Login.Username == "netops").Login), plan.Attachments);
    }

    [Fact]
    public void SameUserWithDifferentKeys_AreSeparateIdentitiesNamedByKey()
    {
        var plan = Plan([
            Ssh("a", "Cloud", "ubuntu", @"C:\keys\prod.pem"), Ssh("b", "Cloud", "ubuntu", @"C:\keys\prod.pem"),
            Ssh("c", "Test", "ubuntu", @"C:\keys\test.pem"), Ssh("d", "Test", "ubuntu", @"C:\keys\test.pem"),
        ]);

        Assert.Equal(["ubuntu", "ubuntu (test.pem)"], plan.Suggestions.Select(s => s.Name).Order().ToArray());
        Assert.All(plan.Suggestions, s => Assert.True(s.Login.UsesKey));
    }

    [Fact]
    public void ExistingIdentityIsReused_EvenForOneSession_AndRootIsNeverAttached()
    {
        var existing = new Identity { Name = "TACACS", Username = "netops" };
        var only = Ssh("a", "", "netops");
        var second = Ssh("b", "", "netops");

        var plan = Plan([only, second], [existing]);

        var suggestion = Assert.Single(plan.Suggestions);
        Assert.Same(existing, suggestion.Existing);
        Assert.Equal("TACACS", suggestion.Name);
        Assert.Empty(plan.Attachments);
        Assert.Equal(ImportLoginKind.Identity, plan.Choices[only].Kind);

        var single = Plan([Ssh("c", "Lab", "netops")], [existing]);
        Assert.Same(existing, Assert.Single(single.Suggestions).Existing);
    }

    [Fact]
    public void FolderWithItsOwnIdentityIsRespected()
    {
        var existing = new Identity { Name = "Lab admin", Username = "labadmin" };
        var matching = Ssh("a", "Lab", "labadmin");
        var other1 = Ssh("b", "Lab", "netops");
        var other2 = Ssh("c", "Lab", "netops");

        var plan = Plan([matching, other1, other2], [existing], path => path == "Lab" ? existing.Id : null);

        Assert.Empty(plan.Attachments);
        Assert.Equal(ImportLoginKind.Inherit, plan.Choices[matching].Kind);
        Assert.Equal(ImportLoginKind.Identity, plan.Choices[other1].Kind);
    }

    [Fact]
    public void NoUsernameInherits_DeclinedLoginsStayOwn_TelnetIsIgnored()
    {
        var blank = Ssh("a", "Branch", "");
        var a = Ssh("b", "Branch", "netops");
        var b = Ssh("c", "Branch", "netops");
        var telnet = Ssh("t", "Branch", "netops") with { Protocol = ImportCandidate.TelnetProtocol };

        var declined = Plan([blank, a, b, telnet], declined: new HashSet<string> { "netops||" });

        Assert.Empty(declined.Suggestions);
        Assert.Equal(ImportLoginKind.Inherit, declined.Choices[blank].Kind);
        Assert.Equal(ImportLoginKind.Own, declined.Choices[a].Kind);
        Assert.False(declined.Choices.ContainsKey(telnet));
    }

    [Fact]
    public void SameUserWithDifferentSavedPasswords_SplitsWithUniqueFolderNames()
    {
        static ImportCandidate WithPassword(string name, string folder, string fingerprint) =>
            Ssh(name, folder, "root") with { PasswordFingerprint = fingerprint };
        var plan = Plan([
            WithPassword("a", "DC", "P1"), WithPassword("b", "DC", "P1"), WithPassword("c", "DC", "P1"),
            WithPassword("d", "Lab", "P2"), WithPassword("e", "Lab", "P2"),
            WithPassword("f", "Lab", "P3"), WithPassword("g", "Lab", "P3"),
        ]);

        Assert.Equal(["root", "root (Lab)", "root (Lab 2)"], plan.Suggestions.Select(s => s.Name).ToArray());
        Assert.All(plan.Suggestions, s => Assert.True(s.LikelyShared));
        Assert.Equal(3, plan.Suggestions[0].SessionCount);
    }

    [Theory]
    [InlineData("03:", 48, true)]
    [InlineData("02:", 32, true)]
    [InlineData("01:", 48, false)]
    public void PasswordFingerprint_ComparesOnlyTheLeadingBlocks(string prefix, int leadingBytes, bool supported)
    {
        var leading = new string('a', leadingBytes * 2);
        var same1 = SecureCrtImporter.PasswordFingerprint(prefix + leading + new string('1', 32));
        var same2 = SecureCrtImporter.PasswordFingerprint(prefix + leading + new string('2', 32));
        var other = SecureCrtImporter.PasswordFingerprint(prefix + new string('b', leadingBytes * 2) + new string('1', 32));

        if (!supported)
        {
            Assert.Null(same1);
            return;
        }
        Assert.NotNull(same1);
        Assert.Equal(same1, same2);
        Assert.NotEqual(same1, other);
        Assert.DoesNotContain(leading, same1);
        Assert.Null(SecureCrtImporter.PasswordFingerprint(prefix + "zz" + leading));
        Assert.Null(SecureCrtImporter.PasswordFingerprint(prefix + "aa"));
        Assert.Null(SecureCrtImporter.PasswordFingerprint(""));
    }

    [Fact]
    public void Commit_CreatesIdentitiesAttachesFoldersAndSkipsDuplicates()
    {
        var store = new SessionStore(Path.Combine(_dir.FullName, "sessions.json"));
        store.Load();
        var identities = new IdentityStore(Path.Combine(_dir.FullName, "identities.json"));
        identities.Load();
        var keys = new SshKeyStore(Path.Combine(_dir.FullName, "ssh-keys.json"));
        keys.Load();
        var duplicate = Ssh("a", "DC", "netops");
        store.Add(new Session { Name = "a", Host = "a.example", Username = "old" });
        var candidates = new[]
        {
            duplicate, Ssh("b", "DC", "netops"), Ssh("c", "DC", "netops"), Ssh("d", "DC", "local"),
            Ssh("e", "Cloud", "ubuntu", Path.Combine(_dir.FullName, "id")), Ssh("f", "Cloud", "ubuntu", Path.Combine(_dir.FullName, "id")),
        };
        var plan = ImportIdentityPlanner.Plan(candidates, identities.Identities, store.FolderIdentity,
            id => keys.Find(id)?.Path);

        var (imported, duplicates) = SecureCrtImporter.Commit(store, candidates, keys, plan, identities);

        Assert.Equal((5, 1), (imported, duplicates));
        var netops = identities.Identities.Single(i => i.Username == "netops");
        var ubuntu = identities.Identities.Single(i => i.Username == "ubuntu");
        Assert.Equal(IdentityProof.Password, netops.Proof);
        Assert.Equal(IdentityProof.Key, ubuntu.Proof);
        Assert.Equal(Path.Combine(_dir.FullName, "id"), keys.Find(ubuntu.KeyId!.Value)!.Path);
        Assert.Equal(netops.Id, store.FolderIdentity("DC"));
        Assert.Equal(ubuntu.Id, store.FolderIdentity("Cloud"));
        var resolver = new LoginResolver(store, identities);
        Assert.Equal(netops.Id, resolver.Resolve(store.Sessions.Single(s => s.Name == "b")).Identity!.Id);
        var own = store.Sessions.Single(s => s.Name == "d");
        Assert.Equal((SessionLogin.Own, "local"), (own.Login, own.Username));
        Assert.True(own.CredentialNeeded);
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
