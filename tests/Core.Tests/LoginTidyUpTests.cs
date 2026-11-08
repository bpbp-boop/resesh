using Resesh.Core.Credentials;
using Resesh.Core.Identities;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class LoginTidyUpTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("login-tidy-up-test");
    private readonly SessionStore _store;
    private readonly IdentityStore _identities;
    private readonly SshKeyStore _keys;
    private readonly ICredentialService _credentials = new MemoryCredentials();

    public LoginTidyUpTests()
    {
        _store = new SessionStore(Path.Combine(_dir.FullName, "sessions.json"));
        _store.Load();
        _identities = new IdentityStore(Path.Combine(_dir.FullName, "identities.json"));
        _identities.Load();
        _keys = new SshKeyStore(Path.Combine(_dir.FullName, "ssh-keys.json"));
        _keys.Load();
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private Session Add(string name, string folder, string username, string? password = null, bool inherit = false)
    {
        var session = new Session { Name = name, Host = $"{name}.example", FolderPath = folder, Username = username, InheritLogin = inherit };
        _store.Add(session);
        if (password is not null)
            _credentials.Write(session.Id, password);
        return session;
    }

    private LoginTidyUpPlan Plan(IReadOnlySet<string>? declined = null) =>
        LoginTidyUp.Plan(_store.Sessions, _identities.Identities, _store.FolderIdentity,
            id => _keys.Find(id)?.Path, _credentials.Read, declined);

    [Fact]
    public void SameSavedPasswordBecomesFolderIdentityWithThatPassword()
    {
        var a = Add("a", "DC", "netops", "s3cret");
        var b = Add("b", "DC", "netops", "s3cret");

        var created = LoginTidyUp.Apply(Plan(), _store, _identities, _keys, _credentials);

        var identity = Assert.Single(created);
        Assert.Equal("netops", identity.Username);
        Assert.Equal(identity.Id, _store.FolderIdentity("DC"));
        Assert.Equal("s3cret", _credentials.ReadIdentity(identity.Id));
        foreach (var id in new[] { a.Id, b.Id })
        {
            var saved = _store.Find(id)!;
            Assert.Equal(SessionLogin.Inherit, saved.Login);
            Assert.Equal("", saved.Username); // a leftover username would override the identity's
            Assert.Null(_credentials.Read(id));
        }
    }

    [Fact]
    public void DifferentSavedPasswordsAreNotGrouped()
    {
        Add("a", "DC", "netops", "one");
        Add("b", "DC", "netops", "two");

        var plan = Plan();

        Assert.Empty(plan.Plan.Suggestions);
        Assert.Equal(0, plan.ChangedSessions);
    }

    [Fact]
    public void FolderWithInheritingSessionIsNotAttached()
    {
        var a = Add("a", "DC", "netops", "s3cret");
        Add("b", "DC/Core", "netops", "s3cret");
        var inheriting = Add("c", "DC/Core", "", inherit: true);

        LoginTidyUp.Apply(Plan(), _store, _identities, _keys, _credentials);

        Assert.Null(_store.FolderIdentity("DC"));
        Assert.Null(_store.FolderIdentity("DC/Core"));
        var identity = Assert.Single(_identities.Identities);
        Assert.Equal(identity.Id, _store.Find(a.Id)!.IdentityId);
        Assert.Equal(_store.Find(inheriting.Id), inheriting);
    }

    [Fact]
    public void DeclinedLoginKeepsOwnLogin()
    {
        var a = Add("a", "DC", "netops", "s3cret");
        Add("b", "DC", "netops", "s3cret");
        var key = Assert.Single(Plan().Plan.Suggestions).Login.Key;

        var created = LoginTidyUp.Apply(Plan(new HashSet<string> { key }), _store, _identities, _keys, _credentials);

        Assert.Empty(created);
        Assert.Equal(SessionLogin.Own, _store.Find(a.Id)!.Login);
        Assert.Equal("s3cret", _credentials.Read(a.Id));
    }

    private sealed class MemoryCredentials : ICredentialService
    {
        private readonly Dictionary<Guid, string> _secrets = [];

        public string? Read(Guid sessionId) => _secrets.GetValueOrDefault(sessionId);
        public void Write(Guid sessionId, string secret) => _secrets[sessionId] = secret;
        public void Delete(Guid sessionId) => _secrets.Remove(sessionId);
    }
}
