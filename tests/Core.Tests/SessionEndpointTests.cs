using Resesh.Core.Models;

namespace Resesh.Core.Tests;

/// <summary>One formatter names every session's target, so the tree, tabs, history and the
/// review dialogs agree on default ports, prefixes and fallbacks.</summary>
public sealed class SessionEndpointTests
{
    private static Session Ssh(int port = 22, string username = "deploy") =>
        new() { Host = "web01", Port = port, Username = username };

    private static Session Telnet(int port = 23) => new() { Kind = SessionKind.Telnet, Host = "console", Port = port };

    private static Session Serial(SerialTarget? serial) => new() { Kind = SessionKind.Serial, Serial = serial };

    private static Session Local(string? executable) =>
        new() { Kind = SessionKind.Local, Local = executable is null ? null : new LocalTarget { Executable = executable } };

    [Fact]
    public void CompactFormHidesDefaultPortsAndUsers()
    {
        Assert.Equal("web01", SessionEndpoint.Format(Ssh()));
        Assert.Equal("web01:2222", SessionEndpoint.Format(Ssh(2222)));
        Assert.Equal("console", SessionEndpoint.Format(Telnet()));
        Assert.Equal("console:2003", SessionEndpoint.Format(Telnet(2003)));
    }

    [Fact]
    public void Ipv6HostsTakeBracketsOnlyBeforeAPort()
    {
        var v6 = new Session { Host = "::1", Port = 2222, Username = "ops" };
        Assert.Equal("ops@[::1]:2222", SessionEndpoint.Format(v6, new() { Username = "ops" }));
        Assert.Equal("::1", SessionEndpoint.Format(v6 with { Port = 22 }));
        Assert.Equal("[::1]:22", SessionEndpoint.Format(v6 with { Port = 22 }, new() { ShowDefaultPort = true }));
        Assert.Equal("telnet [2001:db8::5]:2003",
            SessionEndpoint.Format(new Session { Kind = SessionKind.Telnet, Host = "2001:db8::5", Port = 2003 }, new() { ShowProtocol = true }));
    }

    [Fact]
    public void UnsetPortReadsAsTheDefaultEverywhere()
    {
        Assert.Equal("web01", SessionEndpoint.Format(Ssh(0)));
        Assert.Equal("console", SessionEndpoint.Format(Telnet(0)));
        Assert.Equal("web01:22", SessionEndpoint.Format(Ssh(0), new() { ShowDefaultPort = true }));
        Assert.Equal("console:23", SessionEndpoint.Format(Telnet(0), new() { ShowDefaultPort = true }));
    }

    [Fact]
    public void UserIsShownOnlyForSshAndOnlyWhenKnown()
    {
        Assert.Equal("deploy@web01:22", SessionEndpoint.Format(Ssh(), new() { Username = "deploy", ShowDefaultPort = true }));
        // A session that asks for its login has no user to show, not an empty one.
        Assert.Equal("web01:22", SessionEndpoint.Format(Ssh(username: ""), new() { Username = "", ShowDefaultPort = true }));
        Assert.Equal("console", SessionEndpoint.Format(Telnet(), new() { Username = "deploy" }));
    }

    [Fact]
    public void ProtocolPrefixCoversTelnetAndSerial()
    {
        var serial = new SerialTarget { PortName = "COM3", BaudRate = 9600 };

        Assert.Equal("telnet console", SessionEndpoint.Format(Telnet(), new() { ShowProtocol = true }));
        Assert.Equal($"serial {serial.Describe()}", SessionEndpoint.Format(Serial(serial), new() { ShowProtocol = true }));
        Assert.Equal(serial.Describe(), SessionEndpoint.Format(Serial(serial)));
        Assert.Equal("COM3", SessionEndpoint.Format(Serial(serial), new() { SerialPortOnly = true, ShowProtocol = false }));
        Assert.Equal("web01", SessionEndpoint.Format(Ssh(), new() { ShowProtocol = true }));
    }

    [Fact]
    public void MissingSerialAndShellSettingsHaveAName()
    {
        Assert.Equal("serial", SessionEndpoint.Format(Serial(null)));
        Assert.Equal("serial", SessionEndpoint.Format(Serial(null), new() { ShowProtocol = true }));
        Assert.Equal("local shell", SessionEndpoint.Format(Local(null)));
        Assert.Equal("local shell", SessionEndpoint.Format(Local(" "), new() { LocalShell = LocalShellDisplay.FullPath }));
    }

    [Fact]
    public void LocalShellReadsAsAskedFor()
    {
        var profile = Local(@"C:\Program Files\PowerShell\7\pwsh.exe");

        Assert.Equal("pwsh.exe", SessionEndpoint.Format(profile));
        Assert.Equal("pwsh", SessionEndpoint.Format(profile, new() { LocalShell = LocalShellDisplay.FileNameWithoutExtension }));
        Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe",
            SessionEndpoint.Format(profile, new() { LocalShell = LocalShellDisplay.FullPath }));
        Assert.Equal("wsl", SessionEndpoint.Format(Local(@"C:\tools\wsl\")));
    }

    [Fact]
    public void UnsavedTargetsFormatLikeSessions()
    {
        Assert.Equal("telnet console:2003",
            SessionEndpoint.Format(SessionKind.Telnet, "console", 2003, null, null, new() { ShowProtocol = true }));
        Assert.Equal("root@db",
            SessionEndpoint.Format(SessionKind.Ssh, "db", SessionEndpoint.DefaultSshPort, null, null, new() { Username = "root" }));
    }
}
