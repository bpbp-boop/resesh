using Resesh.Core.Models;
using Resesh.Core.ShellIntegration;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class RemoteShellIntegrationTests
{
    [Theory]
    [InlineData(ShellIntegrationMode.Bash)]
    [InlineData(ShellIntegrationMode.Zsh)]
    [InlineData(ShellIntegrationMode.Fish)]
    public void PosixSetup_UsesUnixLineEndingsOnEveryClientPlatform(ShellIntegrationMode mode)
    {
        var script = RemoteShellIntegration.BuildSetup(mode, new string('a', 32));
        Assert.DoesNotContain("\r", script);
        Assert.EndsWith("\n", script);
    }

    [Fact]
    public void SetupFailure_ReportsExitAndRemoteReasonWithoutTerminalControls()
    {
        var reason = RemoteShellIntegration.DescribeSetupFailure(2,
            "\u001b[31msh: Syntax error: expecting done\u001b[0m\r\n\u202e" + new string('x', 500));
        Assert.Contains("exited with code 2", reason);
        Assert.Contains("Syntax error: expecting done", reason);
        Assert.DoesNotContain(reason, char.IsControl);
        Assert.DoesNotContain('\u202e', reason);
        Assert.True(reason.Length < 300);
    }

    [Fact]
    public void SetupFailure_DistinguishesMissingReadyReplyFromProcessFailure()
    {
        Assert.Contains("valid script directory", RemoteShellIntegration.DescribeSetupFailure(0, ""));
        Assert.Contains("exit status", RemoteShellIntegration.DescribeSetupFailure(null, ""));
    }

    [Theory]
    [InlineData(ShellIntegrationMode.Disabled)]
    [InlineData(ShellIntegrationMode.Automatic)]
    [InlineData((ShellIntegrationMode)999)]
    public void UnselectedRemoteShell_NeverRequiresConnection(ShellIntegrationMode mode)
    {
        using var session = new SshTerminalSession(new KnownHostsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        Assert.Null(session.PrepareShellIntegration(mode, CancellationToken.None));
        Assert.False(RemoteShellIntegration.IsSupported(mode));
        Assert.Empty(RemoteShellIntegration.ResourceFiles(mode));
    }

    [Theory]
    [InlineData("/home/me/.cache/resesh/shell-integration/abc")]
    [InlineData("/home/space and 'quote/.cache/resesh/shell-integration/abc")]
    public void DirectoryReply_PreservesPathAsData(string path) =>
        Assert.Equal(path, RemoteShellIntegration.ParseDirectory(ShellIntegrationMode.Bash,
            "login banner\n" + RemoteShellIntegration.ReadyPrefix + path + "\n"));

    [Theory]
    [InlineData("relative")]
    [InlineData("/home/u\u001b]2;injected")]
    [InlineData("/home/u\rmalformed")]
    [InlineData("")]
    public void DirectoryReply_RejectsUnusablePayload(string path) =>
        Assert.Null(RemoteShellIntegration.ParseDirectory(ShellIntegrationMode.Bash, RemoteShellIntegration.ReadyPrefix + path));

    [Fact]
    public void WindowsDirectory_DoesNotUseHostOperatingSystemPathRules()
    {
        const string path = @"C:\Users\test user\AppData\Local\Resesh\shell-integration\cache";
        Assert.Equal(path, RemoteShellIntegration.ParseDirectory(ShellIntegrationMode.PowerShell, RemoteShellIntegration.ReadyPrefix + path));
        Assert.Null(RemoteShellIntegration.ParseDirectory(ShellIntegrationMode.PowerShell, RemoteShellIntegration.ReadyPrefix + "/tmp/cache"));
    }

    [Fact]
    public void PosixQuoting_ContainsMetacharactersInSingleQuotedSegments() =>
        Assert.Equal("'a'\"'\"'b; $(touch nope)'", RemoteShellIntegration.QuotePosix("a'b; $(touch nope)"));

    [Fact]
    public void FishSourcePath_EscapesBackslashesAndQuotesBeforeThePosixWrapper()
    {
        const string directory = "/home/a\\\\b'quote/cache";
        var source = "source '/home/a\\\\\\\\b\\'quote/cache/fish/vendor_conf.d/integration.fish'";
        var wrapper = "export RESESH_SHELL_INTEGRATION=1 RESESH_SHELL_RESOURCES=" + RemoteShellIntegration.QuotePosix(directory)
            + "; exec fish -l -i --init-command " + RemoteShellIntegration.QuotePosix(source);
        Assert.Equal("sh -c " + RemoteShellIntegration.QuotePosix(wrapper),
            RemoteShellIntegration.LaunchCommand(ShellIntegrationMode.Fish, directory));
    }

    [Fact]
    public void PersistentLaunch_AppliesHooksOnlyToNewPane()
    {
        var launch = RemoteShellIntegration.LaunchCommand(ShellIntegrationMode.Bash, "/home/a'b/cache", tmux: true);
        var bootstrap = TmuxPersistence.BootstrapCommand(Guid.Empty, 0, launch, "bash -l -i");
        var attach = bootstrap[..bootstrap.IndexOf("else ", StringComparison.Ordinal)];
        Assert.DoesNotContain("RESESH_SHELL", attach);
        Assert.Contains("allow-passthrough on", bootstrap);
        Assert.Contains(RemoteShellIntegration.QuotePosix(launch), bootstrap);
        Assert.Contains("Persistent startup failed", bootstrap);
        Assert.EndsWith("exec bash -l -i # resesh-tmux-bootstrap", bootstrap);
        Assert.DoesNotContain("RESESH_SHELL", TmuxPersistence.ResumeCommand(Guid.Empty, 0));
    }

    [Fact]
    public void DisabledPersistentBootstrap_RemainsTheLegacyPath()
    {
        var bootstrap = TmuxPersistence.BootstrapCommand(Guid.Empty, 0);
        Assert.DoesNotContain("allow-passthrough", bootstrap);
        Assert.DoesNotContain("RESESH_SHELL", bootstrap);
        Assert.Contains("exec tmux", bootstrap);
    }

    [Fact]
    public void FailedPreparation_DoesNotReuseTheSelectedMissingShellAsFallback()
    {
        using var session = new SshTerminalSession(new KnownHostsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        // No connection makes preparation fail before an integrated launch is available.
        var bootstrap = session.CreatePersistentBootstrap(Guid.Empty, 0, ShellIntegrationMode.Fish, CancellationToken.None);
        Assert.DoesNotContain("fish -l -i", bootstrap);
        Assert.DoesNotContain("allow-passthrough", bootstrap);
        Assert.Contains(RemoteShellIntegration.QuotePosix(
            "case \"${SHELL:-}\" in /*) if test -f \"$SHELL\" && test -x \"$SHELL\"; then exec \"$SHELL\" -l -i; fi;; esac; exec sh -i"), bootstrap);
    }

    [Theory]
    [InlineData(ShellIntegrationMode.Bash)]
    [InlineData(ShellIntegrationMode.Zsh)]
    [InlineData(ShellIntegrationMode.Fish)]
    public void SuccessfulStaging_PreservesIntegrationInNonPersistentFallback(ShellIntegrationMode mode)
    {
        const string directory = "/home/space and 'quote/cache";
        var bootstrap = SshTerminalSession.BuildPersistentBootstrap(Guid.Empty, 0, mode, directory);
        var fallback = RemoteShellIntegration.LaunchCommand(mode, directory, tmux: false);
        Assert.Contains(RemoteShellIntegration.QuotePosix(RemoteShellIntegration.LaunchCommand(mode, directory, tmux: true)), bootstrap);
        Assert.EndsWith("exec " + fallback + " # resesh-tmux-bootstrap", bootstrap);
        Assert.DoesNotContain("RESESH_SHELL_TMUX", fallback);
        Assert.DoesNotContain("RESESH_SHELL", bootstrap[..bootstrap.IndexOf("else ", StringComparison.Ordinal)]);
    }

    [Fact]
    public void SetupRejectsUntrustedInstallationIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => RemoteShellIntegration.BuildSetup(ShellIntegrationMode.Bash, "../../other"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteShellIntegration.BuildSetup(ShellIntegrationMode.Automatic, new string('a', 32)));
    }

    [Fact]
    public void WindowsLaunch_PreservesPolicyAndEscapesScriptPath()
    {
        var command = RemoteShellIntegration.LaunchCommand(ShellIntegrationMode.PowerShell, @"C:\Users\a'b\cache");
        var encoded = command.Split(' ')[^1];
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.StartsWith(@"try { . 'C:\Users\a''b\cache\pwsh\integration.ps1' }", script);
        Assert.Contains("catch [System.Management.Automation.PSSecurityException]", script);
        Assert.Contains("You can still use this shell.", script);
        Assert.DoesNotContain("ExecutionPolicy", script);
        Assert.DoesNotContain("ExecutionPolicy", command);
        Assert.Throws<NotSupportedException>(() => RemoteShellIntegration.LaunchCommand(ShellIntegrationMode.PowerShell, @"C:\cache", tmux: true));
    }
}
