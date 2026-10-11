using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
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

    private static byte[] HostKey(string type)
    {
        var name = Encoding.ASCII.GetBytes(type);
        var blob = new byte[4 + name.Length + 32];
        BinaryPrimitives.WriteUInt32BigEndian(blob, (uint)name.Length);
        name.CopyTo(blob, 4);
        return blob;
    }

    private static string Fingerprint(byte[] blob) =>
        Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    [Fact]
    public void Accept_PublicKey_PersistsAcrossReload()
    {
        var blob = HostKey("ssh-rsa");
        var fingerprint = Fingerprint(blob);
        NewStore().Accept("host", 22, "rsa-sha2-512", fingerprint, blob);

        var entry = NewStore().Lookup("host", 22)!;
        Assert.Equal(fingerprint, entry.Sha256);
        Assert.Equal(Convert.ToBase64String(blob), entry.PublicKeyBase64);
    }

    [Fact]
    public void Load_LegacyFingerprintOnlyEntry_BackfillsMatchingKeyAndPersists()
    {
        var blob = HostKey("ssh-ed25519");
        var fingerprint = Fingerprint(blob);
        File.WriteAllText(Path.Combine(_dir, "known_hosts.json"),
            $$$"""{"host:22":{"KeyType":"ssh-ed25519","Sha256":"{{{fingerprint}}}"}}""");
        var store = NewStore();
        Assert.Null(store.Lookup("host", 22)!.PublicKeyBase64);
        Assert.Equal(HostKeyVerdict.Match, store.Check("host", 22, "ssh-ed25519", fingerprint));

        Assert.True(store.BackfillHostKey("HOST", 22, "ssh-ed25519", blob));
        Assert.Equal(Convert.ToBase64String(blob), NewStore().Lookup("host", 22)!.PublicKeyBase64);
        Assert.False(store.BackfillHostKey("host", 22, "ssh-ed25519", blob));
    }

    [Fact]
    public void Backfill_UnknownOrDifferentKey_DoesNotChangeTrust()
    {
        var blob = HostKey("ssh-rsa");
        var store = NewStore();
        Assert.False(store.BackfillHostKey("unknown", 22, "ssh-rsa", blob));
        store.Accept("host", 22, "ssh-rsa", "different");
        Assert.False(store.BackfillHostKey("host", 22, "ssh-rsa", blob));
        Assert.Equal(new KnownHostEntry("ssh-rsa", "different"), NewStore().Lookup("host", 22));
        store.Accept("host", 22, "ssh-ed25519", Fingerprint(blob));
        Assert.False(store.BackfillHostKey("host", 22, "ssh-rsa", blob));
        Assert.Null(store.Lookup("host", 22)!.PublicKeyBase64);
    }

    [Fact]
    public void Accept_PublicKeyWithDifferentFingerprint_RejectsWithoutChangingTrust()
    {
        var store = NewStore();
        store.Accept("host", 22, "ssh-rsa", "old");
        Assert.Throws<ArgumentException>(() => store.Accept("host", 22, "ssh-rsa", "other", HostKey("ssh-rsa")));
        Assert.Equal(new KnownHostEntry("ssh-rsa", "old"), store.Lookup("host", 22));
    }

    [Theory]
    [InlineData("rsa-sha2-512", "ssh-rsa", 22, "host")]
    [InlineData("rsa-sha2-256", "ssh-rsa", 2200, "[host]:2200")]
    [InlineData("ssh-ed25519", "ssh-ed25519", 22, "host")]
    [InlineData("ecdsa-sha2-nistp256", "ecdsa-sha2-nistp256", 2222, "[host]:2222")]
    public void KnownHostsLine_UsesBlobKeyTypeAndOpenSshPortFormat(
        string signatureAlgorithm, string keyType, int port, string endpoint)
    {
        var blob = HostKey(keyType);
        var encoded = Convert.ToBase64String(blob);
        var entry = new KnownHostEntry(signatureAlgorithm, Fingerprint(blob), encoded);
        Assert.Equal($"{endpoint} {keyType} {encoded}", entry.ToKnownHostsLine("host", port));
    }

    [Fact]
    public void KnownHostsLine_MissingKey_RequestsTerminalConnection()
    {
        var error = Assert.Throws<IOException>(() => new KnownHostEntry("ssh-rsa", "old").ToKnownHostsLine("host", 22));
        Assert.Contains("Connect the terminal first", error.Message);
    }

    [Fact]
    public void KnownHostsLine_ImportedBlobCannotAuthorizeDifferentFingerprint()
    {
        var entry = new KnownHostEntry("ssh-rsa", "trusted", Convert.ToBase64String(HostKey("ssh-rsa")));
        Assert.Throws<IOException>(() => entry.ToKnownHostsLine("host", 22));
    }

    [Fact]
    public void KnownHostsLine_InvalidSshString_Rejects()
    {
        var blob = new byte[] { 255, 255, 255, 255 };
        var entry = new KnownHostEntry("ssh-rsa", Fingerprint(blob), Convert.ToBase64String(blob));
        Assert.Throws<IOException>(() => entry.ToKnownHostsLine("host", 22));
    }

    [Fact]
    public void Merge_OldAndNewEntriesPreserveLocalTrustAndExportPublicKey()
    {
        var blob = HostKey("ssh-rsa");
        var imported = new KnownHostEntry("rsa-sha2-512", Fingerprint(blob), Convert.ToBase64String(blob));
        var store = NewStore();
        store.Accept("local", 22, "ssh-ed25519", "local-fingerprint");
        Assert.Equal(2, store.Merge(new Dictionary<string, KnownHostEntry>
        {
            ["local:22"] = imported,
            ["new:22"] = imported,
            ["legacy:22"] = new("ssh-ed25519", "legacy-fingerprint"),
        }));
        var reloaded = NewStore();
        Assert.Equal(new KnownHostEntry("ssh-ed25519", "local-fingerprint"), reloaded.Lookup("local", 22));
        Assert.Equal(imported, reloaded.Lookup("new", 22));
        Assert.Null(reloaded.Lookup("legacy", 22)!.PublicKeyBase64);
        Assert.Equal(imported, reloaded.Entries["new:22"]);
    }
}
