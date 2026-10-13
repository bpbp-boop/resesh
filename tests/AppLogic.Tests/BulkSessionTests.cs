using Resesh.App.ViewModels;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

/// <summary>The bulk changes behind Settings &gt; Sessions: stored passwords follow the
/// sessions, open tabs pick up the edits, and the tree shows the result.</summary>
public sealed class BulkSessionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("resesh-bulk-tests-").FullName;
    private readonly Credentials _credentials = new();
    private readonly SessionStore _store;
    private int _changes;

    public BulkSessionTests() => _store = new SessionStore(Path.Combine(_directory, "sessions.json"));

    public void Dispose() => Directory.Delete(_directory, true);

    private MainViewModel Window() => new(_store, _credentials, new ViewModelEnvironment
    {
        CurrentTheme = () => "dark",
        ResolveTheme = theme => theme,
        ShowAgentIcons = () => true,
        IsSessionVisible = _ => true,
        ApplySessionSettings = _ => { },
        ReportError = error => throw error,
        SessionsChanged = _ => _changes++,
    });

    private static Session Ssh(string name) => new() { Name = name, Host = name + ".example", Username = "admin" };

    private static IEnumerable<string> SessionNames(MainViewModel window) =>
        window.RootNodes.Where(node => node.Session is not null).Select(node => node.Name);

    [Fact]
    public void UpdateSessions_SavesEveryEditAndDropsPasswordsNoLongerUsed()
    {
        var window = Window();
        var a = Ssh("a");
        var b = Ssh("b");
        window.AddSession(a, "secret-a");
        window.AddSession(b, "secret-b");
        var tab = window.Connect(a);
        _changes = 0;

        var edit = new SessionBulkEdit { Login = SessionLogin.Identity, IdentityId = Guid.NewGuid() };
        window.UpdateSessions([edit.Apply(a), b with { Port = 2222 }]);

        Assert.Equal(SessionLogin.Identity, _store.Find(a.Id)!.Login);
        Assert.Equal(2222, _store.Find(b.Id)!.Port);
        Assert.Null(_credentials.Read(a.Id));
        Assert.Equal("secret-b", _credentials.Read(b.Id));
        Assert.Equal(SessionLogin.Identity, tab.Session.Login);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void DuplicateSessions_CopiesSavedPasswordsAndShowsTheCopies()
    {
        var window = Window();
        var a = Ssh("a");
        window.AddSession(a, "secret-a");

        var copy = Assert.Single(window.DuplicateSessions([a]));

        Assert.Equal("a copy", copy.Name);
        Assert.Equal("secret-a", _credentials.Read(copy.Id));
        Assert.Equal(["a", "a copy"], SessionNames(window));
    }

    [Fact]
    public void DeleteSessions_RemovesSessionsAndTheirCredentials()
    {
        var window = Window();
        var a = Ssh("a");
        var b = Ssh("b");
        var kept = Ssh("kept");
        window.AddSession(a, "secret-a");
        window.AddSession(b, null);
        window.AddSession(kept, "secret-kept");

        window.DeleteSessions([a, b]);

        Assert.Equal([kept.Id], _store.Sessions.Select(s => s.Id));
        Assert.Null(_credentials.Read(a.Id));
        Assert.Equal("secret-kept", _credentials.Read(kept.Id));
        Assert.Equal(["kept"], SessionNames(window));
    }

    private sealed class Credentials : ICredentialService
    {
        private readonly Dictionary<Guid, string> _secrets = [];

        public string? Read(Guid sessionId) => _secrets.GetValueOrDefault(sessionId);
        public void Write(Guid sessionId, string secret) => _secrets[sessionId] = secret;
        public void Delete(Guid sessionId) => _secrets.Remove(sessionId);
    }
}
