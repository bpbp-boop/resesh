using System.Diagnostics.CodeAnalysis;
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

    // The channel's runtime type is SSH.NET's internal ChannelSession, kept for trimming by
    // the DynamicDependency below; the lookup cannot be annotated statically.
    [DynamicDependency(IntegratedShellStream.ChannelMembers, IntegratedShellStream.ChannelTypeName, "Renci.SshNet")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "DynamicDependency keeps the channel's methods.")]
    /// <summary>Sends a window-change request. The pixel size is the text area's, so a
    /// program asking the pty (TIOCGWINSZ) and one asking the terminal (CSI 14 t) agree;
    /// 0 means unknown. False when nothing was sent (no channel, or it closed).</summary>
    public static bool TryResize(ShellStream? shell, int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
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

            _resizeMethod.Invoke(channel,
                [(uint)columns, (uint)rows, (uint)Math.Max(0, pixelWidth), (uint)Math.Max(0, pixelHeight)]);
            return true;
        }
        catch (TargetInvocationException)
        {
            return false; // channel already closed
        }
    }
}
