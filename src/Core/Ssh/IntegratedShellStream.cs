using System.Reflection;
using System.Runtime.ExceptionServices;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Resesh.Core.Ssh;

/// <summary>Creates a ShellStream with PTY + exec instead of PTY + shell. SSH.NET's
/// public API cannot select the initial command. Keep this pinned compatibility seam
/// beside the existing resize adapter; never fall back to typing setup into a terminal.</summary>
internal static class IntegratedShellStream
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly PropertyInfo? SessionProperty = typeof(BaseClient).GetProperty("Session", InstanceMembers);
    private static readonly ConstructorInfo? Constructor = typeof(ShellStream).GetConstructors(InstanceMembers)
        .SingleOrDefault(c => c.GetParameters() is var p && p.Length == 3 &&
            p[0].ParameterType == SessionProperty?.PropertyType && p[1].ParameterType == typeof(int) &&
            p[2].ParameterType == typeof(bool));
    private static readonly FieldInfo? ChannelField = typeof(ShellStream).GetField("_channel", InstanceMembers);
    private static readonly Type? ChannelType = typeof(SshClient).Assembly.GetType("Renci.SshNet.Channels.ChannelSession");
    private static readonly MethodInfo? OpenMethod = ChannelType?.GetMethod("Open", InstanceMembers, []);
    private static readonly MethodInfo? PtyMethod = ChannelType?.GetMethod("SendPseudoTerminalRequest", InstanceMembers,
        [typeof(string), typeof(uint), typeof(uint), typeof(uint), typeof(uint), typeof(IDictionary<TerminalModes, uint>)]);
    private static readonly MethodInfo? ExecMethod = ChannelType?.GetMethod("SendExecRequest", InstanceMembers, [typeof(string)]);

    public static bool IsSupported => SessionProperty is not null && Constructor is not null &&
        ChannelField is not null && OpenMethod is not null && PtyMethod is not null && ExecMethod is not null;

    public static ShellStream Create(SshClient client, string terminalType, int columns, int rows,
        string command, CancellationToken token)
    {
        if (!IsSupported)
            throw new NotSupportedException("This SSH.NET version cannot start an integrated shell.");
        token.ThrowIfCancellationRequested();
        var session = SessionProperty!.GetValue(client) ?? throw new InvalidOperationException("SSH is not connected.");
        var stream = (ShellStream)Constructor!.Invoke([session, 64 * 1024, false]);
        try
        {
            var channel = ChannelField!.GetValue(stream)!;
            // Dispose the channel if startup is cancelled while waiting for a request reply.
            using var cancellation = token.Register(() => stream.Dispose());
            OpenMethod!.Invoke(channel, null);
            token.ThrowIfCancellationRequested();
            if (PtyMethod!.Invoke(channel,
                    [terminalType, (uint)columns, (uint)rows, 0u, 0u, new Dictionary<TerminalModes, uint>()]) is not true)
                throw new SshException("The server rejected the terminal request.");
            token.ThrowIfCancellationRequested();
            if (ExecMethod!.Invoke(channel, [command]) is not true)
                throw new SshException("The server rejected shell integration startup.");
            token.ThrowIfCancellationRequested();
            return stream;
        }
        catch (Exception ex)
        {
            stream.Dispose();
            token.ThrowIfCancellationRequested();
            if (ex is TargetInvocationException { InnerException: { } inner })
                ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }
}
