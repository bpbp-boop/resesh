using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class SessionBulkEditTests
{
    private static Session Ssh(string name = "web-01", string folder = "") =>
        new() { Name = name, Host = name, FolderPath = folder, Username = "admin" };

    private static Session Local(string name = "PowerShell") =>
        new() { Name = name, Kind = SessionKind.Local, Local = new LocalTarget { Executable = "pwsh.exe" } };

    [Fact]
    public void EmptyEdit_ChangesNothing()
    {
        var session = Ssh();
        var edit = new SessionBulkEdit();

        Assert.True(edit.IsEmpty);
        Assert.Same(session, edit.Apply(session));
        Assert.Empty(edit.ApplyTo([session]));
    }

    [Fact]
    public void Identity_ReplacesAnOwnLoginAndDropsItsUsername()
    {
        var identity = Guid.NewGuid();
        var session = Ssh() with { AuthMethod = AuthMethod.PrivateKey, PrivateKeyId = Guid.NewGuid() };

        var edited = new SessionBulkEdit { Login = SessionLogin.Identity, IdentityId = identity }.Apply(session);

        Assert.Equal(SessionLogin.Identity, edited.Login);
        Assert.Equal(identity, edited.IdentityId);
        Assert.Equal("", edited.Username);
        Assert.Equal(AuthMethod.Password, edited.AuthMethod);
        Assert.Null(edited.PrivateKeyId);
    }

    [Fact]
    public void Identity_CanKeepTheUsernameAsAnOverride()
    {
        var edit = new SessionBulkEdit { Login = SessionLogin.Identity, IdentityId = Guid.NewGuid(), KeepUsernames = true };

        Assert.Equal("admin", edit.Apply(Ssh()).Username);
    }

    [Fact]
    public void Inherit_ClearsAChosenIdentity()
    {
        var session = Ssh() with { Username = "", IdentityId = Guid.NewGuid() };

        var edited = new SessionBulkEdit { Login = SessionLogin.Inherit }.Apply(session);

        Assert.Equal(SessionLogin.Inherit, edited.Login);
        Assert.Null(edited.IdentityId);
    }

    [Fact]
    public void LoginAndPort_SkipSessionsWithoutThem()
    {
        var telnet = Ssh("bbs") with { Kind = SessionKind.Telnet, Port = 23, Username = "" };
        var local = Local();
        var edit = new SessionBulkEdit { Login = SessionLogin.Identity, IdentityId = Guid.NewGuid(), Port = 2222, Persistent = true };

        var editedTelnet = edit.Apply(telnet);

        Assert.Equal(SessionLogin.Own, editedTelnet.Login);
        Assert.Equal(2222, editedTelnet.Port);
        Assert.False(editedTelnet.Persistent);
        Assert.Same(local, edit.Apply(local));
    }

    [Fact]
    public void SessionAgent_IsOnlyKeptOnAPersistentSession()
    {
        var persistent = Ssh() with { Persistent = true };
        Assert.True(new SessionBulkEdit { SessionAgent = true }.Apply(persistent).SessionAgent);
        Assert.False(new SessionBulkEdit { SessionAgent = false }.Apply(persistent with { SessionAgent = true }).SessionAgent);

        // Chosen together with turning persistence on, or lost with turning it off.
        Assert.True(new SessionBulkEdit { Persistent = true, SessionAgent = true }.Apply(Ssh()).SessionAgent);
        Assert.False(new SessionBulkEdit { SessionAgent = true }.Apply(Ssh()).SessionAgent);
        Assert.False(new SessionBulkEdit { Persistent = false }.Apply(persistent with { SessionAgent = true }).SessionAgent);

        // Nothing asked, nothing changed.
        var agent = persistent with { SessionAgent = true };
        Assert.Same(agent, new SessionBulkEdit { Persistent = true }.Apply(agent));
    }

    [Fact]
    public void Persistent_IsNotTurnedOnWithPowerShellIntegration()
    {
        var session = Ssh() with { ShellIntegration = ShellIntegrationMode.PowerShell };

        Assert.False(new SessionBulkEdit { Persistent = true }.Apply(session).Persistent);
    }

    [Fact]
    public void Theme_KeepsOtherOverridesAndClearsBackToTheAppSetting()
    {
        var session = Ssh() with { Overrides = new TerminalOverrides { FontSize = 18 } };

        var themed = new SessionBulkEdit { SetTheme = true, Theme = "light" }.Apply(session);
        var cleared = new SessionBulkEdit { SetTheme = true }.Apply(Ssh() with { Overrides = new TerminalOverrides { Theme = "light" } });

        Assert.Equal(new TerminalOverrides { FontSize = 18, Theme = "light" }, themed.Overrides);
        Assert.Null(cleared.Overrides);
    }

    [Fact]
    public void ColorAndIcon_ApplyToEveryKindAndReportOnlyChangedSessions()
    {
        var red = Ssh("red") with { ColorTag = "#E74856" };
        var plain = Local();
        var edit = new SessionBulkEdit { SetColor = true, ColorTag = "#E74856", SetIcon = true, Icon = "linux" };

        var changed = new SessionBulkEdit { SetColor = true, ColorTag = "#E74856" }.ApplyTo([red, plain]);

        Assert.Equal([plain.Id], changed.Select(s => s.Id));
        Assert.Equal("linux", edit.Apply(plain).Icon);
    }

    [Fact]
    public void Duplicate_PicksNamesThatAreFreeInTheFolder()
    {
        var source = Ssh("web-01", "prod");
        var existing = new[] { source, Ssh("web-01 copy", "prod"), Ssh("web-01 copy 2", "staging") };

        var copies = SessionDuplicator.Duplicate([source, source], existing);

        Assert.Equal(["web-01 copy 2", "web-01 copy 3"], copies.Select(pair => pair.Copy.Name));
        Assert.All(copies, pair =>
        {
            Assert.NotEqual(source.Id, pair.Copy.Id);
            Assert.Equal(source with { Id = pair.Copy.Id, Name = pair.Copy.Name }, pair.Copy);
        });
    }

    [Fact]
    public void Duplicate_OfABuiltInProfileIsAnOrdinaryProfile()
    {
        var builtIn = Local() with { BuiltIn = true };

        var copy = SessionDuplicator.Duplicate([builtIn], [builtIn])[0].Copy;

        Assert.False(copy.BuiltIn);
        Assert.Equal("PowerShell copy", copy.Name);
    }
}
