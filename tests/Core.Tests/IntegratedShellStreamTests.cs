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
