using System.Text;
using System.IO.Compression;
using Resesh.Core.Backup;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class SessionsBackupTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("sessions-backup-test");

    [Fact]
    public void ExportRead_FolderScopeIncludesOnlyThatSubtreeAndGlobalBackupData()
    {
        var source = CreateStores(Path.Combine(_dir.FullName, "source"));
        var selected = Ssh("Selected", "selected.example", "Ops/Prod");
        source.Sessions.Add(selected);
        source.Sessions.Add(Ssh("Other", "other.example", "Personal"));
        source.Sessions.CreateFolder("Ops/Empty");
        source.Sessions.Add(new Session
        {
            Name = "PowerShell",
            Kind = SessionKind.Local,
            FolderPath = "Shells",
            Local = new LocalTarget { Executable = "pwsh.exe" },
        });
        source.Settings.Save(new AppSettings { Theme = "light", FontSize = 17 });
        source.KnownHosts.Accept("selected.example", 22, "ssh-ed25519", "fingerprint");
        Directory.CreateDirectory(Path.Combine(source.Directory, "icons"));
        File.WriteAllText(Path.Combine(source.Directory, "icons", "lab.svg"), "<svg/>");
        File.WriteAllText(Path.Combine(source.Directory, "recording.cast"), "must not be exported");
        File.WriteAllText(Path.Combine(source.Directory, "workspaces.json"), "{\"workspaces\":[]}");

        var path = Path.Combine(_dir.FullName, "filtered.reseshbackup");
        SessionsBackup.Export(path, source.Directory, source.Sessions, source.Settings,
            source.KnownHosts, source.Highlights, source.SshKeys, source.Credentials,
            new BackupExportOptions { Scope = new BackupScope(SessionKind.Ssh, "Ops") });

        Assert.False(SessionsBackup.IsEncrypted(path));
        var package = SessionsBackup.Read(path);
        Assert.Equal(SessionsBackup.CurrentSchemaVersion, package.Manifest.SchemaVersion);
        Assert.Equal(selected.Id, Assert.Single(package.Sessions).Id);
        Assert.Equal(["Ops", "Ops/Empty", "Ops/Prod"], package.Folders.OrderBy(f => f));
        Assert.Empty(package.LocalFolders);
        Assert.Equal("light", package.Settings.Theme);
        Assert.Single(package.KnownHosts);
        Assert.Equal("<svg/>", Encoding.UTF8.GetString(package.Icons["lab.svg"]));
        Assert.Equal("{\"workspaces\":[]}", Encoding.UTF8.GetString(package.Workspaces!));
        using var archive = ZipFile.OpenRead(path);
        Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("recording", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExportRead_WithSecretsEncryptsCompleteArchiveAndRejectsWrongPassphrase()
    {
        var source = CreateStores(Path.Combine(_dir.FullName, "secret-source"));
        var session = Ssh("Secret", "secret.example", "");
        source.Sessions.Add(session);
        source.Credentials.Write(session.Id, "correct horse battery staple");
        var path = Path.Combine(_dir.FullName, "secret.reseshbackup");

        SessionsBackup.Export(path, source.Directory, source.Sessions, source.Settings,
            source.KnownHosts, source.Highlights, source.SshKeys, source.Credentials,
            new BackupExportOptions { IncludeSecrets = true, Passphrase = "long test passphrase" });

        Assert.True(SessionsBackup.IsEncrypted(path));
        Assert.DoesNotContain("correct horse battery staple", File.ReadAllText(path, Encoding.Latin1));
        var error = Assert.Throws<InvalidDataException>(() => SessionsBackup.Read(path, "wrong passphrase"));
        Assert.Contains("incorrect", error.Message, StringComparison.OrdinalIgnoreCase);
        var package = SessionsBackup.Read(path, "long test passphrase");
        Assert.True(package.Manifest.IncludesSecrets);
        Assert.Equal("correct horse battery staple", package.Secrets[session.Id]);
    }

    [Fact]
    public void Import_ResolvesIdAndEndpointConflictsAndMapsSecretsAndPinnedIds()
    {
        var source = CreateStores(Path.Combine(_dir.FullName, "merge-source"));
        var replacement = Ssh("Replacement", "id.example", "") with { Id = Guid.NewGuid() };
        var endpointMatch = Ssh("Endpoint import", "same.example", "") with { Username = "alice" };
        var fresh = Ssh("Fresh", "fresh.example", "");
        source.Sessions.Add(replacement);
        source.Sessions.Add(endpointMatch);
        source.Sessions.Add(fresh);
        source.Settings.Save(new AppSettings { PinnedSessionIds = [endpointMatch.Id], FontSize = 19 });
        source.Credentials.Write(replacement.Id, "replacement secret");
        source.Credentials.Write(endpointMatch.Id, "duplicate secret");
        source.KnownHosts.Accept("same.example", 22, "ssh-ed25519", "imported-key");

        var path = Path.Combine(_dir.FullName, "merge.reseshbackup");
        SessionsBackup.Export(path, source.Directory, source.Sessions, source.Settings,
            source.KnownHosts, source.Highlights, source.SshKeys, source.Credentials,
            new BackupExportOptions { IncludeSecrets = true, Passphrase = "merge passphrase" });
        var package = SessionsBackup.Read(path, "merge passphrase");

        var target = CreateStores(Path.Combine(_dir.FullName, "merge-target"));
        target.Sessions.Add(Ssh("Old id record", "old.example", "") with { Id = replacement.Id });
        var existingEndpoint = Ssh("Existing endpoint", "same.example", "") with { Username = "alice" };
        target.Sessions.Add(existingEndpoint);
        target.Credentials.Write(replacement.Id, "old secret");
        target.KnownHosts.Accept("same.example", 22, "ssh-rsa", "existing-key");

        var conflicts = SessionsBackup.FindConflicts(target.Sessions, package);
        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, c => c.Imported.Id == replacement.Id && c.Match == BackupConflictMatch.SessionId);
        Assert.Contains(conflicts, c => c.Imported.Id == endpointMatch.Id && c.Match == BackupConflictMatch.Endpoint);

        var result = SessionsBackup.Import(package, target.Directory, target.Sessions, target.Settings,
            target.KnownHosts, target.Highlights, target.SshKeys, target.Credentials,
            new Dictionary<Guid, BackupConflictResolution>
            {
                [replacement.Id] = BackupConflictResolution.Replace,
                [endpointMatch.Id] = BackupConflictResolution.Duplicate,
            });

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Replaced);
        Assert.Equal(1, result.Duplicated);
        Assert.Equal(4, target.Sessions.Sessions.Count);
        Assert.Equal("Replacement", target.Sessions.Find(replacement.Id)?.Name);
        Assert.Equal("replacement secret", target.Credentials.Read(replacement.Id));
        var duplicate = Assert.Single(target.Sessions.Sessions, s => s.Name == "Endpoint import");
        Assert.NotEqual(endpointMatch.Id, duplicate.Id);
        Assert.Equal("duplicate secret", target.Credentials.Read(duplicate.Id));
        Assert.Equal([duplicate.Id], target.Settings.Current.PinnedSessionIds);
        Assert.Equal(19, target.Settings.Current.FontSize);
        Assert.Equal("existing-key", target.KnownHosts.Lookup("same.example", 22)?.Sha256);
    }

    [Fact]
    public void ExportImport_PreservesReferencedKeyMetadataAndEncryptedKeyPassphrase()
    {
        var source = CreateStores(Path.Combine(_dir.FullName, "key-source"));
        var key = new SshKeyReference
        {
            Name = "Operations",
            Path = @"C:\Users\operator\.ssh\id_ed25519",
            Algorithm = "ssh-ed25519",
            Fingerprint = "SHA256:key-fingerprint",
            IsEncrypted = true,
        };
        source.SshKeys.MergeImport([key]);
        var session = Ssh("Key session", "key.example", "") with
        {
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyId = key.Id,
        };
        source.Sessions.Add(session);
        source.Credentials.WriteKey(key.Id, "key passphrase");
        var path = Path.Combine(_dir.FullName, "key.reseshbackup");

        SessionsBackup.Export(path, source.Directory, source.Sessions, source.Settings,
            source.KnownHosts, source.Highlights, source.SshKeys, source.Credentials,
            new BackupExportOptions { IncludeSecrets = true, Passphrase = "backup passphrase" });

        var package = SessionsBackup.Read(path, "backup passphrase");
        Assert.Equal(key.Id, Assert.Single(package.SshKeys).Id);
        Assert.Equal("key passphrase", package.KeySecrets[key.Id]);

        var target = CreateStores(Path.Combine(_dir.FullName, "key-target"));
        SessionsBackup.Import(package, target.Directory, target.Sessions, target.Settings,
            target.KnownHosts, target.Highlights, target.SshKeys, target.Credentials,
            new Dictionary<Guid, BackupConflictResolution>());

        var importedKey = Assert.Single(target.SshKeys.Keys);
        Assert.Equal("SHA256:key-fingerprint", importedKey.Fingerprint);
        Assert.Equal(importedKey.Id, Assert.Single(target.Sessions.Sessions).PrivateKeyId);
        Assert.Equal("key passphrase", target.Credentials.ReadKey(importedKey.Id));
    }

    [Fact]
    public void Import_WorkspacesReplaceAndRemapEveryConflictOutcome()
    {
        var source = CreateStores(Path.Combine(_dir.FullName, "workspace-source"));
        var replace = Ssh("Imported replacement", "replace.example", "");
        var keep = Ssh("Imported kept", "keep.example", "") with { Username = "alice" };
        var duplicate = Ssh("Imported duplicate", "duplicate.example", "") with { Username = "bob" };
        var fresh = Ssh("Fresh layout tab", "fresh-layout.example", "");
        foreach (var session in new[] { replace, keep, duplicate, fresh })
            source.Sessions.Add(session);

        var targetOnly = Ssh("Target only", "target-only.example", "");
        var unresolvedId = Guid.NewGuid();
        var importedWorkspaceStore = new WorkspaceStore(Path.Combine(source.Directory, "workspaces.json"));
        importedWorkspaceStore.Load();
        var importedWorkspace = importedWorkspaceStore.SaveAs("Imported workspace", new WorkspaceLayout
        {
            Groups =
            [
                new WorkspaceGroup
                {
                    Tabs =
                    [
                        new WorkspaceTabReference { SessionId = unresolvedId },
                        new WorkspaceTabReference { SessionId = replace.Id, Pinned = true },
                        new WorkspaceTabReference { SessionId = keep.Id },
                        new WorkspaceTabReference { SessionId = duplicate.Id, Pinned = true },
                        new WorkspaceTabReference { SessionId = targetOnly.Id },
                    ],
                    ActiveTabIndex = 3,
                },
                new WorkspaceGroup
                {
                    Tabs = [new WorkspaceTabReference { SessionId = fresh.Id }],
                    ActiveTabIndex = 0,
                },
            ],
        });
        importedWorkspaceStore.SaveLastLayout(new WorkspaceLayout
        {
            Groups =
            [
                new WorkspaceGroup
                {
                    Tabs = [new WorkspaceTabReference { SessionId = duplicate.Id }],
                    ActiveTabIndex = 0,
                },
            ],
        });

        var backupPath = Path.Combine(_dir.FullName, "workspace-remap.reseshbackup");
        SessionsBackup.Export(backupPath, source.Directory, source.Sessions, source.Settings,
            source.KnownHosts, source.Highlights, source.SshKeys, source.Credentials, new BackupExportOptions());
        var package = SessionsBackup.Read(backupPath);

        var target = CreateStores(Path.Combine(_dir.FullName, "workspace-target"));
        target.Sessions.Add(Ssh("Existing replaced", "old-replace.example", "") with { Id = replace.Id });
        var keptTarget = Ssh("Existing kept", keep.Host, "") with { Username = keep.Username };
        target.Sessions.Add(keptTarget);
        var duplicateTarget = Ssh("Existing duplicate endpoint", duplicate.Host, "") with { Username = duplicate.Username };
        target.Sessions.Add(duplicateTarget);
        target.Sessions.Add(targetOnly);
        var oldWorkspaceStore = new WorkspaceStore(Path.Combine(target.Directory, "workspaces.json"));
        oldWorkspaceStore.Load();
        oldWorkspaceStore.SaveAs("Must be replaced", new WorkspaceLayout { Groups = [new WorkspaceGroup()] });

        SessionsBackup.Import(package, target.Directory, target.Sessions, target.Settings,
            target.KnownHosts, target.Highlights, target.SshKeys, target.Credentials,
            new Dictionary<Guid, BackupConflictResolution>
            {
                [replace.Id] = BackupConflictResolution.Replace,
                [keep.Id] = BackupConflictResolution.Keep,
                [duplicate.Id] = BackupConflictResolution.Duplicate,
            });

        var duplicatedSession = Assert.Single(target.Sessions.Sessions, session => session.Name == duplicate.Name);
        var loaded = new WorkspaceStore(Path.Combine(target.Directory, "workspaces.json"));
        loaded.Load();
        var workspace = Assert.Single(loaded.Workspaces);
        Assert.Equal(importedWorkspace.Id, workspace.Id);
        Assert.Equal("Imported workspace", workspace.Name);
        Assert.Equal(
            [replace.Id, keptTarget.Id, duplicatedSession.Id, targetOnly.Id],
            workspace.Groups[0].Tabs.Select(tab => tab.SessionId));
        Assert.Equal([true, false, true, false], workspace.Groups[0].Tabs.Select(tab => tab.Pinned));
        Assert.Equal(2, workspace.Groups[0].ActiveTabIndex);
        Assert.Equal(fresh.Id, Assert.Single(workspace.Groups[1].Tabs).SessionId);
        Assert.Equal(duplicatedSession.Id, Assert.Single(loaded.LastLayout!.Groups[0].Tabs).SessionId);
        Assert.True(File.Exists(Path.Combine(target.Directory, "workspaces.json.bak")));
    }

    [Theory]
    [InlineData("{ malformed")]
    [InlineData("{\"workspaces\":[null]}")]
    [InlineData("{\"workspaces\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"Bad\",\"groups\":[null]}]}")]
    [InlineData("{\"workspaces\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"Bad\",\"groups\":[{\"tabs\":[null],\"activeTabIndex\":0}]}]}")]
    public void Import_InvalidWorkspaceStructureFailsBeforeMutationAndDoesNotOverwrite(string payload)
    {
        var source = CreateStores(Path.Combine(_dir.FullName, "malformed-workspace-source"));
        var imported = Ssh("Would import", "would-import.example", "");
        source.Sessions.Add(imported);
        var package = new BackupPackage
        {
            Manifest = new BackupManifest { SchemaVersion = SessionsBackup.CurrentSchemaVersion },
            Sessions = [imported],
            Folders = [],
            LocalFolders = [],
            Settings = new AppSettings(),
            KnownHosts = new Dictionary<string, KnownHostEntry>(),
            Highlights = new HighlightBackupData(),
            Icons = new Dictionary<string, byte[]>(),
            Secrets = new Dictionary<Guid, string>(),
            SshKeys = [],
            KeySecrets = new Dictionary<Guid, string>(),
            Workspaces = Encoding.UTF8.GetBytes(payload),
        };
        var target = CreateStores(Path.Combine(_dir.FullName, "malformed-workspace-target"));
        var workspacePath = Path.Combine(target.Directory, "workspaces.json");
        var workspaceStore = new WorkspaceStore(workspacePath);
        workspaceStore.Load();
        workspaceStore.SaveAs("Existing", new WorkspaceLayout { Groups = [new WorkspaceGroup()] });
        var before = File.ReadAllBytes(workspacePath);

        Assert.Throws<InvalidDataException>(() => SessionsBackup.Import(
            package, target.Directory, target.Sessions, target.Settings, target.KnownHosts,
            target.Highlights, target.SshKeys, target.Credentials,
            new Dictionary<Guid, BackupConflictResolution>()));

        Assert.Empty(target.Sessions.Sessions);
        Assert.Equal(before, File.ReadAllBytes(workspacePath));
    }

    [Fact]
    public void Import_AbsentWorkspacePayloadPreservesExistingFile()
    {
        var source = CreateStores(Path.Combine(_dir.FullName, "no-workspace-source"));
        var package = new BackupPackage
        {
            Manifest = new BackupManifest { SchemaVersion = SessionsBackup.CurrentSchemaVersion },
            Sessions = [],
            Folders = [],
            LocalFolders = [],
            Settings = new AppSettings(),
            KnownHosts = new Dictionary<string, KnownHostEntry>(),
            Highlights = new HighlightBackupData(),
            Icons = new Dictionary<string, byte[]>(),
            Secrets = new Dictionary<Guid, string>(),
            SshKeys = [],
            KeySecrets = new Dictionary<Guid, string>(),
            Workspaces = null,
        };
        var target = CreateStores(Path.Combine(_dir.FullName, "no-workspace-target"));
        var workspacePath = Path.Combine(target.Directory, "workspaces.json");
        var workspaceStore = new WorkspaceStore(workspacePath);
        workspaceStore.Load();
        workspaceStore.SaveAs("Existing", new WorkspaceLayout { Groups = [new WorkspaceGroup()] });
        var before = File.ReadAllBytes(workspacePath);

        SessionsBackup.Import(package, target.Directory, target.Sessions, target.Settings,
            target.KnownHosts, target.Highlights, target.SshKeys, target.Credentials,
            new Dictionary<Guid, BackupConflictResolution>());

        Assert.Equal(before, File.ReadAllBytes(workspacePath));
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private static Session Ssh(string name, string host, string folder) => new()
    {
        Name = name,
        Host = host,
        Username = "root",
        FolderPath = folder,
    };

    private static TestStores CreateStores(string directory)
    {
        Directory.CreateDirectory(directory);
        var sessions = new SessionStore(Path.Combine(directory, "sessions.json"));
        sessions.Load();
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.Load();
        var knownHosts = new KnownHostsStore(Path.Combine(directory, "known_hosts.json"));
        knownHosts.Load();
        var highlights = new HighlightsStore(Path.Combine(directory, "highlights.json"));
        highlights.Load();
        var sshKeys = new SshKeyStore(Path.Combine(directory, "ssh-keys.json"));
        sshKeys.Load();
        return new TestStores(directory, sessions, settings, knownHosts, highlights, sshKeys, new FakeCredentials());
    }

    private sealed record TestStores(
        string Directory,
        SessionStore Sessions,
        SettingsStore Settings,
        KnownHostsStore KnownHosts,
        HighlightsStore Highlights,
        SshKeyStore SshKeys,
        FakeCredentials Credentials);

    private sealed class FakeCredentials : ICredentialService
    {
        private readonly Dictionary<Guid, string> _secrets = [];
        private readonly Dictionary<Guid, string> _keySecrets = [];
        public string? Read(Guid sessionId) => _secrets.GetValueOrDefault(sessionId);
        public void Write(Guid sessionId, string secret) => _secrets[sessionId] = secret;
        public void Delete(Guid sessionId) => _secrets.Remove(sessionId);
        public string? ReadKey(Guid keyId) => _keySecrets.GetValueOrDefault(keyId);
        public void WriteKey(Guid keyId, string secret) => _keySecrets[keyId] = secret;
        public void DeleteKey(Guid keyId) => _keySecrets.Remove(keyId);
    }
}
