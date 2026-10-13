using Resesh.Core.Agents;
using Resesh.Core.Models;

namespace Resesh.Core.Tests;

public class ProgramStatusTests
{
    private static ProgramStatus Report(int state, int kind = 0, string id = "", string app = "", string message = "") =>
        ProgramStatus.Parse($"{state};{kind};-1\u001F{id}\u001F{app}\u001F\u001F{message}")
        ?? throw new InvalidOperationException("unparsed");

    [Fact]
    public void ParsesTheShimPayload()
    {
        var status = ProgramStatus.Parse("3;1;40\u001F\u001FOMP\u001FDeploy\u001FApply; these: changes?");
        Assert.NotNull(status);
        Assert.Equal(ProgramStatusState.Blocked, status.State);
        Assert.Equal(ProgramStatusKind.Permission, status.Kind);
        Assert.Equal(40, status.Progress);
        Assert.Equal("", status.Id);
        Assert.Equal("omp", status.App);
        Assert.Equal("Deploy", status.Title);
        Assert.Equal("Apply; these: changes?", status.Label);
    }

    [Theory]
    [InlineData(0, 0, AgentAttention.Idle)]
    [InlineData(1, 0, AgentAttention.Working)]
    [InlineData(2, 0, AgentAttention.Complete)]
    [InlineData(3, 1, AgentAttention.NeedsApproval)]
    [InlineData(3, 2, AgentAttention.NeedsAnswer)]
    [InlineData(3, 3, AgentAttention.NeedsAnswer)]
    [InlineData(3, 0, AgentAttention.NeedsAnswer)]
    [InlineData(3, 99, AgentAttention.NeedsAnswer)]   // a kind this version does not know
    [InlineData(4, 0, AgentAttention.Failed)]
    public void MapsStatesOntoAttention(int state, int kind, AgentAttention expected) =>
        Assert.Equal(expected, Report(state, kind).Attention);

    [Theory]
    [InlineData("1;0;40", TerminalProgressState.Normal, 40)]
    [InlineData("3;1;40", TerminalProgressState.Paused, 40)]
    [InlineData("1;0;-1", TerminalProgressState.None, 0)]    // working, no percentage: no bar
    [InlineData("2;0;-1", TerminalProgressState.None, 0)]
    [InlineData("4;0;-1", TerminalProgressState.None, 0)]
    [InlineData("0;0;-1", TerminalProgressState.None, 0)]
    [InlineData("5;0;-1", TerminalProgressState.None, 0)]
    [InlineData("1;0;250", TerminalProgressState.None, 0)]   // out of range reads as not sent
    public void ARootReportDrivesTheProgressBar(string head, TerminalProgressState state, int value) =>
        Assert.Equal(new TerminalProgress(state, value),
            ProgramStatus.Parse(head + "cargo")!.AsTerminalProgress());

    [Fact]
    public void AChildRecordLeavesTheProgressBarAlone() =>
        Assert.Null(ProgramStatus.Parse("1;0;40build/testcargo")!.AsTerminalProgress());

