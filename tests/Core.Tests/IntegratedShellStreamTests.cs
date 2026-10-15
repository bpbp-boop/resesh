using Renci.SshNet;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class IntegratedShellStreamTests
{
    [Fact]
    public void PinnedSshNetVersion_ExposesRequiredPtyExecSeam() =>
        Assert.True(IntegratedShellStream.IsSupported,
            "SSH.NET private API changed: integrated startup must be reviewed before upgrading.");

    [Fact]
    public void PinnedSshNetVersion_ExposesShellAndEnvironmentSeam() =>
        Assert.True(IntegratedShellStream.IsShellSupported,
            "SSH.NET private API changed: plain shells would silently stop offering COLORTERM.");

    [Fact]
    public void PinnedSshNetVersion_SendsEnvironmentWithoutWaitingForAReply() =>
        Assert.True(IntegratedShellStream.CanSendWithoutReply,
            "SSH.NET private API changed: every shell would wait an extra round trip for the COLORTERM reply.");

    [Fact]
    public void EnvironmentRequest_MatchesSshNetFormatButWantsNoReply()
    {
        var internalType = typeof(SshClient).Assembly.GetType("Renci.SshNet.Messages.Connection.EnvironmentVariableRequestInfo")!;
        var reference = (Renci.SshNet.Common.SshData)Activator.CreateInstance(internalType, "COLORTERM", "truecolor")!;
        var expected = reference.GetBytes();
        expected[0] = 0; // want_reply
        Assert.Equal(expected, new EnvironmentRequest("COLORTERM", "truecolor").GetBytes());
    }

    [Fact]
    public void EverySessionOffersTruecolor() =>
        Assert.Contains(new KeyValuePair<string, string>("COLORTERM", "truecolor"), IntegratedShellStream.SessionEnvironment);

    [Fact]
    public void PlainShell_DisconnectedClient_ReportsConnectionRequirement()
    {
        using var client = new SshClient("localhost", "test", "test");
        var error = Assert.Throws<InvalidOperationException>(() =>
            IntegratedShellStream.Create(client, "xterm-256color", 80, 24, null, CancellationToken.None));
        Assert.Equal("SSH is not connected.", error.Message);
    }

    [Fact]
    public void CancelledStartup_DoesNotTryToOpenChannel()
    {
        using var client = new SshClient("localhost", "test", "test");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            IntegratedShellStream.Create(client, "xterm-256color", 80, 24, "echo test", cancellation.Token));
    }

    [Fact]
    public void DisconnectedClient_ReportsConnectionRequirement()
    {
        using var client = new SshClient("localhost", "test", "test");
        var error = Assert.Throws<InvalidOperationException>(() =>
            IntegratedShellStream.Create(client, "xterm-256color", 80, 24, "echo test", CancellationToken.None));
        Assert.Equal("SSH is not connected.", error.Message);
    }
}
