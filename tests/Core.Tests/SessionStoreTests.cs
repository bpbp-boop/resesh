using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

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
    public void ReturnedRecordsAndWriteInputsCannotChangeStoredState()
    {
        var args = new List<string> { "original" };
        var environment = new Dictionary<string, string> { ["TEST"] = "original" };
        var tunnel = new Tunnel { BindPort = 8080, DestinationHost = "original", DestinationPort = 80 };
        var algorithms = new SshAlgorithms { DisabledCiphers = new List<string> { "original" } };
        var session = NewSession(folder: "Folder") with
        {
            Local = new LocalTarget { Executable = "original", Arguments = args, Environment = environment },
            Serial = new SerialTarget { PortName = "COM1" },
            Proxy = new ProxySettings { Host = "original" },
            Tunnels = [tunnel],
            Algorithms = algorithms,
            Overrides = new TerminalOverrides { EnabledRules = new List<string> { "original" } },
        };
        var store = NewStore();
        store.Add(session);
        var writes = 0;
        store.Changed += () => writes++;
        var file = File.ReadAllText(StorePath);

        session.Name = "changed input";
        args[0] = "changed input";
        environment["TEST"] = "changed input";
        tunnel.DestinationHost = "changed input";
        algorithms.DisabledCiphers = ["changed input"];

        foreach (var copy in new[] { store.Find(session.Id)!, store.Sessions[0], store.SessionsInFolder("Folder")[0] })
        {
            copy.Name = "changed read";
            copy.Local!.Executable = "changed read";
            ((string[])copy.Local.Arguments)[0] = "changed read";
            ((Dictionary<string, string>)copy.Local.Environment!)["TEST"] = "changed read";
            copy.Serial!.PortName = "COM2";
            copy.Proxy!.Host = "changed read";
            copy.Tunnels![0].DestinationHost = "changed read";
            copy.Algorithms!.DisabledCiphers = ["changed read"];
            ((string[])copy.Overrides!.EnabledRules!)[0] = "changed read";
        }

        var stored = store.Find(session.Id)!;
        Assert.Equal("web-01", stored.Name);
        Assert.Equal("original", stored.Local!.Executable);
        Assert.Equal(["original"], stored.Local.Arguments);
        Assert.Equal("original", stored.Local.Environment!["TEST"]);
        Assert.Equal("COM1", stored.Serial!.PortName);
        Assert.Equal("original", stored.Proxy!.Host);
        Assert.Equal("original", stored.Tunnels![0].DestinationHost);
        Assert.Equal(["original"], stored.Algorithms!.DisabledCiphers);
        Assert.Equal(["original"], stored.Overrides!.EnabledRules);
        Assert.Equal(file, File.ReadAllText(StorePath));
        Assert.Equal(0, writes);
        store.Update(stored with { Name = "saved" });
        Assert.Equal("saved", NewStore().Find(session.Id)!.Name);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void FolderSettingsDoNotShareMutableRecordsWithTheStore()
    {
        var store = NewStore();
        var identity = Guid.NewGuid();
        var setting = new FolderSettings { Path = "Folder", IdentityId = identity };
        store.MergeFolderSettings([setting]);
        setting.Path = "changed input";
        store.FolderSettings[0].Path = "changed read";
        Assert.Equal(identity, store.FolderIdentity("Folder"));
    }

    [Theory]
    [InlineData("\"startingDirectory\":null")]
    [InlineData("\"arguments\":null")]
    [InlineData("\"arguments\":[null]")]
    public void InvalidNestedLocalDataFallsBackToBackup(string invalidProperty)
    {
        var good = NewSession();
        var store = NewStore();
        store.Add(good);
        File.Copy(StorePath, StorePath + ".bak");
        File.WriteAllText(StorePath, "{\"sessions\":[{\"name\":\"bad\",\"kind\":\"local\",\"local\":{\"executable\":\"cmd.exe\"," + invalidProperty + "}}]}");

        var recovered = NewStore();
        Assert.Equal(good.Id, Assert.Single(recovered.Sessions).Id);
        Assert.Equal(StoreLoadState.RecoveredFromBackup, recovered.LoadState);
    }

    [Fact]
    public void DuplicateIdsInJsonFallBackToBackup()
    {
        var good = NewSession();
        var store = NewStore();
        store.Add(good);
        File.Copy(StorePath, StorePath + ".bak");
        File.WriteAllText(StorePath, $$"""
            {"sessions":[{"id":"{{good.Id}}","name":"one"},{"id":"{{good.Id}}","name":"two"}]}
            """);
        var recovered = NewStore();
        Assert.Equal("web-01", Assert.Single(recovered.Sessions).Name);
        Assert.Equal(StoreLoadState.RecoveredFromBackup, recovered.LoadState);
    }

    [Fact]
    public void InvalidWriteLeavesTheFileAndPublishedStateUnchanged()
    {
        var good = NewSession();
        var store = NewStore();
        store.Add(good);
        var file = File.ReadAllText(StorePath);
        var writes = 0;
        store.Changed += () => writes++;
        Assert.Throws<InvalidDataException>(() => store.Update(good with
        {
            Local = new LocalTarget { StartingDirectory = null! },
        }));
        Assert.Equal(file, File.ReadAllText(StorePath));
        Assert.Null(store.Find(good.Id)!.Local);
        Assert.Equal(0, writes);
    }

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
            Overrides = new TerminalOverrides { Theme = "light", FontSize = 18 },
        };
        NewStore().Add(session);

        var reloaded = NewStore().Find(session.Id);
        Assert.Equal(session, reloaded);
    }

    [Fact]
    public void Load_IgnoresFieldsNoLongerKept()
    {
        // credentialNeeded was saved by imports until it was dropped as never read.
        var id = Guid.NewGuid();
        File.WriteAllText(StorePath, $$"""
            {"sessions":[{"id":"{{id}}","name":"core-sw-3","host":"192.168.1.3","username":"netops","folderPath":"","credentialNeeded":true}]}
            """);

        var store = NewStore();

        Assert.Null(store.LoadWarning);
        Assert.Equal("core-sw-3", store.Find(id)?.Name);
    }

    [Fact]
    public void SetFolderIdentity_KeepsTheFolderAfterItsSessionsMoveOut()
    {
        var store = NewStore();
        var session = NewSession(folder: "Prod");
        store.Add(session);
        var identityId = Guid.NewGuid();

        store.SetFolderIdentity("Prod", identityId);
        store.MoveToFolder(session.Id, "");

        // The folder stays, showing the identity it holds, rather than vanishing while the
        // identity waits to log in whatever is made at "Prod" next.
        Assert.Equal(["Prod"], store.Folders);
        Assert.Equal(["Prod"], NewStore().Folders);
        Assert.Equal(identityId, store.FolderIdentity("Prod"));
        store.DeleteFolder("Prod");
        Assert.Empty(store.Folders);
        Assert.Null(store.FolderIdentity("Prod"));
    }

    [Fact]
    public void DeleteMany_RemovesFoldersOfBothKindsAndSessionsInOneWrite()
    {
        var store = NewStore();
        store.CreateFolder("Prod/Web");
        store.CreateFolder("Tools", SessionKind.Local);
        var web = NewSession("web", "Prod/Web");
        var loose = NewSession("loose");
        var kept = NewSession("kept", "Lab");
        var profile = new Session { Name = "pwsh", Kind = SessionKind.Local, FolderPath = "Tools" };
        store.AddMany([web, loose, kept, profile]);
        store.SetFolderIdentity("Prod", Guid.NewGuid());
        var writes = 0;
        store.Changed += () => writes++;

        var removed = store.DeleteMany([("Prod", SessionKind.Ssh), ("Tools", SessionKind.Local)], [loose.Id]);

        Assert.Equal(1, writes);
        Assert.Equal(["web", "loose", "pwsh"], removed.Select(s => s.Name));
        Assert.Equal(["kept"], NewStore().Sessions.Select(s => s.Name));
        Assert.Equal(["Lab"], NewStore().Folders);
        Assert.Empty(NewStore().FoldersOf(SessionKind.Local));
        Assert.Null(store.FolderIdentity("Prod"));
    }

    [Fact]
    public void SetFolderIdentity_OnTheRootAddsNoFolder()
    {
        var store = NewStore();

        store.SetFolderIdentity("", Guid.NewGuid());

        Assert.Empty(store.Folders);
        Assert.Empty(store.ExplicitFoldersOf(SessionKind.Ssh));
    }

    [Fact]
    public void Load_FolderSettingWithNoFolder_BringsTheFolderBack()
    {
        // Written by builds that let a folder identity outlive its implicit folder.
        var identityId = Guid.NewGuid();
        File.WriteAllText(StorePath, $$"""
            {"sessions":[],"folders":[],"folderSettings":[{"path":"Prod/Web","identityId":"{{identityId}}"},{"path":"","identityId":"{{identityId}}"}]}
            """);

        var store = NewStore();

        Assert.Equal(["Prod", "Prod/Web"], store.Folders);
        Assert.Equal(["Prod/Web"], store.ExplicitFoldersOf(SessionKind.Ssh));
        Assert.Equal(identityId, store.FolderIdentity("Prod/Web"));
    }

    [Fact]
    public void MergeFolderSettings_MakesTheirFoldersExplicit()
    {
        var store = NewStore();

        store.MergeFolderSettings([new FolderSettings { Path = "Branch", IdentityId = Guid.NewGuid() }]);

        Assert.Equal(["Branch"], store.ExplicitFoldersOf(SessionKind.Ssh));
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
    public void Load_CorruptMainFile_WarnsAndKeepsTheGoodBackupOnNextSave()
    {
        var store = NewStore();
        var session = NewSession();
        store.Add(session);
        store.Add(NewSession("web-02", host: "10.0.0.2"));
        File.WriteAllText(StorePath, "{ this is not json");

        var recovered = NewStore();
        Assert.Contains("recovered from the backup", recovered.LoadWarning);
        recovered.Add(NewSession("web-03", host: "10.0.0.3"));

        Assert.Contains(session.Id.ToString(), File.ReadAllText(StorePath + ".bak"));
        Assert.DoesNotContain("not json", File.ReadAllText(StorePath + ".bak"));
    }

    [Fact]
    public void Load_UnreadableWithoutBackup_KeepsACopyThatLaterSavesCannotOverwrite()
    {
        File.WriteAllText(StorePath, "{ \"sessions\": [ truncated");

        var store = NewStore();
        Assert.Empty(store.Sessions);
        Assert.Contains("could not be loaded", store.LoadWarning);
        // Startup re-adds built-in shells one save at a time; two saves used to rotate the
        // only copy of the user's file out of .bak.
        store.Add(NewSession("pwsh"));
        store.Add(NewSession("cmd"));

        var kept = Assert.Single(Directory.GetFiles(_dir, "sessions.json.unreadable-*"));
        Assert.Equal("{ \"sessions\": [ truncated", File.ReadAllText(kept));
        Assert.Contains(kept, store.LoadWarning);
    }

    [Fact]
    public void Load_MissingFile_HasNoWarning()
    {
        Assert.Null(NewStore().LoadWarning);
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

    [Theory]
    [InlineData("Add")]
    [InlineData("Update")]
    [InlineData("Remove")]
    [InlineData("MoveToFolder")]
    [InlineData("MoveToFolderBatch")]
    [InlineData("CreateFolder")]
    [InlineData("CreateLocalFolder")]
    [InlineData("SetFolderIdentity")]
    [InlineData("ReplaceFolderIdentity")]
    [InlineData("ClearFolderIdentity")]
    [InlineData("MergeFolderSettings")]
    [InlineData("DetachIdentity")]
    [InlineData("ApplyImport")]
    [InlineData("RenameFolder")]
    [InlineData("RenameLocalFolder")]
    [InlineData("DeleteFolder")]
    [InlineData("DeleteLocalFolder")]
    public void Mutation_SaveFailure_DoesNotPublishOrPersistOnLaterSave(string mutation)
    {
        var store = NewStore();
        var identityId = Guid.NewGuid();
        var first = NewSession("first", "Original/Child") with { IdentityId = identityId };
        var second = NewSession("second", "Original");
        var local = NewSession("local", "Original/Child") with { Kind = SessionKind.Local };
        store.ApplyImport([first, second, local], ["Original/Empty"], ["Original/Empty"]);
        store.SetFolderIdentity("", identityId);
        store.SetFolderIdentity("Original", identityId);
        store.SetFolderIdentity("Original/Child", identityId);

        var sessions = store.Sessions;
        var folders = store.Folders;
        var localFolders = store.FoldersOf(SessionKind.Local);
        var folderSettings = store.FolderSettings;
        var originalFile = File.ReadAllText(StorePath);
        var originalBackup = File.ReadAllText(StorePath + ".bak");
        var attempted = NewSession("failed", "Imported");
        Action mutate = mutation switch
        {
            "Add" => () => store.Add(attempted),
            "Update" => () => store.Update(first with { Name = "failed", Host = "10.9.9.9" }),
            "Remove" => () => store.Remove(first.Id),
            "MoveToFolder" => () => store.MoveToFolder(first.Id, "Moved"),
            "MoveToFolderBatch" => () => store.MoveToFolder([first.Id, second.Id, local.Id], "Moved"),
            "CreateFolder" => () => store.CreateFolder("Failed/Empty"),
            "CreateLocalFolder" => () => store.CreateFolder("Failed/Empty", SessionKind.Local),
            "SetFolderIdentity" => () => store.SetFolderIdentity("Failed", identityId),
            "ReplaceFolderIdentity" => () => store.SetFolderIdentity("Original", Guid.NewGuid()),
            "ClearFolderIdentity" => () => store.SetFolderIdentity("Original", null),
            "MergeFolderSettings" => () => store.MergeFolderSettings(
                [
                    new FolderSettings { Path = "Imported", IdentityId = identityId },
                    new FolderSettings { Path = "Imported/Child", IdentityId = identityId },
                ]),
            "DetachIdentity" => () => store.DetachIdentity(identityId),
            "ApplyImport" => () => store.ApplyImport(
                [first with { Name = "failed" }, attempted], ["Imported/Empty"], ["Imported/Local"]),
            "RenameFolder" => () => store.RenameFolder("Original", "Renamed"),
            "RenameLocalFolder" => () => store.RenameFolder("Original", "Renamed", SessionKind.Local),
            "DeleteFolder" => () => store.DeleteFolder("Original"),
            "DeleteLocalFolder" => () => store.DeleteFolder("Original", SessionKind.Local),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Directory.CreateDirectory(StorePath + ".tmp");
        try
        {
            var error = Record.Exception(mutate);
            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Expected an atomic-write failure, got {error}.");
            Assert.Equal(sessions, store.Sessions);
            Assert.Equal(folders, store.Folders);
            Assert.Equal(localFolders, store.FoldersOf(SessionKind.Local));
            Assert.Equal(folderSettings, store.FolderSettings);
            Assert.Equal(first, store.Find(first.Id));
            Assert.Null(store.Find(attempted.Id));
            Assert.Equal(originalFile, File.ReadAllText(StorePath));
            Assert.Equal(originalBackup, File.ReadAllText(StorePath + ".bak"));
        }
        finally
        {
            Directory.Delete(StorePath + ".tmp");
        }

        var committed = NewSession("committed");
        store.Add(committed);
        var reloaded = NewStore();
        Assert.Equal(sessions.Append(committed), store.Sessions);
        Assert.Equal(sessions.Append(committed), reloaded.Sessions);
        Assert.Equal(folders, reloaded.Folders);
        Assert.Equal(localFolders, reloaded.FoldersOf(SessionKind.Local));
        Assert.Equal(folderSettings, reloaded.FolderSettings);
    }

    [Fact]
    public void RecoveredStore_SaveFailure_PreservesGoodBackupForNextSuccessfulSave()
    {
        var store = NewStore();
        var original = NewSession("original");
        store.Add(original);
        store.Add(NewSession("not-in-backup"));
        var backup = File.ReadAllText(StorePath + ".bak");
        File.WriteAllText(StorePath, "{ corrupt");
        var recovered = NewStore();
        Assert.Contains("recovered from the backup", recovered.LoadWarning);

        var attempted = NewSession("failed");
        Directory.CreateDirectory(StorePath + ".tmp");
        try
        {
            var error = Record.Exception(() => recovered.Add(attempted));
            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Expected an atomic-write failure, got {error}.");
            Assert.Equal(new[] { original }, recovered.Sessions);
            Assert.Equal(backup, File.ReadAllText(StorePath + ".bak"));
        }
        finally
        {
            Directory.Delete(StorePath + ".tmp");
        }

        var committed = NewSession("committed");
        recovered.Add(committed);
        Assert.Equal(backup, File.ReadAllText(StorePath + ".bak"));
        Assert.Equal(new[] { original, committed }, NewStore().Sessions);

        var next = NewSession("next");
        recovered.Add(next);
        File.Copy(StorePath + ".bak", StorePath, overwrite: true);
        Assert.Equal(new[] { original, committed }, NewStore().Sessions);
    }

    [Theory]
    [InlineData("ApplyImportSessions")]
    [InlineData("ApplyImportFolders")]
    [InlineData("ApplyImportLocalFolders")]
    [InlineData("MergeFolderSettings")]
    public void Import_EnumerationFailure_DoesNotPublishPartialChanges(string mutation)
    {
        var store = NewStore();
        var original = NewSession("original");
        store.Add(original);
        var imported = NewSession("imported");
        var setting = new FolderSettings { Path = "Imported", IdentityId = Guid.NewGuid() };
        Action mutate = mutation switch
        {
            "ApplyImportSessions" => () => store.ApplyImport(ThrowAfter(imported), [], []),
            "ApplyImportFolders" => () => store.ApplyImport([imported], ThrowAfter("Imported"), []),
            "ApplyImportLocalFolders" => () => store.ApplyImport(
                [imported], ["Imported"], ThrowAfter("ImportedLocal")),
            "MergeFolderSettings" => () => store.MergeFolderSettings(ThrowAfter(setting)),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Assert.Throws<InvalidOperationException>(mutate);
        Assert.Equal(new[] { original }, store.Sessions);
        Assert.Empty(store.Folders);
        Assert.Empty(store.FoldersOf(SessionKind.Local));
        Assert.Empty(store.FolderSettings);

        var committed = NewSession("committed");
        store.Add(committed);
        var reloaded = NewStore();
        Assert.Equal(new[] { original, committed }, reloaded.Sessions);
        Assert.Empty(reloaded.Folders);
        Assert.Empty(reloaded.FoldersOf(SessionKind.Local));
        Assert.Empty(reloaded.FolderSettings);
    }

    private static IEnumerable<T> ThrowAfter<T>(T item)
    {
        yield return item;
        throw new InvalidOperationException("Import enumeration failed.");
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
    public void MoveToFolder_PersistsAcrossReload()
    {
        var store = NewStore();
        var session = NewSession(folder: "A");
        store.Add(session);
        store.MoveToFolder(session.Id, "B/C");
        Assert.Equal("B/C", NewStore().Find(session.Id)!.FolderPath);
    }

    [Fact]
    public void MoveToFolder_BatchPersistsEverySession()
    {
        var store = NewStore();
        var first = NewSession("first", "A");
        var second = NewSession("second", "B");
        store.Add(first);
        store.Add(second);

        store.MoveToFolder([first.Id, second.Id], "C");

        var reloaded = NewStore();
        Assert.Equal("C", reloaded.Find(first.Id)!.FolderPath);
        Assert.Equal("C", reloaded.Find(second.Id)!.FolderPath);
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
    public void Batches_AddUpdateAndRemoveSeveralSessionsAndPersist()
    {
        var store = NewStore();
        var a = NewSession("a");
        var b = NewSession("b");
        var kept = NewSession("kept");
        store.Add(kept);

        store.AddMany([a, b]);
        store.UpdateMany([a with { Port = 2222 }, b with { Port = 2200 }]);
        var removed = store.RemoveMany([b.Id, Guid.NewGuid()]);

        Assert.Equal([b.Id], removed.Select(s => s.Id));
        var reloaded = NewStore();
        Assert.Equal(2222, reloaded.Find(a.Id)!.Port);
        Assert.Null(reloaded.Find(b.Id));
        Assert.NotNull(reloaded.Find(kept.Id));
    }

    [Fact]
    public void UpdateMany_MissingSession_ChangesNothing()
    {
        var store = NewStore();
        var saved = NewSession("saved");
        store.Add(saved);

        Assert.Throws<InvalidOperationException>(
            () => store.UpdateMany([saved with { Port = 2222 }, NewSession("never saved")]));

        Assert.Equal(22, store.Find(saved.Id)!.Port);
        Assert.Equal(22, NewStore().Find(saved.Id)!.Port);
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

    [Fact]
    public void Changed_IsRaisedAfterEverySavedChangeOnly()
    {
        var store = NewStore();
        var session = NewSession();
        var changes = 0;
        store.Changed += () =>
        {
            changes++;
            Assert.NotNull(store.Find(session.Id)); // handlers see the saved state
        };

        store.Add(session);
        store.SetFolderIdentity("", Guid.NewGuid());
        store.DetachIdentity(store.FolderIdentity("")!.Value);
        store.MoveToFolder(session.Id, ""); // already there: nothing to save
        Assert.Equal(3, changes);

        using (new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => store.Update(session with { Name = "renamed" }));
        Assert.Equal(3, changes);
        Assert.Equal("web-01", store.Find(session.Id)!.Name);
    }

    [Fact]
    public void SessionsInFolder_IsWhatDeleteFolderRemoves()
    {
        var store = NewStore();
        store.AddMany(
        [
            NewSession("ssh", "Lab"),
            NewSession("telnet", "Lab/Consoles") with { Kind = SessionKind.Telnet },
            NewSession("serial", "Lab") with { Kind = SessionKind.Serial, Serial = new SerialTarget { PortName = "COM3" } },
            NewSession("elsewhere", "Prod"),
            new Session { Name = "pwsh", Kind = SessionKind.Local, FolderPath = "Lab", Local = new LocalTarget { Executable = "pwsh.exe" } },
        ]);

        var counted = store.SessionsInFolder("Lab").Select(s => s.Name).Order().ToList();
        var deleted = store.DeleteFolder("Lab").Select(s => s.Name).Order().ToList();

        Assert.Equal(["serial", "ssh", "telnet"], counted);
        Assert.Equal(counted, deleted);
        Assert.Equal(["elsewhere", "pwsh"], store.Sessions.Select(s => s.Name).Order());
    }
}
