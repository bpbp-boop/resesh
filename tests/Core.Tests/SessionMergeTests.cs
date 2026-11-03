using System.Reflection;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class SessionMergeTests
{
    private static readonly Guid SessionId = Guid.NewGuid();

    /// <summary>Every settable property differs between <see cref="First"/> and <see cref="Second"/>;
    /// <see cref="Fixtures_DifferInEveryProperty"/> keeps it that way as Session grows.</summary>
    private static Session First() => new()
    {
        Id = SessionId,
        Name = "web-01",
        Kind = SessionKind.Ssh,
        Local = new LocalTarget { Executable = "pwsh.exe", Arguments = ["-NoLogo"], StartingDirectory = @"C:\" },
        BuiltIn = false,
        Serial = new SerialTarget { PortName = "COM3" },
        FolderPath = "Prod",
        Host = "web-01.example",
        Port = 22,
        Username = "admin",
        IdentityId = null,
        InheritLogin = false,
        AuthMethod = AuthMethod.Password,
        PrivateKeyId = null,
        PrivateKeyPath = null,
        PassphraseRequired = false,
        JumpHostId = null,
        Proxy = null,
        Tunnels = null,
        TerminalType = "xterm-256color",
        TelnetEncoding = TelnetTextEncoding.Auto,
        TelnetReport80x25 = false,
        TelnetFixed80Columns = false,
        Algorithms = null,
        PasteLineDelay = 0,
        PasteCharacterDelay = 0,
        Persistent = false,
        DetachedSessions = DetachedSessionAction.Ask,
        SessionAgent = false,
        ShellIntegration = ShellIntegrationMode.Disabled,
        Notes = "",
        ColorTag = null,
        Icon = null,
        Agent = null,
        Overrides = new TerminalOverrides { Theme = "dark", EnabledRules = ["ipv4"] },
    };

    private static Session Second() => new()
    {
        Id = SessionId,
        Name = "web-02",
        Kind = SessionKind.Telnet,
        Local = new LocalTarget { Executable = "cmd.exe", Arguments = ["/k"], StartingDirectory = @"D:\" },
        BuiltIn = true,
        Serial = new SerialTarget { PortName = "COM5", BaudRate = 115200 },
        FolderPath = "Staging/Web",
        Host = "web-02.example",
        Port = 2222,
        Username = "deploy",
        IdentityId = Guid.NewGuid(),
        InheritLogin = true,
        AuthMethod = AuthMethod.PrivateKey,
        PrivateKeyId = Guid.NewGuid(),
        PrivateKeyPath = @"C:\keys\id_ed25519",
        PassphraseRequired = true,
        JumpHostId = Guid.NewGuid(),
        Proxy = new ProxySettings { Host = "proxy.example" },
        Tunnels = [new Tunnel()],
        TerminalType = "vt100",
        TelnetEncoding = TelnetTextEncoding.Cp437,
        TelnetReport80x25 = true,
        TelnetFixed80Columns = true,
        Algorithms = new SshAlgorithms { DisabledCiphers = ["3des-cbc"] },
        PasteLineDelay = 50,
        PasteCharacterDelay = 5,
        Persistent = true,
        DetachedSessions = DetachedSessionAction.StartNew,
        SessionAgent = true,
        ShellIntegration = ShellIntegrationMode.Bash,
        Notes = "rack 4",
        ColorTag = "#E74856",
        Icon = "ubuntu",
        Agent = "claude",
        Overrides = new TerminalOverrides
        {
            Theme = "light",
            FontFamily = "Consolas",
            FontSize = 18,
            Scrollback = 20000,
            MouseCapture = MouseCaptureModes.Never,
            AlwaysRecord = true,
            KeepCommandHistory = false,
            EnabledRules = ["ipv6"],
            DisabledRules = ["mac"],
        },
    };

    /// <summary>Fails when Session or TerminalOverrides gains a property the fixtures leave
    /// equal: add it to both fixtures, and to <see cref="SessionMerge.Rebase"/>.</summary>
    [Theory]
    [InlineData(typeof(Session))]
    [InlineData(typeof(TerminalOverrides))]
    public void Fixtures_DifferInEveryProperty(Type type)
    {
        object Of(Session session) => type == typeof(Session) ? session : session.Overrides!;
        var first = Of(First());
        var second = Of(Second());
        var same = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is not null && property.Name != nameof(Session.Id))
            .Where(property => Equivalent(property.GetValue(first), property.GetValue(second)))
            .Select(property => property.Name)
            .ToList();
        Assert.Empty(same);
    }

    [Fact]
    public void Rebase_NoEdits_KeepsEveryLatestValue()
    {
        var original = First();
        var latest = Second();

        AssertEquivalent(latest, SessionMerge.Rebase(original, First(), latest));
    }

    [Fact]
    public void Rebase_EveryFieldEdited_TakesEveryEditedValue()
    {
        var edited = Second();

        AssertEquivalent(edited, SessionMerge.Rebase(First(), edited, First()));
    }

    [Fact]
    public void Rebase_KeepsAFolderMovedElsewhere_WhileTheNameIsEdited()
    {
        var original = First();
        var edited = original with { Name = "web-01-renamed" };
        var latest = original with { FolderPath = "Archive", Icon = "debian" };

        var saved = SessionMerge.Rebase(original, edited, latest);

        Assert.Equal("web-01-renamed", saved.Name);
        Assert.Equal("Archive", saved.FolderPath);
        Assert.Equal("debian", saved.Icon);
    }

    [Fact]
    public void Rebase_KeepsAHighlightRuleToggledMeanwhile_WhileTheThemeIsEdited()
    {
        var original = First();
        var edited = original with { Overrides = original.Overrides! with { Theme = "nord", EnabledRules = ["ipv4"] } };
        var latest = original with { Overrides = original.Overrides! with { DisabledRules = ["mac"] } };

        var overrides = SessionMerge.Rebase(original, edited, latest).Overrides!;

        Assert.Equal("nord", overrides.Theme);
        Assert.Equal(["ipv4"], overrides.EnabledRules!);
        Assert.Equal(["mac"], overrides.DisabledRules!);
    }

    [Fact]
    public void Rebase_RebuiltListsWithTheSameItems_AreNotEdits()
    {
        var original = First() with { Tunnels = [new Tunnel()] };
        var edited = original with { Tunnels = original.Tunnels!.ToList() };
        var latest = original with { Tunnels = null };

        Assert.Null(SessionMerge.Rebase(original, edited, latest).Tunnels);
    }

    [Fact]
    public void Rebase_MovesTheLoginAsOne()
    {
        var original = First();
        var identity = Guid.NewGuid();
        var edited = original with { IdentityId = identity, Username = "" };
        var latest = original with { InheritLogin = true };

        var saved = SessionMerge.Rebase(original, edited, latest);

        Assert.Equal(identity, saved.IdentityId);
        Assert.False(saved.InheritLogin);
        Assert.Equal(SessionLogin.Identity, saved.Login);
    }

    [Fact]
    public void Rebase_ClearingEveryOverride_StoresNull()
    {
        var original = First() with { Overrides = new TerminalOverrides { Theme = "nord" } };
        var edited = original with { Overrides = null };

        Assert.Null(SessionMerge.Rebase(original, edited, original).Overrides);
    }

    private static void AssertEquivalent(Session expected, Session actual)
    {
        foreach (var property in typeof(Session).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.True(Equivalent(property.GetValue(expected), property.GetValue(actual)), property.Name);
        foreach (var property in typeof(TerminalOverrides).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.True(Equivalent(property.GetValue(expected.Overrides), property.GetValue(actual.Overrides)), $"Overrides.{property.Name}");
    }

    /// <summary>Value equality that looks inside lists and the records holding them.</summary>
    private static bool Equivalent(object? first, object? second)
    {
        if (first is null || second is null)
            return first is null && second is null;
        if (first is string || first.GetType().IsValueType)
            return first.Equals(second);
        if (first is System.Collections.IEnumerable firstItems && second is System.Collections.IEnumerable secondItems)
        {
            var a = firstItems.Cast<object?>().ToList();
            var b = secondItems.Cast<object?>().ToList();
            return a.Count == b.Count && a.Zip(b).All(pair => Equivalent(pair.First, pair.Second));
        }
        return first.GetType() == second.GetType()
            && first.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .All(property => Equivalent(property.GetValue(first), property.GetValue(second)));
    }
}
