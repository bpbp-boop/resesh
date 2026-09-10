using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class ShellIntegrationSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("shell-integration-settings").FullName;
    private string StorePath => Path.Combine(_dir, "sessions.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void LegacyAndNewProfiles_DefaultToDisabled_AndOmitSetting()
    {
        File.WriteAllText(StorePath, """
            { "sessions": [{ "name": "legacy", "host": "router" }] }
            """);
        var store = new SessionStore(StorePath);
        store.Load();
        Assert.Equal(ShellIntegrationMode.Disabled, Assert.Single(store.Sessions).ShellIntegration);
        var local = new Session { Name = "local", Kind = SessionKind.Local, Local = new LocalTarget { Executable = "pwsh" } };
        Assert.Equal(ShellIntegrationMode.Disabled, local.ShellIntegration);
        store.Add(local);
        Assert.DoesNotContain("shellIntegration", File.ReadAllText(StorePath));
    }

    [Theory]
    [InlineData(SessionKind.Local, ShellIntegrationMode.Automatic)]
    [InlineData(SessionKind.Ssh, ShellIntegrationMode.Bash)]
    [InlineData(SessionKind.Ssh, ShellIntegrationMode.Zsh)]
    [InlineData(SessionKind.Ssh, ShellIntegrationMode.Fish)]
    [InlineData(SessionKind.Ssh, ShellIntegrationMode.PowerShell)]
    public void ExplicitChoice_RoundTripsAndSurvivesCloneAndAppearanceEdit(SessionKind kind, ShellIntegrationMode mode)
    {
        var original = new Session { Name = "shell", Kind = kind, ShellIntegration = mode };
        var store = new SessionStore(StorePath);
        store.Add(original);
        var reloaded = new SessionStore(StorePath);
        reloaded.Load();
        var saved = Assert.Single(reloaded.Sessions);
        Assert.Equal(mode, saved.ShellIntegration);
        var clone = saved with { Id = Guid.NewGuid(), Name = "clone", Overrides = new TerminalOverrides { FontSize = 18 } };
        Assert.Equal(mode, clone.ShellIntegration);
        reloaded.Add(clone);
        reloaded.Load();
        Assert.All(reloaded.Sessions, session => Assert.Equal(mode, session.ShellIntegration));
        reloaded.Update(clone with { ShellIntegration = ShellIntegrationMode.Disabled });
        reloaded.Load();
        Assert.Equal(ShellIntegrationMode.Disabled, reloaded.Find(clone.Id)!.ShellIntegration);
        Assert.Equal(mode, reloaded.Find(original.Id)!.ShellIntegration);
    }
}
