using Resesh.Core.Models;

namespace Resesh.Core.Tests;

public sealed class QuickConnectTargetTests
{
    [Theory]
    [InlineData("telnet ts1", "ts1", 23)]
    [InlineData("telnet ts1:2003", "ts1", 2003)]
    [InlineData("telnet ts1 2003", "ts1", 2003)]
    [InlineData("  TELNET 10.0.0.5   2001 ", "10.0.0.5", 2001)]
    public void TelnetForms_ParseToTelnetSessions(string input, string host, int port)
    {
        Assert.True(QuickConnectTarget.TryParse(input, "me", out var session));
        Assert.Equal(SessionKind.Telnet, session.Kind);
        Assert.Equal(host, session.Host);
        Assert.Equal(port, session.Port);
        Assert.Equal("", session.Username);
    }

    [Theory]
    [InlineData("telnet")]
    [InlineData("telnet ts1 port")]
    [InlineData("telnet ts1:23 2003")]
    [InlineData("telnet ts1 70000")]
    [InlineData("telnet admin@ts1")]
    [InlineData("telnet a b c")]
    public void MalformedTelnetTargets_AreRejected(string input) =>
        Assert.False(QuickConnectTarget.TryParse(input, "me", out _));

    [Theory]
    [InlineData("ssh ::1", "::1", 22, "me@::1")]
    [InlineData("ssh 2001:db8::22", "2001:db8::22", 22, "me@2001:db8::22")]
    [InlineData("ssh [::1]:2222", "::1", 2222, "me@[::1]:2222")]
    [InlineData("root@[2001:db8::1]", "2001:db8::1", 22, "root@2001:db8::1")]
    [InlineData("ops@web01:2222", "web01", 2222, "ops@web01:2222")]
    [InlineData("john@corp@web01", "web01", 22, "john@corp@web01")]
    public void SshTargets_ReadIpv6AsOpenSshDoes_AndNameTheirPort(string input, string host, int port, string name)
    {
        Assert.True(QuickConnectTarget.TryParse(input, "me", out var session));
        Assert.Equal(SessionKind.Ssh, session.Kind);
        Assert.Equal(host, session.Host);
        Assert.Equal(port, session.Port);
        Assert.Equal(name, session.Name);
    }

    [Theory]
    [InlineData("telnet ::1", "::1", 23, "::1")]
    [InlineData("telnet ::1 2003", "::1", 2003, "[::1]:2003")]
    [InlineData("telnet [::1]:2003", "::1", 2003, "[::1]:2003")]
    [InlineData("telnet ts1:2003", "ts1", 2003, "ts1:2003")]
    public void TelnetTargets_ReadIpv6AsOpenSshDoes(string input, string host, int port, string name)
    {
        Assert.True(QuickConnectTarget.TryParse(input, "me", out var session));
        Assert.Equal(host, session.Host);
        Assert.Equal(port, session.Port);
        Assert.Equal(name, session.Name);
    }

    [Theory]
    [InlineData("telnet [::1]:23 2003")]
    [InlineData("ssh [::1")]
    [InlineData("ssh web01:abc")]
    public void Ipv6AndPortMistakes_AreRejected(string input) =>
        Assert.False(QuickConnectTarget.TryParse(input, "me", out _));

    [Theory]
    [InlineData("serial COM5", "COM5 9600 8N1")]
    [InlineData("serial com5 115200", "COM5 115200 8N1")]
    [InlineData("serial COM5:115200", "COM5 115200 8N1")]
    [InlineData("SERIAL COM12 7E1 19200", "COM12 19200 7E1")]
    public void SerialForms_ParseToSerialSessions(string input, string described)
    {
        Assert.True(QuickConnectTarget.TryParse(input, "me", out var session));
        Assert.Equal(SessionKind.Serial, session.Kind);
        Assert.Equal(described, session.Serial!.Describe());
        Assert.Equal(session.Serial.PortName, session.Name);
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("serial printer")]
    [InlineData("serial COM5 fast")]
    [InlineData("serial COM5 9600 8X1")]
    [InlineData("serial COM5 9600 8N1 extra")]
    [InlineData("COM5")]
    public void MalformedSerialTargets_AreRejected(string input) =>
        Assert.False(QuickConnectTarget.TryParse(input, "me", out _));

    [Theory]
    [InlineData("ssh web", "me", "web", 22)]
    [InlineData("root@web:2222", "root", "web", 2222)]
    [InlineData("ssh admin@10.0.0.1", "admin", "10.0.0.1", 22)]
    public void SshForms_AreUnchanged(string input, string user, string host, int port)
    {
        Assert.True(QuickConnectTarget.TryParse(input, "me", out var session));
        Assert.Equal(SessionKind.Ssh, session.Kind);
        Assert.Equal(user, session.Username);
        Assert.Equal(host, session.Host);
        Assert.Equal(port, session.Port);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("@web")]
    [InlineData("prod web")]
    public void PlainSearchText_IsNotATarget(string input) =>
        Assert.False(QuickConnectTarget.TryParse(input, "me", out _));
}
