using Renci.SshNet;
using Renci.SshNet.Common;
using Session = Sessions.Core.Models.Session;

namespace Sessions.Core.Ssh;

public sealed record KeyboardInteractivePrompt(string Text, bool IsSecret);

/// <summary>
/// Connection plumbing shared by the terminal (SshClient) and the file pane (SftpClient):
/// auth-method construction, failure classification, and the fast TCP preflight.
/// Auth methods are stateful in SSH.NET — build a fresh set per client, never share them.
/// </summary>
internal static class SshConnectionFactory
{
    internal static AuthenticationMethod[] BuildAuthMethods(
        Session session,
        string? secret,
        Func<IReadOnlyList<KeyboardInteractivePrompt>, IReadOnlyList<string>?>? interactiveResponder = null)
    {
        var user = session.Username;
        switch (session.AuthMethod)
        {
            case Models.AuthMethod.Password:
            {
                var password = new PasswordAuthenticationMethod(user, secret ?? "");
                // Some servers demand keyboard-interactive. Every prompt must be answered
                // explicitly; never send the saved password to an arbitrary challenge.
                var interactive = new KeyboardInteractiveAuthenticationMethod(user);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    var requests = e.Prompts
                        .Select(prompt => new KeyboardInteractivePrompt(prompt.Request, !prompt.IsEchoed))
                        .ToList();
                    var responses = interactiveResponder?.Invoke(requests);
                    if (responses is null || responses.Count != e.Prompts.Count)
                        return;
                    for (var index = 0; index < e.Prompts.Count; index++)
                        e.Prompts[index].Response = responses[index];
                };
                return [password, interactive];
            }
            case Models.AuthMethod.PrivateKey:
            {
                var keyFile = string.IsNullOrEmpty(secret)
                    ? new PrivateKeyFile(session.PrivateKeyPath!)
                    : new PrivateKeyFile(session.PrivateKeyPath!, secret);
                return [new PrivateKeyAuthenticationMethod(user, keyFile)];
            }
            default:
                return [new NoneAuthenticationMethod(user)];
        }
    }

    internal static SshSessionException Classify(Exception ex) => ex switch
    {
        SshAuthenticationException => new SshSessionException(
            SshFailureKind.AuthenticationFailed, "Authentication failed — check the username and credential.", ex),
        SshConnectionException or System.Net.Sockets.SocketException => new SshSessionException(
            SshFailureKind.HostUnreachable, $"Could not reach the host: {ex.Message}", ex),
        _ => new SshSessionException(SshFailureKind.Other, ex.Message, ex),
    };

    internal static void PreflightTcp(string host, int port, TimeSpan timeout)
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        try
        {
            if (!socket.ConnectAsync(host, port).Wait(timeout))
                throw new SshSessionException(
                    SshFailureKind.HostUnreachable, $"Could not reach {host}:{port} (connection timed out).");
        }
        catch (AggregateException e) when (e.InnerException is System.Net.Sockets.SocketException se)
        {
            throw new SshSessionException(
                SshFailureKind.HostUnreachable, $"Could not reach {host}:{port}: {se.Message}", se);
        }
    }
}
