using Resesh.Core.Import;

namespace Resesh.Core.Tests;

public sealed class OpenSshConfigImporterTests
{
    [Fact]
    public void Parse_ImportsConcreteHostsWithUserPortAndHostname()
    {
        const string config = """
            Host prod
                HostName prod.example.com
                User deploy
                Port 2222

            Host staging backup
                User ops
            """;

        var result = OpenSshConfigImporter.Parse(config);

        Assert.Equal(3, result.Importable.Count);
        var prod = result.Importable.Single(candidate => candidate.Name == "prod");
        Assert.Equal("prod.example.com", prod.Host);
        Assert.Equal("deploy", prod.Username);
        Assert.Equal(2222, prod.Port);

        var staging = result.Importable.Single(candidate => candidate.Name == "staging");
        Assert.Equal("staging", staging.Host);
        Assert.Equal("ops", staging.Username);
        Assert.Equal(22, staging.Port);
    }

    [Fact]
    public void Parse_ImportsAlgorithmOptions_FirstValueWins()
    {
        const string config = """
            Host old-switch
                HostName 10.0.0.2
                KexAlgorithms +diffie-hellman-group1-sha1
                HostKeyAlgorithms=+ssh-rsa
                Ciphers aes128-cbc,3des-cbc

            Host *
                Ciphers aes256-ctr
                MACs hmac-sha1

            Host modern
            """;

        var result = OpenSshConfigImporter.Parse(config);

        var old = result.Importable.Single(candidate => candidate.Name == "old-switch").Algorithms!;
        Assert.Empty(old.DisabledKeyExchange!);
        Assert.Empty(old.DisabledHostKeys!);
        Assert.Equal(["aes128-cbc", "3des-cbc"], Ssh.SshAlgorithmPolicy.Offered(Ssh.SshAlgorithmKind.Cipher, old, null));
        Assert.Equal(["hmac-sha1"], Ssh.SshAlgorithmPolicy.Offered(Ssh.SshAlgorithmKind.Mac, old, null));
        var modern = result.Importable.Single(candidate => candidate.Name == "modern").Algorithms!;
        Assert.Equal(["aes256-ctr"], Ssh.SshAlgorithmPolicy.Offered(Ssh.SshAlgorithmKind.Cipher, modern, null));
        Assert.Null(modern.DisabledKeyExchange);
    }

    [Fact]
    public void Parse_SkipsWildcardAndNegatedHostPatterns()
    {
        const string config = """
            Host *
                User default-user
            Host !blocked *.internal concrete
                HostName gateway.example.com
            """;

        var result = OpenSshConfigImporter.Parse(config);

        var candidate = Assert.Single(result.Importable);
        Assert.Equal("concrete", candidate.Name);
        Assert.Equal("gateway.example.com", candidate.Host);
    }

    [Fact]
    public void Parse_UsesFirstDirectiveAndHandlesQuotesAndComments()
    {
        const string config = """
            Host "lab box" # visible name
                HostName "10.0.0.8"
                HostName ignored.example.com
                User "lab-user"
                Port 2200
            """;

        var candidate = Assert.Single(OpenSshConfigImporter.Parse(config).Importable);

        Assert.Equal("lab box", candidate.Name);
        Assert.Equal("10.0.0.8", candidate.Host);
        Assert.Equal("lab-user", candidate.Username);
        Assert.Equal(2200, candidate.Port);
    }

    [Fact]
    public void Scan_MissingFileReturnsNoCandidates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-ssh-config-{Guid.NewGuid():N}");

        var result = OpenSshConfigImporter.Scan(path);

        Assert.Empty(result.Importable);
        Assert.Empty(result.Skipped);
    }
}
