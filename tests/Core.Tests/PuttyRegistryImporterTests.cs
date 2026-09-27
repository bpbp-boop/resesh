using Resesh.Core.Import;

namespace Resesh.Core.Tests;

public sealed class PuttyRegistryImporterTests
{
    private sealed class MockPuttySource : IPuttyRegistrySource
    {
        private readonly Dictionary<string, Dictionary<string, object?>> _sessions = new();

        public void AddSession(string encodedName, Dictionary<string, object?> values)
        {
            _sessions[encodedName] = values;
        }

        public IEnumerable<string> GetSessionNames() => _sessions.Keys;

        public IReadOnlyDictionary<string, object?>? GetSessionValues(string sessionName) =>
            _sessions.TryGetValue(sessionName, out var values) ? values : null;
    }

    [Fact]
    public void Scan_DecodesPercentEncodedNamesAndExtractsSshSessions()
    {
        var mock = new MockPuttySource();
        mock.AddSession("Default%20Settings", new()
        {
            ["HostName"] = "default.example.com",
            ["PortNumber"] = 22,
            ["Protocol"] = "ssh",
        });
        mock.AddSession("production%20server", new()
        {
            ["HostName"] = "prod.internal",
            ["PortNumber"] = 2200,
            ["UserName"] = "deployer",
            ["Protocol"] = "ssh",
        });
        mock.AddSession("router%2Dcore", new()
        {
            ["HostName"] = "admin@10.0.0.1",
            ["PortNumber"] = 22,
            ["Protocol"] = "ssh",
        });
        mock.AddSession("legacy%20switch", new()
        {
            ["HostName"] = "10.0.0.254",
            ["PortNumber"] = 23,
            ["Protocol"] = "telnet",
        });

        var result = PuttyRegistryImporter.Scan(mock);

        // Default%20Settings must be skipped; telnet is importable
        Assert.Equal(3, result.Importable.Count);
        Assert.Empty(result.Skipped);

        var prod = result.Importable.Single(s => s.Name == "production server");
        Assert.Equal("prod.internal", prod.Host);
        Assert.Equal(2200, prod.Port);
        Assert.Equal("deployer", prod.Username);
        Assert.True(prod.IsSupported);

        var router = result.Importable.Single(s => s.Name == "router-core");
        Assert.Equal("10.0.0.1", router.Host);
        Assert.Equal("admin", router.Username);
        Assert.Equal(22, router.Port);

        var telnet = result.Importable.Single(s => s.IsTelnet);
        Assert.Equal("legacy switch", telnet.Name);
        Assert.Equal("10.0.0.254", telnet.Host);
        Assert.Equal(23, telnet.Port);
        Assert.Equal("", telnet.Username); // not the Windows user name SSH falls back to
    }

    [Fact]
    public void ParseSession_TelnetWithoutPort_DefaultsTo23_AndOtherProtocolsStaySkipped()
    {
        var telnet = PuttyRegistryImporter.ParseSession("ts", new Dictionary<string, object?>
        {
            ["HostName"] = "ts1.lab",
            ["Protocol"] = "telnet",
            ["PublicKeyFile"] = @"C:\keys\id.ppk",
        });
        Assert.True(telnet.IsSupported);
        Assert.Equal(23, telnet.Port);
        Assert.Null(telnet.PrivateKeyPath);

        foreach (var protocol in new[] { "serial", "raw", "rlogin" })
            Assert.False(PuttyRegistryImporter.ParseSession("x", new Dictionary<string, object?>
            {
                ["HostName"] = "h",
                ["Protocol"] = protocol,
            }).IsSupported);
    }

    [Fact]
    public void DecodeSessionName_HandlesSpecialCharacters()
    {
        Assert.Equal("My Web Server", PuttyRegistryImporter.DecodeSessionName("My%20Web%20Server"));
        Assert.Equal("Server (Dev)", PuttyRegistryImporter.DecodeSessionName("Server%20%28Dev%29"));
        Assert.Equal("Lab+Testing", PuttyRegistryImporter.DecodeSessionName("Lab%2BTesting"));
    }
}
