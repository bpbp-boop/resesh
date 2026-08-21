using Resesh.Core.Agents;

namespace Resesh.Core.Tests;

public class AgentTrackerTests
{
    private static AgentEvent Structured(string payload) =>
        AgentOsc.ParseStructured(payload) ?? throw new InvalidOperationException($"unparsed: {payload}");

    // ---- identity ----

    [Fact]
    public void StartsWithNothing()
    {
        var tracker = new AgentTracker();
        Assert.Equal(AgentSnapshot.Empty, tracker.Current);
        Assert.False(tracker.Current.IsAgent);
    }

    [Fact]
    public void CommandAtAPromptSetsIdentityAndWorking()
    {
        var tracker = new AgentTracker();
        Assert.True(tracker.ObserveCommand("claude --resume"));
        Assert.Equal("claude", tracker.Current.Key);
        Assert.Equal(AgentAttention.Working, tracker.Current.Attention);
        Assert.Equal(AgentSource.Command, tracker.Current.Source);
    }

    [Fact]
    public void PlainCommandMeansShellNotAgent()
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("ls -la");
        Assert.False(tracker.Current.IsAgent);
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
    }

    [Fact]
    public void ReachingAPromptRetiresAnAgentThatNeverSaidGoodbye()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=working"));
        Assert.True(tracker.Current.IsAgent);

        // The user is typing shell commands again: whatever owned the terminal is gone.
        tracker.ObserveCommand("git status");
        Assert.False(tracker.Current.IsAgent);
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
    }

    [Fact]
    public void TitleNamingAnAgentIsAccepted_ButSilenceIsNotEvidenceOfExit()
    {
        var tracker = new AgentTracker();
        tracker.ObserveTitle("Claude Code");
        Assert.Equal("claude", tracker.Current.Key);
        Assert.Equal(AgentSource.Title, tracker.Current.Source);

        Assert.False(tracker.ObserveTitle("bpg@host: ~/src"));
        Assert.Equal("claude", tracker.Current.Key);
    }

    [Fact]
    public void TmuxShellTitleRetiresAnAgentWithAStalePaneTitle()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=codex;state=complete"));

        Assert.True(tracker.ObserveTitle("bash"));
        Assert.False(tracker.Current.IsAgent);
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
    }

    [Fact]
    public void StructuredEventsOutrankTitles()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=codex;state=working"));
        tracker.ObserveTitle("Claude Code");
        Assert.Equal("codex", tracker.Current.Key);
    }

    [Fact]
    public void ManualOverrideOutranksEverything()
    {
        var tracker = new AgentTracker();
        tracker.SetManualOverride("gemini");
        tracker.ObserveCommand("claude");
        tracker.ObserveEvent(Structured("agent;id=codex;state=working"));
        Assert.Equal("gemini", tracker.Current.Key);
        Assert.Equal(AgentSource.Manual, tracker.Current.Source);

        tracker.SetManualOverride(null); // back to auto
        Assert.Equal("codex", tracker.Current.Key);
    }

    [Fact]
    public void SessionDefaultAppliesOnlyUntilSomethingIsObserved()
    {
        var tracker = new AgentTracker("claude");
        Assert.Equal("claude", tracker.Current.Key);

        tracker.ObserveCommand("ls");
        Assert.False(tracker.Current.IsAgent); // observation beats the default
    }

    [Fact]
    public void NoneSuppressesEverything()
    {
        var tracker = new AgentTracker(AgentIdentities.None);
        Assert.True(tracker.Suppressed);
        Assert.False(tracker.ObserveCommand("claude"));
        Assert.False(tracker.ObserveEvent(Structured("agent;id=claude;state=needs-approval")));
        Assert.Null(tracker.Current.Key);
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
    }

    [Fact]
    public void ManualNoneSuppressesASessionDefault()
    {
        var tracker = new AgentTracker("claude");
        tracker.SetManualOverride(AgentIdentities.None);
        Assert.Null(tracker.Current.Key);
    }

    // ---- local process membership ----

    [Fact]
    public void ProcessMembershipStartsAndEndsAnAgent()
    {
        var tracker = new AgentTracker();
        tracker.ObserveProcesses(["powershell", "claude"]);
        Assert.Equal("claude", tracker.Current.Key);
        Assert.Equal(AgentSource.Process, tracker.Current.Source);

        tracker.ObserveProcesses(["powershell"]);
        Assert.False(tracker.Current.IsAgent);
    }

    [Fact]
    public void ProcessDisappearanceAlsoRetiresAnAdapterReportedAgent()
    {
        // A local agent killed mid-run never emits its exit event; job membership is the truth.
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=needs-approval"));
        tracker.ObserveProcesses(["powershell"]);
        Assert.False(tracker.Current.IsAgent);
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
    }

    [Fact]
    public void RepeatedIdenticalProcessScansDoNotChangeState()
    {
        var tracker = new AgentTracker();
        Assert.True(tracker.ObserveProcesses(["claude"]));
        Assert.False(tracker.ObserveProcesses(["claude"]));
        Assert.False(tracker.ObserveProcesses(["claude", "git"]));
    }

    // ---- attention ----

    [Fact]
    public void StructuredEventsDriveAttention()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=working"));
        Assert.Equal(AgentAttention.Working, tracker.Current.Attention);

        tracker.ObserveEvent(Structured("agent;state=needs-approval;label=Delete%20file"));
        Assert.Equal(AgentAttention.NeedsApproval, tracker.Current.Attention);
        Assert.Equal("Delete file", tracker.Current.Label);
        Assert.Equal("claude", tracker.Current.Key); // a state-only event keeps the identity

        tracker.ObserveEvent(Structured("agent;state=complete"));
        Assert.Equal(AgentAttention.Complete, tracker.Current.Attention);
    }

    [Fact]
    public void BellIsIgnoredWithoutAnAgent()
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("make");
        Assert.False(tracker.ObserveEvent(AgentOsc.Bell()));
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
    }

    [Fact]
    public void BellRaisesOnlyTheLowConfidenceSignalState()
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("claude");
        Assert.True(tracker.ObserveEvent(AgentOsc.Bell()));
        Assert.Equal(AgentAttention.Signal, tracker.Current.Attention);
        Assert.False(tracker.Current.Attention.RequiresUser()); // never claims input is needed
    }

    [Fact]
    public void BellsAreIgnoredOnceAnAdapterHasReported()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=working"));
        Assert.False(tracker.ObserveEvent(AgentOsc.Bell()));
        Assert.Equal(AgentAttention.Working, tracker.Current.Attention);
    }

    [Fact]
    public void BellNeverDowngradesABlockedAgent()
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("claude");
        tracker.ObserveEvent(new AgentEvent("claude", AgentAttention.NeedsApproval, null, AgentSource.Structured));
        tracker.ObserveEvent(AgentOsc.Bell());
        Assert.Equal(AgentAttention.NeedsApproval, tracker.Current.Attention);
    }

    [Fact]
    public void ViewingClearsSeenBadgesButNotABlockedAgent()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=complete"));
        Assert.True(tracker.ObserveViewed());
        Assert.Equal(AgentAttention.Idle, tracker.Current.Attention);

        tracker.ObserveEvent(Structured("agent;state=needs-answer"));
        Assert.False(tracker.ObserveViewed());
        Assert.Equal(AgentAttention.NeedsAnswer, tracker.Current.Attention);
    }

    [Fact]
    public void AnsweringClearsTheBlockedBadge()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=needs-answer"));
        Assert.True(tracker.ObserveUserInput());
        Assert.Equal(AgentAttention.Working, tracker.Current.Attention);
        Assert.False(tracker.ObserveUserInput()); // idempotent while working
    }

    [Fact]
    public void EndingTheShellClearsDetectionButKeepsTheUsersChoice()
    {
        var tracker = new AgentTracker();
        tracker.SetManualOverride("claude");
        tracker.ObserveEvent(Structured("agent;state=needs-approval"));
        tracker.ObserveEnded();
        Assert.Equal("claude", tracker.Current.Key);
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
    }

    [Fact]
    public void AttentionIsNeverShownForAPlainShell()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=needs-approval"));
        tracker.ObserveCommand("ls");
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
        Assert.Null(tracker.Current.Label);
    }
}
