using Sessions.Core.Models;
using Sessions.Core.Storage;

namespace Sessions.Core.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sessions-store-tests").FullName;
    private string StorePath => Path.Combine(_dir, "sessions.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SessionStore NewStore()
    {
        var store = new SessionStore(StorePath);
        store.Load();
        return store;
    }

    private static Session NewSession(string name = "web-01", string folder = "", string host = "10.0.0.1") =>
        new() { Name = name, Host = host, FolderPath = folder, Username = "admin" };

    [Fact]
    public void RoundTrip_PersistsAllFields()
    {
        var session = new Session
        {
            Name = "core-sw-3",
            FolderPath = "Branch/Floor 2",
            Host = "192.168.1.3",
            Port = 2222,
            Username = "netops",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = @"C:\keys\id_ed25519",
            PassphraseRequired = true,
            Notes = "core switch",
            ColorTag = "#FF8800",
            CredentialNeeded = true,
            Overrides = new TerminalOverrides { Theme = "light", FontSize = 18 },
        };
        NewStore().Add(session);

        var reloaded = NewStore().Find(session.Id);
        Assert.Equal(session, reloaded);
    }

    [Fact]
    public void Load_MissingFile_YieldsEmptyStore()
    {
        Assert.Empty(NewStore().Sessions);
    }

    [Fact]
    public void Load_CorruptMainFile_FallsBackToBackup()
    {
        var store = NewStore();
        var session = NewSession();
        store.Add(session);
        store.Add(NewSession("web-02", host: "10.0.0.2")); // second save rotates first into .bak

        File.WriteAllText(StorePath, "{ this is not json");

        var recovered = NewStore();
        Assert.Contains(recovered.Sessions, s => s.Id == session.Id);
    }

    [Fact]
    public void Save_KeepsOneBackupRotation()
    {
        var store = NewStore();
        store.Add(NewSession());
        store.Add(NewSession("web-02", host: "10.0.0.2"));
        Assert.True(File.Exists(StorePath));
        Assert.True(File.Exists(StorePath + ".bak"));
        Assert.False(File.Exists(StorePath + ".tmp"));
    }

    [Fact]
    public void Update_ReplacesSession()
    {
        var store = NewStore();
        var session = NewSession();
        store.Add(session);
        store.Update(session with { Host = "10.9.9.9" });
        Assert.Equal("10.9.9.9", NewStore().Find(session.Id)!.Host);
    }

    [Fact]
    public void Remove_DeletesAndPersists()
    {
        var store = NewStore();
        var session = NewSession();
        store.Add(session);
        Assert.True(store.Remove(session.Id));
        Assert.Null(NewStore().Find(session.Id));
    }

    [Fact]
    public void MoveToFolder_ChangesFolderPath()
    {
        var store = NewStore();
        var session = NewSession(folder: "A");
        store.Add(session);
        store.MoveToFolder(session.Id, "B/C");
        Assert.Equal("B/C", store.Find(session.Id)!.FolderPath);
    }

    [Fact]
    public void Folders_IncludeExplicitAndDerivedAncestors()
    {
        var store = NewStore();
        store.CreateFolder("Empty/Nested");
        store.Add(NewSession(folder: "Datacenter/Rack 4"));

        var folders = store.Folders;
        Assert.Contains("Empty", folders);
        Assert.Contains("Empty/Nested", folders);
        Assert.Contains("Datacenter", folders);
        Assert.Contains("Datacenter/Rack 4", folders);
    }

    [Fact]
    public void RenameFolder_MovesSessionsAndSubfolders()
    {
        var store = NewStore();
        var inFolder = NewSession("a", "Old/Sub");
        var atRoot = NewSession("b");
        store.Add(inFolder);
        store.Add(atRoot);
        store.CreateFolder("Old/Empty");

        store.RenameFolder("Old", "New");

        Assert.Equal("New/Sub", store.Find(inFolder.Id)!.FolderPath);
        Assert.Equal("", store.Find(atRoot.Id)!.FolderPath);
        Assert.Contains("New/Empty", store.Folders);
        Assert.DoesNotContain("Old/Empty", store.Folders);
    }

    [Fact]
    public void DeleteFolder_RemovesDescendantsAndReturnsThem()
    {
        var store = NewStore();
        var inside = NewSession("a", "Gone/Deep");
        var outside = NewSession("b", "Kept");
        store.Add(inside);
        store.Add(outside);

        var removed = store.DeleteFolder("Gone");

        Assert.Single(removed);
        Assert.Equal(inside.Id, removed[0].Id);
        Assert.Null(store.Find(inside.Id));
        Assert.NotNull(store.Find(outside.Id));
    }

    [Fact]
    public void FolderPaths_NormalizeHandlesSlashesAndWhitespace()
    {
        Assert.Equal("A/B", FolderPaths.Normalize(@" A \ B "));
        Assert.Equal("", FolderPaths.Normalize(null));
        Assert.Equal("", FolderPaths.Normalize("///"));
        Assert.Equal("A/B", FolderPaths.Combine("A", "B"));
        Assert.Equal("B", FolderPaths.Combine("", "B"));
        Assert.Equal("A", FolderPaths.Parent("A/B"));
        Assert.Equal("", FolderPaths.Parent("A"));
    }
}
