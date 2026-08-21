using System.Reflection;
using Renci.SshNet;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

/// <summary>
/// Pins the SSH.NET private members the resize reflection helper depends on.
/// If a package bump renames them, these tests fail instead of resize silently no-oping.
/// </summary>
public sealed class ShellStreamResizerTests
{
    [Fact]
    public void ShellStream_HasPrivateChannelField()
    {
        var field = typeof(ShellStream).GetField(
            ShellStreamResizer.ChannelFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.True(ShellStreamResizer.IsSupported);
    }

    [Fact]
    public void ChannelSession_HasSendWindowChangeRequest()
    {
        var channelType = typeof(SshClient).Assembly.GetTypes()
            .SingleOrDefault(t => t.Name == "ChannelSession" && !t.IsInterface);
        Assert.NotNull(channelType);

        var method = channelType.GetMethod(
            ShellStreamResizer.ResizeMethodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var parameters = method.GetParameters();
        Assert.Equal(4, parameters.Length);
        Assert.All(parameters, p => Assert.Equal(typeof(uint), p.ParameterType));
    }
}
