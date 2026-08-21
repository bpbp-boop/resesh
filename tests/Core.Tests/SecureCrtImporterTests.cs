using Resesh.Core.Import;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class SecureCrtImporterTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "SecureCRT");

    [Fact]
    public void Scan_FindsSshSessionsAndMirrorsFolders()
    {
        var result = SecureCrtImporter.Scan(FixtureDir);

        Assert.Equal(4, result.Importable.Count); // 3× SSH2 + 1× SSH1

        var coreSw = result.Importable.Single(c => c.Name == "core-sw-3");
        Assert.Equal("Datacenter/Rack 4", coreSw.FolderPath);
        Assert.Equal("192.168.1.3", coreSw.Host);
        Assert.Equal(2222, coreSw.Port); // 0x8AE
        Assert.Equal("netops", coreSw.Username);

        var prodWeb = result.Importable.Single(c => c.Name == "prod-web-01");
        Assert.Equal("", prodWeb.FolderPath);
        Assert.Equal(22, prodWeb.Port); // no port key → default
    }

    [Fact]
    public void Scan_HandlesWeirdCharactersInNames()
    {
        var result = SecureCrtImporter.Scan(FixtureDir);
        var weird = result.Importable.Single(c => c.Name == "edge-rtr-1 (mgmt) & backup");
        Assert.Equal(22, weird.Port); // 0x16
        Assert.Equal("192.168.1.1", weird.Host);
    }

    [Fact]
    public void Scan_ParsesSsh1WithItsOwnPortKey()
    {
        var result = SecureCrtImporter.Scan(FixtureDir);
        var ssh1 = result.Importable.Single(c => c.Protocol == "SSH1");
        Assert.Equal("old-ssh1-box", ssh1.Name);
        Assert.Equal(886, ssh1.Port); // 0x376
    }

    [Fact]
    public void Scan_ListsTelnetAndSerialAsSkipped()
    {
        var result = SecureCrtImporter.Scan(FixtureDir);
        Assert.Equal(2, result.Skipped.Count);
        Assert.Contains(result.Skipped, c => c.Protocol == "TELNET");
        Assert.Contains(result.Skipped, c => c.Protocol == "SERIAL");
    }

    [Fact]
    public void Scan_IgnoresFolderDataAndDefaultIni()
    {
        var result = SecureCrtImporter.Scan(FixtureDir);
        var all = result.Importable.Concat(result.Skipped).Select(c => c.Name);
        Assert.DoesNotContain("__FolderData__", all);
        Assert.DoesNotContain("Default", all);
    }

    [Fact]
    public void Parse_IgnoresUnknownAndMalformedLines()
    {
        var candidate = SecureCrtImporter.Parse(
            "garbage line\nS:\"Hostname\"=1.2.3.4\nZ9 nonsense == \nD:\"[SSH2] Port\"=zzzz\nS:\"Protocol Name\"=SSH2",
            "x", "", "x.ini");
        Assert.Equal("1.2.3.4", candidate.Host);
        Assert.Equal(22, candidate.Port); // invalid hex ignored
        Assert.True(candidate.IsSupported);
    }

    [Fact]
    public void Commit_MarksCredentialNeededAndSkipsDuplicatesOnReimport()
    {
        var dir = Directory.CreateTempSubdirectory("import-commit-test");
        try
        {
            var store = new SessionStore(Path.Combine(dir.FullName, "sessions.json"));
            store.Load();
            var scan = SecureCrtImporter.Scan(FixtureDir);

            var (imported, duplicates) = SecureCrtImporter.Commit(store, scan.Importable);
            Assert.Equal(4, imported);
            Assert.Equal(0, duplicates);
            Assert.All(store.Sessions, s => Assert.True(s.CredentialNeeded));

            var (reimported, reDuplicates) = SecureCrtImporter.Commit(store, scan.Importable);
            Assert.Equal(0, reimported);
            Assert.Equal(4, reDuplicates);
            Assert.Equal(4, store.Sessions.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
