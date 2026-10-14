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

    /// <summary>Sessions that reach their host through <paramref name="jumpId"/> directly.</summary>
    public static IReadOnlyList<Session> UsersOf(Guid jumpId, IEnumerable<Session> sessions) =>
        sessions.Where(session => session.JumpHostId == jumpId).ToList();

    private static SshSessionException Failure(string message) => new(SshFailureKind.Other, message);
}
