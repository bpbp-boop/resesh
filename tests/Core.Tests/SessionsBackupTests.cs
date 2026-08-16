using System.Text;
using System.IO.Compression;
using Sessions.Core.Backup;
using Sessions.Core.Credentials;
using Sessions.Core.Models;
using Sessions.Core.Ssh;
using Sessions.Core.Storage;

namespace Sessions.Core.Tests;

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

        var path = Path.Combine(_dir.FullName, "filtered.sessionsbackup");
        SessionsBackup.Export(path, source.Directory, source.Sessions, source.Settings,
            source.KnownHosts, source.Highlights, source.Credentials,
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
        var path = Path.Combine(_dir.FullName, "secret.sessionsbackup");

        SessionsBackup.Export(path, source.Directory, source.Sessions, source.Settings,
            source.KnownHosts, source.Highlights, source.Credentials,
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

        var path = Path.Combine(_dir.FullName, "merge.sessionsbackup");
        SessionsBackup.Export(path, source.Directory, source.Sessions, source.Settings,
            source.KnownHosts, source.Highlights, source.Credentials,
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
            target.KnownHosts, target.Highlights, target.Credentials,
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
        return new TestStores(directory, sessions, settings, knownHosts, highlights, new FakeCredentials());
    }

    private sealed record TestStores(
        string Directory,
        SessionStore Sessions,
        SettingsStore Settings,
        KnownHostsStore KnownHosts,
        HighlightsStore Highlights,
        FakeCredentials Credentials);

    private sealed class FakeCredentials : ICredentialService
    {
        private readonly Dictionary<Guid, string> _secrets = [];
        public string? Read(Guid sessionId) => _secrets.GetValueOrDefault(sessionId);
        public void Write(Guid sessionId, string secret) => _secrets[sessionId] = secret;
        public void Delete(Guid sessionId) => _secrets.Remove(sessionId);
    }
}
