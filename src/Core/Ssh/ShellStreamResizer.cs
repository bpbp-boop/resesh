using System.Reflection;
using Renci.SshNet;

namespace Resesh.Core.Ssh;

/// <summary>
/// SSH.NET (as of the pinned version) exposes no public window-change request on
/// <see cref="ShellStream"/>, so live terminal resize reaches the private session channel
/// via reflection. A unit test pins the member names so a package bump that breaks this
/// fails loudly instead of silently. (Planned fallback if it ever breaks: recreate the
/// shell at the new size.)
/// </summary>
public static class ShellStreamResizer
{
    public const string ChannelFieldName = "_channel";
    public const string ResizeMethodName = "SendWindowChangeRequest";

    private static readonly FieldInfo? ChannelField =
        typeof(ShellStream).GetField(ChannelFieldName, BindingFlags.Instance | BindingFlags.NonPublic);

    private static MethodInfo? _resizeMethod;

    public static bool IsSupported => ChannelField is not null;

    public static bool TryResize(ShellStream? shell, int columns, int rows)
    {
        if (shell is null || ChannelField is null || columns <= 0 || rows <= 0)
            return false;

        try
        {
            var channel = ChannelField.GetValue(shell);
            if (channel is null)
                return false;

            _resizeMethod ??= channel.GetType().GetMethod(
                ResizeMethodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_resizeMethod is null)
                return false;

            _resizeMethod.Invoke(channel, [(uint)columns, (uint)rows, 0u, 0u]);
            return true;
        }
        catch (TargetInvocationException)
        {
            return false; // channel already closed
        }
    }
}
