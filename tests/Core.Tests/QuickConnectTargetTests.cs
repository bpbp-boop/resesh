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