    [Fact]
    public void ParsesAReportAsAProgramWritesIt()
    {
        var status = ProgramStatus.ParseReport("state=blocked:kind=permission:app=Terraform:progress=40:msg=QXBwbHk/:n=77");
        Assert.NotNull(status);
        Assert.Equal(ProgramStatusState.Blocked, status.State);
        Assert.Equal(ProgramStatusKind.Permission, status.Kind);
        Assert.Equal(40, status.Progress);
        Assert.Equal("terraform", status.App);
        Assert.Equal("Apply?", status.Label);
        Assert.Equal("", status.Id);

        Assert.Equal(ProgramStatusState.Clear, ProgramStatus.ParseReport("state=clear")!.State);
        Assert.Equal("build/test", ProgramStatus.ParseReport("state=working:id=build/test")!.Id);
        // Kind and progress belong to the states that can have them.
        var done = ProgramStatus.ParseReport("state=done:kind=permission:progress=40")!;
        Assert.Equal(ProgramStatusKind.None, done.Kind);
        Assert.Equal(-1, done.Progress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("app=terraform")]            // no state
    [InlineData("state=exploding")]
    [InlineData("working")]
    public void RejectsReportsWithoutAKnownState(string? body) =>
        Assert.Null(ProgramStatus.ParseReport(body));

    [Fact]
    public void AReportWithUnreadableTextKeepsItsState()
    {
        var status = ProgramStatus.ParseReport("state=error:msg=not base64!");
        Assert.NotNull(status);
        Assert.Equal(ProgramStatusState.Error, status.State);
        Assert.Null(status.Message);
    }

    [Theory]
    [InlineData("node [[resesh-status:state=working:app=claude:n=12]]", "node", "state=working:app=claude:n=12")]
    [InlineData("✳ Fix the build [[resesh-status:state=done:app=claude]]", "✳ Fix the build", "state=done:app=claude")]
    // A program can write a marker into its own title; the genuine one is appended last.
    [InlineData("x [[resesh-status:state=error]] [[resesh-status:state=idle:app=codex]]",
        "x [[resesh-status:state=error]]", "state=idle:app=codex")]
    public void ATmuxTitleSplitsIntoTitleAndReport(string title, string expectedTitle, string expectedReport)
    {
        Assert.Equal(expectedTitle, TmuxStatus.SplitTitle(title, out var report));
        Assert.Equal(expectedReport, report);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("root@host:~/src")]
    [InlineData("notes [[draft]]")]
    [InlineData("[[resesh-status:state=working")]   // cut off before its end
    [InlineData("")]
    public void ATitleWithoutAReportIsLeftAlone(string title)
    {
        Assert.Equal(title, TmuxStatus.SplitTitle(title, out var report));
        Assert.Null(report);
    }

    [Fact]
    public void BlockedOnAuthWithoutAMessageSaysSo() =>
        Assert.Equal("needs sign-in", Report(3, 3).Label);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1;0")]
    [InlineData("x;0;-1")]
    [InlineData("9;0;-1")]            // unknown state
    public void RejectsMalformedPayloads(string? payload) =>
        Assert.Null(ProgramStatus.Parse(payload));

    [Fact]
    public void ANamedAgentSetsIdentityAndState()
    {
        var tracker = new AgentTracker();
        Assert.True(tracker.ObserveProgramStatus(Report(1, app: "omp", message: "Reading files")));
        Assert.Equal("omp", tracker.Current.Key);
        Assert.Equal(AgentAttention.Working, tracker.Current.Attention);
        Assert.Equal("Reading files", tracker.Current.Label);
        Assert.Equal(AgentSource.Structured, tracker.Current.Source);

        tracker.ObserveProgramStatus(Report(3, kind: 1, app: "omp"));
        Assert.Equal(AgentAttention.NeedsApproval, tracker.Current.Attention);
        Assert.Null(tracker.Current.Label);
    }

    [Fact]
    public void AnOrdinaryProgramGetsTheStateWithoutAnIdentity()
    {
        var tracker = new AgentTracker();
        Assert.True(tracker.ObserveProgramStatus(Report(3, kind: 1, app: "terraform", message: "Apply?")));
        var snapshot = tracker.Current;
        Assert.False(snapshot.IsAgent);
        Assert.Null(snapshot.Key);
        Assert.Equal("terraform", snapshot.Program);
        Assert.Equal(AgentAttention.NeedsApproval, snapshot.Attention);
        Assert.Equal("Apply?", snapshot.Label);
        Assert.Equal(AgentSource.Structured, snapshot.Source);

        // A program that gives no name still reports; one at rest has nothing to show.
        var unnamed = new AgentTracker();
        Assert.True(unnamed.ObserveProgramStatus(Report(4)));
        Assert.Equal("", unnamed.Current.Program);
        Assert.Equal(AgentAttention.Failed, unnamed.Current.Attention);
        unnamed.ObserveProgramStatus(Report(0));
        Assert.Equal(AgentSnapshot.Empty, unnamed.Current);
    }

    [Fact]
    public void AnOrdinaryProgramDoesNotTouchAnAgentThatIsShowing()
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("claude");
        Assert.False(tracker.ObserveProgramStatus(Report(3, kind: 1, app: "terraform")));
        Assert.Equal("claude", tracker.Current.Key);
        Assert.Equal(AgentAttention.Working, tracker.Current.Attention);
        Assert.Null(tracker.Current.Program);
    }

    [Theory]
    [InlineData(2, AgentAttention.Complete)]
    [InlineData(4, AgentAttention.Failed)]
    public void AFinishedResultOutlivesItsProgramUntilItIsSeen(int state, AgentAttention expected)
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("make deploy");
        tracker.ObserveProgramStatus(Report(1, app: "make"));
        tracker.ObserveProgramStatus(Report(state, app: "make", message: "3 targets"));

