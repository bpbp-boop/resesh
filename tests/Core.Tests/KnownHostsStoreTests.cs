using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class KnownHostsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("known-hosts-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private KnownHostsStore NewStore()
    {
        var store = new KnownHostsStore(Path.Combine(_dir, "known_hosts.json"));
        store.Load();
        return store;
    }

    [Fact]
    public void Check_UnknownHost_ReturnsUnknown()
    {
        Assert.Equal(HostKeyVerdict.Unknown, NewStore().Check("host", 22, "ssh-ed25519", "abc"));
    }

    [Fact]
    public void Accept_ThenCheck_Matches_AndPersists()
    {
        NewStore().Accept("host", 22, "ssh-ed25519", "abc");

        var reloaded = NewStore();
        Assert.Equal(HostKeyVerdict.Match, reloaded.Check("host", 22, "ssh-ed25519", "abc"));
        Assert.Equal(HostKeyVerdict.Match, reloaded.Check("HOST", 22, "ssh-ed25519", "abc"));
        Assert.Equal(HostKeyVerdict.Mismatch, reloaded.Check("host", 22, "ssh-ed25519", "OTHER"));
    }

    [Fact]
    public void Accept_ChangedKey_OverwritesPreviousEntry()
    {
        var store = NewStore();
        store.Accept("host", 22, "ssh-rsa", "old");

        // The mismatch-override flow re-Accepts with the new key; the old entry must be replaced.
        store.Accept("host", 22, "ssh-ed25519", "new");

        var reloaded = NewStore();
        Assert.Equal(HostKeyVerdict.Match, reloaded.Check("host", 22, "ssh-ed25519", "new"));
        Assert.Equal(HostKeyVerdict.Mismatch, reloaded.Check("host", 22, "ssh-rsa", "old"));
        Assert.Equal(new KnownHostEntry("ssh-ed25519", "new"), reloaded.Lookup("host", 22));
    }
}
