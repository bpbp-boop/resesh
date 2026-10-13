using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class AgentPersistenceTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Listing_ReadsOurSessionsAndSkipsTheRest()
    {
        var name = TmuxPersistence.SessionName(Id, 0);
        // The first two lines are as the agent printed them on a host; the directory holds
        // a quote and a tab.
        var output =
            """{"name":"NAME-2","pid":125823,"clients":1,"created":1791931702,"pwd":"file://rct-keep/tmp/x","labels":{},"protocol":1,"agent":"0.8.1-resesh.1","fg":"sleep","cwd":"/tmp/dir with \"q\" and\ttab"}""".Replace("NAME", name) + "\r\n"
            + """{"name":"NAME","pid":123396,"clients":0,"created":1791928968,"pwd":"file://rct-keep/root/a%20b","labels":{}}""".Replace("NAME", name) + "\n"
            + """{"name":"NAME-3","error":"Timeout"}""".Replace("NAME", name) + "\n"
            + """{"name":"NAME-02","pid":1,"clients":0,"created":1}""".Replace("NAME", name) + "\n"
            + """{"name":"unrelated","pid":1,"clients":0,"created":1}""" + "\n"
            + "not json\n"
            + "[]\n";

        var sessions = AgentPersistence.ParseSessions(output, Id);

        Assert.Equal(2, sessions.Count);
        // A daemon from before the stat reply: listed, with the directory the shell last
        // reported and no foreground command.
        Assert.Equal(0, sessions[0].Slot);
        Assert.Equal("/root/a b", sessions[0].CurrentPath);
        Assert.Equal("", sessions[0].CurrentCommand);
        Assert.Equal(0, sessions[0].AttachedClients);

        Assert.Equal(1, sessions[1].Slot);
        Assert.Equal("/tmp/dir with \"q\" and\ttab", sessions[1].CurrentPath);
        Assert.Equal("sleep", sessions[1].CurrentCommand);
        Assert.Equal(1, sessions[1].AttachedClients);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791931702), sessions[1].CreatedAt);
    }

    [Fact]
    public void Listing_OfNothingIsEmpty() =>
        Assert.Empty(AgentPersistence.ParseSessions("", Id));

    [Fact]
    public void Listing_FeedsTheSamePlanAsTmux()
    {
        var name = TmuxPersistence.SessionName(Id, 0);
        var sessions = AgentPersistence.ParseSessions(
            """{"name":"NAME","pid":1,"clients":0,"created":1700000000,"protocol":1,"fg":"bash","cwd":"/home/me"}""".Replace("NAME", name), Id);

        var plan = TmuxPersistence.PlanConnect(sessions, new HashSet<int>(), Models.DetachedSessionAction.Ask);
        Assert.Equal(new TmuxConnectPlan.Resume(0), plan);
    }

    [Fact]
    public void ListCommand_KeepsSocketsUnderTheHomeDirectory() =>
        Assert.Equal(
            "ZMX_DIR=\"$HOME/.local/share/resesh/sock\" ~/.local/share/resesh/0.8.1-resesh.1/resesh-agent list --json",
            AgentPersistence.ListCommand("~/.local/share/resesh/0.8.1-resesh.1/resesh-agent"));
}