        // The program exits and the prompt returns, more than once.
        tracker.ObserveCommand("");
        tracker.ObserveCommand("");
        tracker.ObserveTitle("root@host:~");
        Assert.Equal(expected, tracker.Current.Attention);
        Assert.Equal("make", tracker.Current.Program);
        Assert.Equal("3 targets", tracker.Current.Label);

        Assert.True(tracker.ObserveViewed());
        Assert.Null(tracker.Current.Program);
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
    }

    [Theory]
    [InlineData(1, 0)]   // working
    [InlineData(3, 1)]   // blocked
    public void AnUnfinishedStateEndsWithItsProgram(int state, int kind)
    {
        var tracker = new AgentTracker();
        tracker.ObserveProgramStatus(Report(state, kind, app: "terraform"));
        Assert.True(tracker.ObserveCommand(""));
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
        Assert.Null(tracker.Current.Program);
    }

    [Fact]
    public void TypingAnswersABlockedProgramAndDismissesAFinishedOne()
    {
        var tracker = new AgentTracker();
        tracker.ObserveProgramStatus(Report(3, kind: 2, app: "terraform"));
        Assert.True(tracker.ObserveUserInput());
        Assert.Equal(AgentAttention.Working, tracker.Current.Attention);
        Assert.Equal("terraform", tracker.Current.Program);

        tracker.ObserveProgramStatus(Report(2, app: "terraform"));
        Assert.True(tracker.ObserveUserInput());
        Assert.Null(tracker.Current.Program);
    }

    [Fact]
    public void ClearAndSuppressionRemoveAProgramsState()
    {
        var tracker = new AgentTracker();
        tracker.ObserveProgramStatus(Report(4, app: "terraform"));
        Assert.True(tracker.ObserveProgramStatus(Report(5)));
        Assert.Equal(AgentSnapshot.Empty, tracker.Current);

        var suppressed = new AgentTracker(AgentIdentities.None);
        Assert.False(suppressed.ObserveProgramStatus(Report(4, app: "terraform")));
        Assert.Null(suppressed.Current.Program);
    }

    [Fact]
    public void AnAgentStartingTakesOverFromAProgramsState()
    {
        var tracker = new AgentTracker();
        tracker.ObserveProgramStatus(Report(2, app: "make"));
        tracker.ObserveProgramStatus(Report(1, app: "omp"));
        Assert.Equal("omp", tracker.Current.Key);
        Assert.Null(tracker.Current.Program);

        // ...and the old result does not come back when the agent leaves.
        tracker.ObserveCommand("");
        Assert.Null(tracker.Current.Program);
    }

    [Fact]
    public void AnUnnamedReportUpdatesTheAgentAlreadyShowing()
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("codex");
        Assert.True(tracker.ObserveProgramStatus(Report(2)));
        Assert.Equal("codex", tracker.Current.Key);
        Assert.Equal(AgentAttention.Complete, tracker.Current.Attention);
    }

    [Fact]
    public void ChildRecordsAreNotShown()
    {
        var tracker = new AgentTracker();
        tracker.ObserveProgramStatus(Report(0, app: "omp"));
        Assert.False(tracker.ObserveProgramStatus(Report(4, id: "build/test", app: "omp")));
        Assert.Equal(AgentAttention.Idle, tracker.Current.Attention);
    }

    [Fact]
    public void ClearFallsBackToDetection()
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("omp");
        tracker.ObserveProgramStatus(Report(3, kind: 2, app: "omp", message: "Which branch?"));
        Assert.True(tracker.ObserveProgramStatus(Report(5)));
        Assert.Equal("omp", tracker.Current.Key);
        Assert.Equal(AgentAttention.Idle, tracker.Current.Attention);
        Assert.Null(tracker.Current.Label);

        // With nothing detected underneath, the agent goes with its record.
        var bare = new AgentTracker();
        bare.ObserveProgramStatus(Report(1, app: "omp"));
        bare.ObserveProgramStatus(Report(5));
        Assert.False(bare.Current.IsAgent);
    }

    [Fact]
    public void ReachingAPromptRetiresTheReportedAgent()
    {
        var tracker = new AgentTracker();
        tracker.ObserveProgramStatus(Report(1, app: "omp"));
        tracker.ObserveCommand("");
        Assert.False(tracker.Current.IsAgent);
    }
}
