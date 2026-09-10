using Resesh.Core.Models;
using Resesh.Core.ShellIntegration;

namespace Resesh.Core.Tests;

public sealed class ShellIntegrationLocalTests
{
    [Fact]
    public void DisabledLeavesArgumentsAndEnvironmentUntouched()
    {
        string[] args = ["-Command", "echo hi"];
        var env = new Dictionary<string, string> { ["ENV"] = "%literal%" };
        var plan = LocalShellIntegration.CreatePlan("pwsh.exe", args, ShellIntegrationMode.Disabled, env);
        Assert.Same(args, plan.Arguments);
        Assert.Same(env, plan.Environment);
        Assert.Null(plan.Message);
    }

    [Theory]
    [InlineData("cmd.exe", "")]
    [InlineData("wsl.exe", "")]
    [InlineData("bash.exe", "-c")]
    [InlineData("pwsh.exe", "-File")]
    [InlineData("pwsh.exe", "-EncodedCommand")]
    [InlineData("bash.exe", "--rcfile")]
    public void UnsupportedLaunchesRemainUnchanged(string executable, string argument)
    {
        string[] args = argument.Length == 0 ? [] : [argument];
        var plan = LocalShellIntegration.CreatePlan(executable, args, ShellIntegrationMode.Automatic, new Dictionary<string, string>());
        Assert.Same(args, plan.Arguments);
        Assert.Contains("skipped", plan.Message);
    }

    [Fact]
    public void BashPreservesLiteralEnvironmentAndEmptyInjectionFlag()
    {
        var env = new Dictionary<string, string> { ["ENV"] = "a%literal%", ["OTHER"] = "keep" };
        var plan = LocalShellIntegration.CreatePlan("bash.exe", [], ShellIntegrationMode.Bash, env, @"C:\cache space");
        Assert.Equal("", plan.Environment["RESESH_SHELL_BASH_INJECT"]);
        Assert.Equal("a%literal%", plan.Environment["RESESH_SHELL_BASH_ENV"]);
        Assert.Equal("C:/cache space/bash/integration.bash", plan.Environment["ENV"]);
        Assert.Equal(new[] { "--posix", "-i" }, plan.Arguments);
        Assert.Equal("a%literal%", env["ENV"]);
    }

    [Fact]
    public void PowerShellKeepsProfileChoiceAndDoesNotBypassExecutionPolicy()
    {
        var plan = LocalShellIntegration.CreatePlan("powershell.exe", ["-NoProfile"], ShellIntegrationMode.Automatic,
            new Dictionary<string, string>(), "C:/someone's cache");
        Assert.Equal(new[] { "-NoProfile", "-NoExit", "-Command" }, plan.Arguments.Take(3));
        Assert.StartsWith("try { . 'C:/someone''s cache/pwsh/integration.ps1' }", plan.Arguments[3]);
        Assert.Contains("catch [System.Management.Automation.PSSecurityException]", plan.Arguments[3]);
        Assert.Contains("You can still use this shell.", plan.Arguments[3]);
        Assert.DoesNotContain("ExecutionPolicy", plan.Arguments[3]);
    }

    [Fact]
    public void ExplicitPolicyIsPreservedAndMissingValueSkips()
    {
        var env = new Dictionary<string, string>();
        var plan = LocalShellIntegration.CreatePlan("pwsh.exe", ["-ExecutionPolicy", "RemoteSigned"], ShellIntegrationMode.Automatic, env, "C:/cache");
        Assert.Null(plan.Message);
        Assert.Equal(new[] { "-ExecutionPolicy", "RemoteSigned" }, plan.Arguments.Take(2));
        Assert.NotNull(LocalShellIntegration.CreatePlan("pwsh.exe", ["-ExecutionPolicy"], ShellIntegrationMode.Automatic, env, "C:/cache").Message);
    }

    [Fact]
    public void BashLoginPreservesShellFlagAndStartupIntent()
    {
        var plan = LocalShellIntegration.CreatePlan("bash.exe", ["--login", "-i"], ShellIntegrationMode.Automatic, new Dictionary<string, string>(), "C:/cache");
        Assert.Equal(new[] { "--posix", "--login", "-i" }, plan.Arguments);
        Assert.Equal("login", plan.Environment["RESESH_SHELL_BASH_INJECT"]);
    }

    [Fact]
    public void EveryScriptIsEmbeddedAndUsesExplicitTmuxOptIn()
    {
        foreach (var path in ShellIntegrationScripts.Files)
        {
            var script = ShellIntegrationScripts.Read(path);
            Assert.NotEmpty(script);
            Assert.DoesNotContain("\r", script);
            if (path is "bash/integration.bash" or "zsh/.zshrc" or "pwsh/integration.ps1" or "fish/vendor_conf.d/integration.fish")
                Assert.Contains("RESESH_SHELL_TMUX", script);
        }
    }
}
