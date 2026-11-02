using Resesh.Core.Models;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

/// <summary>
/// Every JSON store must refuse to save over a file it could not read: a sharing violation
/// (antivirus, OneDrive) used to leave the store empty, and its first two saves rotated the
/// user's file into .bak and then over it.
/// </summary>
public sealed class StoreLoadStateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("store-load-state-tests").FullName;
    private string StorePath => Path.Combine(_dir, "store.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string> Stores => ["sessions", "settings", "workspaces", "highlights", "identities", "sshKeys", "knownHosts"];

    /// <summary>A store behind one interface: load it, change it (which saves), read its state.</summary>
    private sealed class Harness(Action load, Action mutate, Func<StoreLoadState> state, Func<string?> warning)
    {
        public void Load() => load();
        public void Mutate() => mutate();
        public StoreLoadState State => state();
        public string? Warning => warning();
    }

    private static Harness Open(string kind, string path)
    {
        var n = 0;
        switch (kind)
        {
            case "sessions":
            {
                var store = new SessionStore(path);
                return new(store.Load,
                    () => store.Add(new Session { Name = $"s{Guid.NewGuid()}", Host = "h", Username = "u", FolderPath = "" }),
                    () => store.LoadState, () => store.LoadWarning);
            }
            case "settings":
            {
                var store = new SettingsStore(path);
                return new(store.Load, () => store.RecordRecentSession(Guid.NewGuid()),
                    () => store.LoadState, () => store.LoadWarning);
            }
            case "workspaces":
            {
                var store = new WorkspaceStore(path);
                return new(store.Load, () => store.SaveAs($"w{Guid.NewGuid()}", new WorkspaceLayout()),
                    () => store.LoadState, () => store.LoadWarning);
            }
            case "highlights":
            {
                var store = new HighlightsStore(path);
                var rule = BuiltinHighlights.Rules[0];
                return new(store.Load, () => store.SetEnabled(rule.Id, n++ % 2 == 0 ? !rule.Enabled : rule.Enabled),
                    () => store.LoadState, () => store.LoadWarning);
            }
            case "identities":
            {
                var store = new IdentityStore(path);
                return new(store.Load, () => store.Add(new Identity { Name = $"i{Guid.NewGuid()}", Username = "u" }),
                    () => store.LoadState, () => store.LoadWarning);
            }
            case "sshKeys":
            {
                var store = new SshKeyStore(path);
                return new(store.Load,
                    () => store.RegisterExternal(Path.Combine(Path.GetDirectoryName(path)!, $"key{Guid.NewGuid()}"), allowMissing: true),
                    () => store.LoadState, () => store.LoadWarning);
            }
            case "knownHosts":
            {
                var store = new KnownHostsStore(path);
                return new(store.Load, () => store.Accept($"host{n++}", 22, "ssh-ed25519", "abc"),
                    () => store.LoadState, () => store.LoadWarning ?? store.LoadError);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>Writes a valid file through the store itself; two saves leave a .bak too.</summary>
    private string Seed(string kind, int saves = 1)
    {
        var store = Open(kind, StorePath);
        store.Load();
        for (var i = 0; i < saves; i++)
            store.Mutate();
        return File.ReadAllText(StorePath);
    }

    private static FileStream Lock(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    [Theory]
    [MemberData(nameof(Stores))]
    public void MissingFile_LoadsEmptyAndSaves(string kind)
    {
        var store = Open(kind, StorePath);
        store.Load();

        Assert.Equal(StoreLoadState.Loaded, store.State);
        Assert.Null(store.Warning);
        store.Mutate();
        Assert.True(File.Exists(StorePath));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void LockedFile_IsReadOnlyAndSurvivesEverySaveAttempt(string kind)
    {
        var original = Seed(kind);
        var store = Open(kind, StorePath);
        using (Lock(StorePath))
        {
            store.Load();

            Assert.Equal(StoreLoadState.Unreadable, store.State);
            Assert.NotNull(store.Warning);
            // Two saves used to rotate the file into .bak and then over it.
            Assert.ThrowsAny<IOException>(store.Mutate);
            Assert.ThrowsAny<IOException>(store.Mutate);
        }
        // Still refused once the lock is gone: the store holds no copy of the file's data.
        if (kind != "knownHosts")
            Assert.IsType<StoreReadOnlyException>(Record.Exception(store.Mutate));

        Assert.Equal(original, File.ReadAllText(StorePath));
        Assert.False(File.Exists(StorePath + ".bak"));
        var reloaded = Open(kind, StorePath);
        reloaded.Load();
        Assert.Equal(StoreLoadState.Loaded, reloaded.State);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void LockedFileWithGoodBackup_ShowsTheBackupButKeepsTheNewerFile(string kind)
    {
        var primary = Seed(kind, saves: 2);
        var backup = File.ReadAllText(StorePath + ".bak");
        var store = Open(kind, StorePath);
        using (Lock(StorePath))
            store.Load();

        Assert.Equal(StoreLoadState.Unreadable, store.State);
        Assert.NotNull(store.Warning);
        Assert.IsType<StoreReadOnlyException>(Record.Exception(store.Mutate));
        Assert.Equal(primary, File.ReadAllText(StorePath));
        Assert.Equal(backup, File.ReadAllText(StorePath + ".bak"));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void CorruptFile_IsKeptAsideAndSavesProceed(string kind)
    {
        Seed(kind);
        File.WriteAllText(StorePath, "{ truncated");
        var store = Open(kind, StorePath);
        store.Load();

        if (kind == "knownHosts")
        {
            // No good backup: SSH is blocked rather than every host turning "unknown".
            Assert.Equal(StoreLoadState.Unreadable, store.State);
            Assert.ThrowsAny<IOException>(store.Mutate);
            return;
        }
        Assert.Equal(StoreLoadState.Loaded, store.State);
        Assert.Contains("could not be loaded", store.Warning);
        store.Mutate();
        store.Mutate();
        var kept = Assert.Single(Directory.GetFiles(_dir, "store.json.unreadable-*"));
        Assert.Equal("{ truncated", File.ReadAllText(kept));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void CorruptFileWithGoodBackup_RecoversThenSavesNormally(string kind)
    {
        Seed(kind, saves: 2);
        var backup = File.ReadAllText(StorePath + ".bak");
        File.WriteAllText(StorePath, "{ truncated");
        var store = Open(kind, StorePath);
        store.Load();

        Assert.Equal(StoreLoadState.RecoveredFromBackup, store.State);
        Assert.Contains("recovered from the backup", store.Warning);
        store.Mutate();
        Assert.Equal(StoreLoadState.Loaded, store.State);
        // The first save replaced the corrupt primary without rotating it over the good backup.
        Assert.Equal(backup, File.ReadAllText(StorePath + ".bak"));
        Assert.Single(Directory.GetFiles(_dir, "store.json.unreadable-*"));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void SaveBeforeLoad_IsRefusedWhenAFileExists(string kind)
    {
        var original = Seed(kind);
        var store = Open(kind, StorePath);

        Assert.Equal(StoreLoadState.NotLoaded, store.State);
        Assert.Throws<InvalidOperationException>(store.Mutate);
        Assert.Equal(original, File.ReadAllText(StorePath));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void SaveBeforeLoad_IsAllowedWithNoFile(string kind)
    {
        var store = Open(kind, StorePath);
        store.Mutate();
        Assert.Equal(StoreLoadState.Loaded, store.State);
        Assert.True(File.Exists(StorePath));
    }

    [Fact]
    public void LockedBackupWithMissingPrimary_IsReadOnly()
    {
        Seed("sessions", saves: 2);
        File.Delete(StorePath);
        var backup = File.ReadAllText(StorePath + ".bak");
        var store = Open("sessions", StorePath);
        using (Lock(StorePath + ".bak"))
            store.Load();

        Assert.Equal(StoreLoadState.Unreadable, store.State);
        Assert.Throws<StoreReadOnlyException>(store.Mutate);
        Assert.Throws<StoreReadOnlyException>(store.Mutate);
        Assert.Equal(backup, File.ReadAllText(StorePath + ".bak"));
    }

    [Fact]
    public void BriefSharingViolation_IsRetried()
    {
        var original = Seed("sessions");
        var store = Open("sessions", StorePath);
        var held = Lock(StorePath);
        var retries = 0;
        // Let go when the first retry is about to wait, as a scanner would, rather than on a
        // timer that a busy machine can run late.
        AtomicFile.BeforeSharingViolationRetry = () =>
        {
            retries++;
            held.Dispose();
        };
        try
        {
            store.Load();
        }
        finally
        {
            AtomicFile.BeforeSharingViolationRetry = null;
            held.Dispose();
        }

        Assert.Equal(1, retries);
        Assert.Equal(StoreLoadState.Loaded, store.State);
        Assert.Null(store.Warning);
        store.Mutate();
        Assert.Equal(original, File.ReadAllText(StorePath + ".bak"));
    }

    [Fact]
    public void LockedSettings_KeepDefaultsAndExplainWhy()
    {
        Seed("settings");
        var store = new SettingsStore(StorePath);
        using (Lock(StorePath))
            store.Load();

        Assert.Equal(StoreLoadState.Unreadable, store.LoadState);
        Assert.Contains("Default settings are in use", store.LoadWarning);
        Assert.Contains("will not be saved", store.LoadWarning);
    }

    [Fact]
    public void KnownHosts_LockedPrimaryWithBackup_StillVerifiesHosts()
    {
        var store = new KnownHostsStore(StorePath);
        store.Load();
        store.Accept("a", 22, "ssh-ed25519", "aaa");
        store.Accept("b", 22, "ssh-ed25519", "bbb");

        var reloaded = new KnownHostsStore(StorePath);
        using (Lock(StorePath))
            reloaded.Load();

        Assert.Null(reloaded.LoadError);
        Assert.Equal(HostKeyVerdict.Match, reloaded.Check("a", 22, "ssh-ed25519", "aaa"));
        Assert.Throws<StoreReadOnlyException>(() => reloaded.Accept("c", 22, "ssh-ed25519", "ccc"));
        Assert.Contains("\"b:22\"", File.ReadAllText(StorePath));
    }

    [Fact]
    public void KnownHosts_CheckBeforeLoad_IsRefusedWhenAFileExists()
    {
        Seed("knownHosts");
        var store = new KnownHostsStore(StorePath);
        Assert.Throws<InvalidOperationException>(() => store.Check("host0", 22, "ssh-ed25519", "abc"));
    }
}
