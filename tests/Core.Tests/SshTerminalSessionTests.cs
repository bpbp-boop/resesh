using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class SshTerminalSessionTests
{
    [Theory]
    [InlineData(true, true, 0, true)]
    [InlineData(true, true, 1, false)]
    [InlineData(false, true, 0, false)]
    [InlineData(true, false, 0, false)]
    public void IsConnectionOpen_RequiresAnOpenShellChannel(
        bool transportConnected, bool shellCreated, int closedRaised, bool expected)
    {
        Assert.Equal(expected,
            SshTerminalSession.IsConnectionOpen(transportConnected, shellCreated, closedRaised));
    }
}
