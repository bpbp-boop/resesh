using Resesh.Core.Models;
using Resesh.Core.Import;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class SecureCrtImporterTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "SecureCRT");

    private sealed class MockSecureCrtConfigSource(
        string? configPath,
        bool storePersonalDataSeparately = false,
        string? personalDataPath = null) : ISecureCrtConfigSource
    {
        public SecureCrtConfigSettings GetSettings() =>
            new(configPath, storePersonalDataSeparately, personalDataPath);
    }

    [Fact]
    public void ScanDefault_UsesConfiguredRegistryRoot()
    {
        var configDir = Directory.CreateTempSubdirectory("securecrt-config-test");
        try
        {
            var sessionsDir = Directory.CreateDirectory(Path.Combine(configDir.FullName, "Sessions"));
            File.WriteAllText(
                Path.Combine(sessionsDir.FullName, "configured-session.ini"),
                "S:\"Hostname\"=registry.example\n"
                + "S:\"Username\"=admin\n"
                + "S:\"Protocol Name\"=SSH2\n"
                + "D:\"[SSH2] Port\"=00000016");
            var source = new MockSecureCrtConfigSource(configDir.FullName);

            var paths = SecureCrtImporter.GetSessionPaths(source);
            Assert.Equal(sessionsDir.FullName, paths.ConfigSessionsPath);
            Assert.Null(paths.PersonalSessionsPath);
            var candidate = Assert.Single(SecureCrtImporter.ScanDefault(source).Importable);
            Assert.Equal("configured-session", candidate.Name);
            Assert.Equal("registry.example", candidate.Host);
        }
        finally
        {
            configDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ScanDefault_OverlaysUsernameOnlyWhenPersonalDataIsEnabled()
    {
        var root = Directory.CreateTempSubdirectory("securecrt-personal-test");
        try
        {
            var configPath = Directory.CreateDirectory(Path.Combine(root.FullName, "Config"));
            var configSessions = Directory.CreateDirectory(Path.Combine(configPath.FullName, "Sessions", "Team"));
            File.WriteAllText(
                Path.Combine(configSessions.FullName, "router.ini"),
                "S:\"Hostname\"=router.example\n"
                + "S:\"Username\"=shared-user\n"
                + "S:\"Protocol Name\"=SSH2");

            var personalPath = Directory.CreateDirectory(Path.Combine(root.FullName, "ConfigPersonal"));
            var personalSessions = Directory.CreateDirectory(
                Path.Combine(personalPath.FullName, "Sessions", "Team"));
            File.WriteAllText(
                Path.Combine(personalSessions.FullName, "router.ini"),
                "S:\"Username\"=personal-user\n"
                + "S:\"Password V2\"=encrypted-value");

            var enabledSource = new MockSecureCrtConfigSource(
                configPath.FullName,
                storePersonalDataSeparately: true,
                personalDataPath: personalPath.FullName);
            var paths = SecureCrtImporter.GetSessionPaths(enabledSource);
            Assert.Equal(Path.Combine(personalPath.FullName, "Sessions"), paths.PersonalSessionsPath);

            var candidate = Assert.Single(SecureCrtImporter.ScanDefault(enabledSource).Importable);
            Assert.Equal("router.example", candidate.Host);
            Assert.Equal("personal-user", candidate.Username);

            var disabledSource = new MockSecureCrtConfigSource(
                configPath.FullName,
                storePersonalDataSeparately: false,
                personalDataPath: personalPath.FullName);
            var candidateWithoutOverlay = Assert.Single(
                SecureCrtImporter.ScanDefault(disabledSource).Importable);
            Assert.Equal("shared-user", candidateWithoutOverlay.Username);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Scan_FindsSshSessionsAndMirrorsFolders()
    {
        var result = SecureCrtImporter.Scan(FixtureDir);

        Assert.Equal(5, result.Importable.Count); // 3× SSH2 + 1× SSH1 + 1× Telnet

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
    public void Scan_ImportsTelnet_AndListsSerialAsSkipped()
    {
        var result = SecureCrtImporter.Scan(FixtureDir);
        var telnet = result.Importable.Single(c => c.IsTelnet);
        Assert.Equal("ancient-telnet", telnet.Name);
        Assert.Equal("Legacy", telnet.FolderPath);
        Assert.Equal("172.16.0.20", telnet.Host);
        Assert.Equal(23, telnet.Port); // D:"Port"=0x17
        Assert.Equal("SERIAL", Assert.Single(result.Skipped).Protocol);
    }

    [Fact]
    public void Parse_TelnetSessionWithLeftoverSshPort_UsesTheTelnetPort()
    {
        // Real SecureCRT files keep "[SSH2] Port" after a session is switched to telnet,
        // and it can come first.
        var candidate = SecureCrtImporter.Parse(
            "D:\"[SSH2] Port\"=00000016\nD:\"Port\"=00000017\nS:\"Protocol Name\"=Telnet\nS:\"Hostname\"=mgmt1",
            "mgmt1", "", "mgmt1.ini");
        Assert.True(candidate.IsTelnet);
        Assert.Equal(23, candidate.Port);
    }

    [Fact]
    public void Parse_SshSessionWithLeftoverTelnetPort_UsesTheSshPort()
    {
        var candidate = SecureCrtImporter.Parse(
            "D:\"Port\"=00000017\nS:\"Protocol Name\"=SSH2\nD:\"[SSH2] Port\"=000008AE",
            "x", "", "x.ini");
        Assert.Equal(2222, candidate.Port);
    }

    [Theory]
    [InlineData("Telnet", true, 23)]
    [InlineData("Telnet/SSL", false, 22)] // TLS-wrapped telnet is not something we speak
    [InlineData("RLogin", false, 22)]
    public void Parse_TelnetWithoutAPortKey_DefaultsTo23(string protocolName, bool supported, int port)
    {
        var candidate = SecureCrtImporter.Parse(
            $"S:\"Hostname\"=ts1\nS:\"Username\"=ignored\nS:\"Protocol Name\"={protocolName}", "ts1", "", "ts1.ini");
        Assert.Equal(supported, candidate.IsSupported);
        Assert.Equal(port, candidate.Port);
        if (supported)
            Assert.Equal("", candidate.Username);
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
            Assert.Equal(5, imported);
            Assert.Equal(0, duplicates);
            Assert.All(store.Sessions.Where(s => !s.IsTelnet), s => Assert.True(s.CredentialNeeded));

            // Telnet has no credential to capture: the server prompts in the terminal.
            var telnet = store.Sessions.Single(s => s.IsTelnet);
            Assert.Equal(SessionKind.Telnet, telnet.Kind);
            Assert.Equal(("172.16.0.20", 23, "Legacy"), (telnet.Host, telnet.Port, telnet.FolderPath));
            Assert.Equal(AuthMethod.None, telnet.AuthMethod);
            Assert.False(telnet.CredentialNeeded);
            Assert.Equal("", telnet.Username);

            var (reimported, reDuplicates) = SecureCrtImporter.Commit(store, scan.Importable);
            Assert.Equal(0, reimported);
            Assert.Equal(5, reDuplicates);
            Assert.Equal(5, store.Sessions.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
