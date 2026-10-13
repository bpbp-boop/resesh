using System.Security.Cryptography;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class AgentInstallTests
{
    private const string Version = "0.8.1-resesh.1";
    private static readonly string Sha = new('a', 64);
    private static readonly AgentBinary X64 = new("x86_64/resesh-agent", Sha, 1809936);
    private static readonly AgentPackage Package = new(Version, new Dictionary<string, AgentBinary> { ["x86_64"] = X64 });

    [Theory]
    [InlineData("Linux", "x86_64", "x86_64")]
    [InlineData("Linux", "amd64", "x86_64")]
    [InlineData("Linux", "aarch64", "aarch64")]
    [InlineData("Linux", "arm64", "aarch64")]
    [InlineData("Linux", "armv7l", null)]
    [InlineData("Darwin", "arm64", null)]
    [InlineData("FreeBSD", "amd64", null)]
    public void PicksTheBuildFromUname(string os, string machine, string? expected) =>
        Assert.Equal(expected, AgentInstall.Architecture(os, machine));

    [Fact]
    public void ProbeLineIsFoundAmongOtherOutput()
    {
        var output = "Authorized users only.\r\n"
            + $"resesh-agent-probe os=Linux machine=x86_64 hash={Sha} runs=1\r\n";
        Assert.Equal(new AgentProbe("Linux", "x86_64", Sha, true), AgentInstall.ParseProbe(output));

        Assert.Equal(new AgentProbe("Linux", "aarch64", "", false),
            AgentInstall.ParseProbe("resesh-agent-probe os=Linux machine=aarch64 hash= runs=0\n"));

        Assert.Null(AgentInstall.ParseProbe("sh: 1: uname: not found\n"));
        Assert.Null(AgentInstall.ParseProbe("resesh-agent-probe os=Linux machine=x86_64 hash=zz runs=1\n"));
    }

    [Fact]
    public void AnInstalledAgentThatMatchesIsUsed()
    {
        var plan = AgentInstall.Plan(new AgentProbe("Linux", "x86_64", Sha, true), Package, mayUpload: false);
        Assert.Equal(new AgentInstallPlan.Ready($"\"$HOME/.local/share/resesh/{Version}/resesh-agent\""), plan);

        // No hashing tool on the host: an agent that runs is taken as it is.
        Assert.IsType<AgentInstallPlan.Ready>(
            AgentInstall.Plan(new AgentProbe("Linux", "x86_64", "", true), Package, mayUpload: false));
    }

    [Fact]
    public void AMissingStaleOrBrokenAgentIsUploadedOnlyWhenTheSessionAllowsIt()
    {
        var missing = new AgentProbe("Linux", "x86_64", "", false);
        var stale = new AgentProbe("Linux", "x86_64", new string('b', 64), true);
        var broken = new AgentProbe("Linux", "x86_64", Sha, false);
        foreach (var probe in new[] { missing, stale, broken })
        {
            Assert.Equal(new AgentInstallPlan.Upload("x86_64", X64), AgentInstall.Plan(probe, Package, mayUpload: true));
            Assert.IsType<AgentInstallPlan.Unavailable>(AgentInstall.Plan(probe, Package, mayUpload: false));
        }
    }

    [Fact]
    public void AHostWeHaveNoBuildForGetsNoUpload()
    {
        var mac = AgentInstall.Plan(new AgentProbe("Darwin", "arm64", "", false), Package, mayUpload: true);
        Assert.Equal("there is no session agent for Darwin arm64", Assert.IsType<AgentInstallPlan.Unavailable>(mac).Reason);

        // Built for the architecture, but not carried by this copy of the app.
        Assert.IsType<AgentInstallPlan.Unavailable>(
            AgentInstall.Plan(new AgentProbe("Linux", "aarch64", "", false), Package, mayUpload: true));
    }

    [Fact]
    public void InstallCommandIsOneArgumentForAnyLoginShell()
    {
        var command = AgentInstall.InstallCommand(Version, X64);
        Assert.StartsWith("sh -c '", command);
        Assert.EndsWith("'", command);
        var script = command["sh -c '".Length..^1];
        Assert.DoesNotContain('\'', script);
        Assert.DoesNotContain('\n', script);
        Assert.Contains($"d=\"$HOME/.local/share/resesh/{Version}\"", script);
        Assert.Contains($"test \"$h\" = \"{Sha}\"", script);
        Assert.Contains("= \"1809936\"", script);
        // Checked and run before it replaces anything.
        Assert.True(script.IndexOf("cannot-run", StringComparison.Ordinal) < script.IndexOf("mv -f", StringComparison.Ordinal));
    }

    [Fact]
    public void ScriptsNeverOpenArithmeticByAccident()
    {
        // dash reads "$((" as arithmetic, so "$( (a || b) | c )" needs its space. Found on
        // a real host: the probe failed with "Missing '))'".
        Assert.DoesNotContain("$((", AgentInstall.ProbeScript(Version));
        Assert.DoesNotContain("$((", AgentInstall.InstallCommand(Version, X64));
    }

    [Theory]
    [InlineData("1.0; rm -rf ~")]
    [InlineData("$(id)")]
    [InlineData("../x")]
    [InlineData("")]
    public void AVersionThatIsNotPlainNeverReachesAShell(string version)
    {
        Assert.False(AgentInstall.IsPlainVersion(version));
        Assert.Throws<ArgumentException>(() => AgentInstall.RemotePath(version));
        Assert.Throws<ArgumentException>(() => AgentInstall.ProbeScript(version));
        Assert.Throws<ArgumentException>(() => AgentInstall.InstallCommand(version, X64));
    }

    [Fact]
    public void AHashThatIsNotPlainNeverReachesAShell() =>
        Assert.Throws<ArgumentException>(() => AgentInstall.InstallCommand(Version, X64 with { Sha256 = "\"; id; \"" }));

    [Fact]
    public void InstallOutcomeIsOnlySuccessWhenTheHostSaidSo()
    {
        Assert.Equal(AgentInstallOutcome.Installed,
            AgentInstall.ParseInstall(new SshCommandResult(true, "motd\nresesh-agent-install ok\n", "")));
        Assert.Equal(AgentInstallOutcome.Mismatch,
            AgentInstall.ParseInstall(new SshCommandResult(false, "resesh-agent-install mismatch\n", "")));
        Assert.Equal(AgentInstallOutcome.CannotRun,
            AgentInstall.ParseInstall(new SshCommandResult(false, "resesh-agent-install cannot-run\n", "")));
        Assert.Equal(AgentInstallOutcome.Failed,
            AgentInstall.ParseInstall(new SshCommandResult(false, "", "mkdir: cannot create directory")));
        Assert.Equal(AgentInstallOutcome.Failed,
            AgentInstall.ParseInstall(new SshCommandResult(false, "resesh-agent-install ok\n", "")));
        Assert.Equal(AgentInstallOutcome.Failed, AgentInstall.ParseInstall(null));
    }

    [Fact]
    public void PackageIsReadFromItsManifestAndBinariesAreVerified()
    {
        var directory = Directory.CreateTempSubdirectory("resesh-agent-test").FullName;
        try
        {
            var bytes = new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3 };
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Directory.CreateDirectory(Path.Combine(directory, "x86_64"));
            File.WriteAllBytes(Path.Combine(directory, "x86_64", "resesh-agent"), bytes);
            void Manifest(string version, string hash, string file) => File.WriteAllText(
                Path.Combine(directory, AgentPackage.ManifestName),
                $$"""{"version":"{{version}}","binaries":{"x86_64":{"file":"{{file}}","sha256":"{{hash}}","size":7} } }""");

            Manifest(Version, sha, "x86_64/resesh-agent");
            var package = AgentPackage.Load(directory);
            Assert.NotNull(package);
            Assert.Equal(Version, package.Version);
            Assert.Equal(bytes, AgentPackage.ReadVerified(package.Binaries["x86_64"]));

            // The file on disk is not the one the manifest describes.
            Manifest(Version, new string('c', 64), "x86_64/resesh-agent");
            Assert.Null(AgentPackage.ReadVerified(AgentPackage.Load(directory)!.Binaries["x86_64"]));

            Manifest("1; id", sha, "x86_64/resesh-agent");
            Assert.Null(AgentPackage.Load(directory));
            Manifest(Version, sha, "../outside");
            Assert.Null(AgentPackage.Load(directory));
            Assert.Null(AgentPackage.Load(Path.Combine(directory, "missing")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
