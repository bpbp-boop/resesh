using System.Reflection;
using System.Runtime.ExceptionServices;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Resesh.Core.Ssh;

/// <summary>Creates a ShellStream with an environment request, a PTY, and either exec or
/// shell. SSH.NET's public API can neither select the initial command nor send environment
/// variables. Keep this pinned compatibility seam beside the existing resize adapter; never
/// fall back to typing setup into a terminal.</summary>
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
    private static readonly MethodInfo? ShellMethod = ChannelType?.GetMethod("SendShellRequest", InstanceMembers, []);
    private static readonly MethodInfo? EnvironmentMethod = ChannelType?.GetMethod("SendEnvironmentVariableRequest", InstanceMembers,
        [typeof(string), typeof(string)]);

    /// <summary>Offered on every session channel. SSH forwards TERM but not COLORTERM, and
    /// the terminal renders 24-bit colour. Servers only accept names listed in AcceptEnv;
    /// a refusal is harmless, and shell integration also exports it.</summary>
    internal static readonly IReadOnlyList<KeyValuePair<string, string>> SessionEnvironment =
        [new("COLORTERM", "truecolor")];

    public static bool IsSupported => SessionProperty is not null && Constructor is not null &&
        ChannelField is not null && OpenMethod is not null && PtyMethod is not null && ExecMethod is not null;

    /// <summary>Whether a plain interactive shell can be started with the environment request.</summary>
    public static bool IsShellSupported => IsSupported && ShellMethod is not null && EnvironmentMethod is not null;

    /// <summary>Starts <paramref name="command"/> with exec, or the account's login shell
    /// when it is null.</summary>
    public static ShellStream Create(SshClient client, string terminalType, int columns, int rows,
        string? command, CancellationToken token)
    {
        if (command is null ? !IsShellSupported : !IsSupported)
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
            if (EnvironmentMethod is not null)
            {
                foreach (var (name, value) in SessionEnvironment)
                {
                    EnvironmentMethod.Invoke(channel, [name, value]); // false = not in AcceptEnv
                    token.ThrowIfCancellationRequested();
                }
            }
            if (PtyMethod!.Invoke(channel,
                    [terminalType, (uint)columns, (uint)rows, 0u, 0u, new Dictionary<TerminalModes, uint>()]) is not true)
                throw new SshException("The server rejected the terminal request.");
            token.ThrowIfCancellationRequested();
            if (command is null)
            {
                if (ShellMethod!.Invoke(channel, null) is not true)
                    throw new SshException("The server rejected the shell request.");
            }
            else if (ExecMethod!.Invoke(channel, [command]) is not true)
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
