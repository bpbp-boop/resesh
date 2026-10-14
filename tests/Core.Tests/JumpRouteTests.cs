using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class JumpRouteTests
{
    private static Session Ssh(string name, Session? jump = null) =>
        new() { Name = name, Host = name, Username = "admin", JumpHostId = jump?.Id };

    private static Func<Guid, Session?> Finder(params Session[] sessions) =>
        id => sessions.FirstOrDefault(session => session.Id == id);

    [Fact]
    public void DirectSessionHasNoJumps()
    {
        Assert.Empty(JumpRoute.Resolve(Ssh("web"), Finder()));
    }

    [Fact]
    public void JumpsAreListedInConnectionOrder()
    {
        var edge = Ssh("edge");
        var core = Ssh("core", edge);
        var target = Ssh("db", core);

        var jumps = JumpRoute.Resolve(target, Finder(edge, core, target));

        Assert.Equal(["edge", "core"], jumps.Select(jump => jump.Name));
    }

    [Fact]
    public void MissingJumpIsReportedAgainstTheSessionThatNamesIt()
    {
        var target = Ssh("db") with { JumpHostId = Guid.NewGuid() };

        var failure = Assert.Throws<SshSessionException>(() => JumpRoute.Resolve(target, Finder(target)));

        Assert.Contains("jump host for db no longer exists", failure.Message);
    }

    [Fact]
    public void LoopIsRefused()
    {
        var a = Ssh("a");
        var b = Ssh("b", a);
        a = a with { JumpHostId = b.Id };

        var failure = Assert.Throws<SshSessionException>(() => JumpRoute.Resolve(a, Finder(a, b)));

        Assert.Contains("loop back", failure.Message);
    }

    [Fact]
    public void SessionCannotJumpThroughItself()
    {
        var a = Ssh("a");
        a = a with { JumpHostId = a.Id };

        Assert.Throws<SshSessionException>(() => JumpRoute.Resolve(a, Finder(a)));
    }

    [Fact]
    public void TelnetSessionCannotBeAJump()
    {
        var console = new Session { Name = "console", Host = "console", Kind = SessionKind.Telnet };
        var target = Ssh("db", console);

        var failure = Assert.Throws<SshSessionException>(() => JumpRoute.Resolve(target, Finder(console, target)));

        Assert.Contains("only SSH sessions can", failure.Message);
    }

    [Fact]
    public void OverlongChainIsRefused()
    {
        var sessions = new List<Session> { Ssh("hop0") };
        for (var index = 1; index <= JumpRoute.MaxJumps + 1; index++)
            sessions.Add(Ssh($"hop{index}", sessions[^1]));

        Assert.Throws<SshSessionException>(() => JumpRoute.Resolve(sessions[^1], Finder([.. sessions])));
        Assert.Equal(JumpRoute.MaxJumps, JumpRoute.Resolve(sessions[^2], Finder([.. sessions])).Count);
    }

    [Fact]
    public void WouldLoopCatchesDirectAndIndirectLoops()
    {
        var edge = Ssh("edge");
        var core = Ssh("core", edge);
        var db = Ssh("db", core);
        var find = Finder(edge, core, db);

        Assert.True(JumpRoute.WouldLoop(edge.Id, edge.Id, find));
        Assert.True(JumpRoute.WouldLoop(edge.Id, db.Id, find));
        Assert.False(JumpRoute.WouldLoop(db.Id, edge.Id, find));
    }

    [Fact]
    public void UsersOfFindsSessionsThatJumpThroughASession()
    {
        var edge = Ssh("edge");
        var core = Ssh("core", edge);
        var db = Ssh("db", core);

        Assert.Equal([core], JumpRoute.UsersOf(edge.Id, [edge, core, db]));
    }
}
