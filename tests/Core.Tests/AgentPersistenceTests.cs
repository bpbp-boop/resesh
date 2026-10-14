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

    private const string Path = "\"$HOME/.local/share/resesh/0.8.1-resesh.1/resesh-agent\"";
    private const string Agent = "env ZMX_DIR=\"$HOME/.local/share/resesh/sock\" ZMX_NO_DETACH_KEY=1 " + Path;

    [Fact]
    public void Commands_KeepSocketsUnderTheHomeDirectoryAndTheDetachKeyOff()
    {
        var name = TmuxPersistence.SessionName(Id, 1);
        Assert.Equal($"{Agent} list --json", AgentPersistence.ListCommand(Path));
        Assert.Equal($"{Agent} kill {name}", AgentPersistence.KillCommand(Path, Id, 1));
        Assert.Equal(
            $"{Agent} kill {TmuxPersistence.SessionName(Id, 0)}; {Agent} kill {name}",
            AgentPersistence.KillCommand(Path, Id, [1, 0, 1]));
    }

    [Fact]
    public void Bootstrap_AttachesOrStartsAndFallsBackLikeTmux()
    {
        var name = TmuxPersistence.SessionName(Id, 0);
        Assert.Equal(
            $" printf '\\033[2J\\033[3J\\033[H'; exec {Agent} attach {name} # resesh-agent-bootstrap",
            AgentPersistence.BootstrapCommand(Path, Id, 0));

        var withShell = AgentPersistence.BootstrapCommand(Path, Id, 0, "exec bash -l", "sh -i");
        Assert.Contains($"; {Agent} attach {name} sh -c 'exec bash -l'; resesh_result=$?", withShell);
        Assert.DoesNotContain("exec env", withShell);
        Assert.Contains("opening a non-persistent shell.\\n'; exec sh -i # resesh-agent-bootstrap", withShell);
    }

    [Fact]
    public void Resume_NeverStartsAShellThatHasEnded()
    {
        var name = TmuxPersistence.SessionName(Id, 2);
        var command = AgentPersistence.ResumeCommand(Path, Id, 2);
        Assert.Contains($"if {Agent} list --short | grep -qx {name}; then exec {Agent} attach {name}; else", command);
        Assert.EndsWith("exit 1; fi", command);
    }

    [Fact]
    public void Backends_AnswerTheSameQuestions()
    {
        var name = TmuxPersistence.SessionName(Id, 0);
        var agent = PersistenceBackend.Agent(Path);
        Assert.False(agent.StatusRidesOnTitle);
        Assert.True(PersistenceBackend.Tmux.StatusRidesOnTitle);

        var listing = """{"name":"NAME","pid":1,"clients":0,"created":1700000000,"protocol":1,"fg":"vim","cwd":"/srv/app"}""".Replace("NAME", name);
        Assert.Equal("vim", Assert.Single(agent.ParseManagedSessions(listing, Id)).CurrentCommand);
        Assert.Equal("/srv/app", agent.ParseCurrentPath(listing, Id, 0));
        Assert.Null(agent.ParseCurrentPath(listing, Id, 1));
        // Nothing running is an empty list, not a failure to explain away.
        Assert.False(agent.IsAbsent(new SshCommandResult(false, "", "no server running on /tmp/x")));
        Assert.True(PersistenceBackend.Tmux.IsAbsent(new SshCommandResult(false, "", "no server running on /tmp/x")));

        Assert.Equal(TmuxPersistence.ResumeCommand(Id, 0), PersistenceBackend.Tmux.ResumeCommand(Id, 0));
        Assert.Equal(AgentPersistence.ResumeCommand(Path, Id, 0), agent.ResumeCommand(Id, 0));
    }
}
