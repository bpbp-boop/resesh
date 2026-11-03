using Resesh.Core.Models;

namespace Resesh.Core.Tests;

/// <summary>Host and port text is read one way everywhere: quick connect, telnet's two-word
/// form and ProxyJump all treat IPv6 as OpenSSH does.</summary>
public sealed class HostPortTests
{
    [Theory]
    [InlineData("web01", "web01", null)]
    [InlineData("web01:2222", "web01", 2222)]
    [InlineData("::1", "::1", null)]
    [InlineData("2001:db8::22", "2001:db8::22", null)]
    [InlineData("[::1]", "::1", null)]
    [InlineData("[::1]:2222", "::1", 2222)]
    [InlineData("[2001:db8::1]:22", "2001:db8::1", 22)]
    [InlineData(" 10.0.0.5:23 ", "10.0.0.5", 23)]
    public void SplitsHostsAndPorts(string text, string host, int? port)
    {
        Assert.True(HostPort.TrySplit(text, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData(":22")]
    [InlineData("web01:")]
    [InlineData("web01:ssh")]
    [InlineData("web01:70000")]
    [InlineData("web01:+22")]
    [InlineData("[::1")]
    [InlineData("[]:22")]
    [InlineData("[::1]2222")]
    [InlineData("[::1]:")]
    [InlineData("a]b")]
    public void RejectsMalformedText(string text) => Assert.False(HostPort.TrySplit(text, out _, out _));

    [Theory]
    [InlineData("[::1]", "::1")]
    [InlineData(" web01 ", "web01")]
    [InlineData("::1", "::1")]
    [InlineData("[]", "[]")]
    public void UnbracketsStoredHosts(string host, string stored) => Assert.Equal(stored, HostPort.Unbracket(host));

    [Theory]
    [InlineData("web01", 2222, "web01:2222")]
    [InlineData("::1", 2222, "[::1]:2222")]
    [InlineData("[::1]", 2222, "[::1]:2222")]
    public void BracketsIpv6BeforeAPort(string host, int port, string text) => Assert.Equal(text, HostPort.WithPort(host, port));
}
