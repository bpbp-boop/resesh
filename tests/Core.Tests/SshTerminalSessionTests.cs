using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class SshTerminalSessionTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    public void IsConnectionOpen_RequiresAnOpenShellChannel(
        bool transportConnected, bool shellCreated, bool open, bool expected)
    {
        Assert.Equal(expected,
            SshTerminalSession.IsConnectionOpen(transportConnected, shellCreated, open));
    }
}
