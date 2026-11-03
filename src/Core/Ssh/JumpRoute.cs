using Resesh.Core.Models;
using Session = Resesh.Core.Models.Session;

namespace Resesh.Core.Ssh;

/// <summary>Works out the chain of jump hosts a session is reached through.</summary>
public static class JumpRoute
{
    /// <summary>More hops than anyone chains on purpose; also bounds a damaged store.</summary>
    public const int MaxJumps = 8;

    /// <summary>
    /// The jump sessions for <paramref name="target"/>, in connection order: the first is
    /// reached from this machine, the last reaches the target. Empty when it connects directly.
    /// </summary>
    /// <exception cref="SshSessionException">A jump is missing, is not an SSH session, or the
    /// chain loops or is too long.</exception>
    public static IReadOnlyList<Session> Resolve(Session target, Func<Guid, Session?> find)
    {
        var jumps = new List<Session>();
        var seen = new HashSet<Guid> { target.Id };
        var current = target;
        while (current.JumpHostId is { } jumpId)
        {
            var jump = find(jumpId) ?? throw Failure(
                $"The jump host for {current.Name} no longer exists. Edit the session and choose another.");
            if (jump.Kind != SessionKind.Ssh)
                throw Failure($"{jump.Name} cannot be a jump host: only SSH sessions can.");
            if (!seen.Add(jump.Id))
                throw Failure($"The jump hosts for {target.Name} loop back to {jump.Name}.");
            if (jumps.Count == MaxJumps)
                throw Failure($"{target.Name} is reached through more than {MaxJumps} jump hosts.");
            jumps.Add(jump);
            current = jump;
        }
        jumps.Reverse();
        return jumps;
    }

    /// <summary>True when making <paramref name="candidateId"/> the jump of
    /// <paramref name="sessionId"/> would make a chain lead back to itself.</summary>
    public static bool WouldLoop(Guid sessionId, Guid candidateId, Func<Guid, Session?> find)
    {
        var seen = new HashSet<Guid> { sessionId };
        Guid? next = candidateId;
        while (next is { } id)
        {
            if (!seen.Add(id))
                return true;
            next = find(id)?.JumpHostId;
        }
        return false;
    }

    /// <summary>The proxy <paramref name="session"/> is dialled through, or null when it has
    /// none, it is incomplete, or a jump host reaches the session instead. The one test for
    /// "goes through a proxy": what dials, what asks for the proxy password and what describes
    /// the route all use it, so they cannot disagree about an incomplete proxy.</summary>
    public static ProxySettings? ActiveProxy(Session session) =>
        session.JumpHostId is null
        && session.Proxy is { Port: > 0 and <= 65535 } proxy && !string.IsNullOrWhiteSpace(proxy.Host)
            ? proxy
            : null;

    /// <summary>The login sent to <paramref name="session"/>'s proxy, trimmed; null when the
    /// proxy is not used or needs no login (SSH.NET then sends none).</summary>
    public static string? ProxyUsername(Session session) =>
        ActiveProxy(session) is { } proxy && !string.IsNullOrWhiteSpace(proxy.Username) ? proxy.Username.Trim() : null;

    /// <summary>Sessions that reach their host through <paramref name="jumpId"/> directly.</summary>
    public static IReadOnlyList<Session> UsersOf(Guid jumpId, IEnumerable<Session> sessions) =>
        sessions.Where(session => session.JumpHostId == jumpId).ToList();

    private static SshSessionException Failure(string message) => new(SshFailureKind.Other, message);
}
