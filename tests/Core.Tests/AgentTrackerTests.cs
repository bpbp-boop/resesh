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
    public void CommandAtAPromptSetsIdentityButNotActivity()
    {
        var tracker = new AgentTracker();
        Assert.True(tracker.ObserveCommand("claude --resume"));
        Assert.Equal("claude", tracker.Current.Key);
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
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
    public void CommandEndRetiresAnAgentThatNeverSaidGoodbye()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=codex;state=working"));
        Assert.True(tracker.Current.IsAgent);

        // CommandEnded ("") arriving from OSC 133;D / prompt context
        tracker.ObserveCommand("");
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

        Assert.False(tracker.ObserveTitle("project dashboard"));
        Assert.Equal("claude", tracker.Current.Key);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("root@rct-keep:/srv/rct-keep")]
    public void ShellTitleRetiresAnAgentWithAStaleTitle(string shellTitle)
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=codex;state=complete"));

        Assert.True(tracker.ObserveTitle(shellTitle));
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
    public void AgentRunningNeedsEvidenceNotADefaultOrAPinnedIcon()
    {
        var tracker = new AgentTracker("claude");
        Assert.True(tracker.Current.IsAgent);
        Assert.False(tracker.AgentRunning);  // a shell prompt under the session's icon

        tracker.SetManualOverride("codex");
        Assert.False(tracker.AgentRunning);
        tracker.ObserveCommand("claude");
        Assert.True(tracker.AgentRunning);
        tracker.ObserveCommand("");          // back at a prompt; the pinned icon stays
        Assert.False(tracker.AgentRunning);
        Assert.Equal("codex", tracker.Current.Key);
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
        Assert.Equal(AgentAttention.None, tracker.Current.Attention); // running is not working

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

    [Fact]
    public void TitleDoesNotRenameAnAgentFoundInTheProcesses()
    {
        // Codex's spinner frames name Codex; over a claude process they used to flip the
        // icon on every frame against the next process poll.
        var tracker = new AgentTracker();
        tracker.ObserveProcesses(["powershell", "claude"]);
        tracker.ObserveTitle("⠋ Fix login bug");
        Assert.Equal("claude", tracker.Current.Key);
        Assert.Equal(AgentAttention.Working, tracker.Current.Attention); // the spinner still counts

        // A title still names an agent the processes cannot see (one inside ssh or wsl).
        tracker.ObserveProcesses(["powershell", "ssh"]);
        tracker.ObserveTitle("⠋ resesh");
        Assert.Equal("codex", tracker.Current.Key);
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
    public void InputAtAFinishedAgentNeverRestartsWorking()
    {
        // Viewing the result and then typing (or the agent's prompt getting any input at
        // all) used to turn the badge back to working with nothing left to end it.
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=complete"));
        tracker.ObserveViewed();
        Assert.False(tracker.ObserveUserInput());
        Assert.Equal(AgentAttention.Idle, tracker.Current.Attention);

        tracker.ObserveEvent(Structured("agent;state=complete"));
        Assert.True(tracker.ObserveUserInput()); // acting on the result is seeing it
        Assert.Equal(AgentAttention.Idle, tracker.Current.Attention);
    }

    // ---- the agent's own title spinner ----

    [Fact]
    public void TitleSpinnerMarksWorkAndItsEndMarksIdle()
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("claude");
        Assert.True(tracker.ObserveTitle("⠐ Fix login bug"));
        Assert.Equal(AgentAttention.Working, tracker.Current.Attention);
        Assert.False(tracker.ObserveTitle("⠂ Fix login bug"));

        Assert.True(tracker.ObserveTitle("✳ Fix login bug"));
        Assert.Equal(AgentAttention.Idle, tracker.Current.Attention);
    }

    [Fact]
    public void CodexSpinnerNamesTheAgentAndStoppingItEndsWork()
    {
        var tracker = new AgentTracker();
        tracker.ObserveTitle("⠋ resesh");
        Assert.Equal("codex", tracker.Current.Key);
        Assert.Equal(AgentAttention.Working, tracker.Current.Attention);

        tracker.ObserveTitle("resesh");
        Assert.Equal("codex", tracker.Current.Key);
        Assert.Equal(AgentAttention.Idle, tracker.Current.Attention);
    }

    [Fact]
    public void SpinnerStoppingEndsAnAdapterAgentsWorkThatNoHookEnded()
    {
        // Claude Code's Stop hook does not run when the user interrupts it.
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=working"));
        tracker.ObserveTitle("⠐ Fix login bug");
        tracker.ObserveTitle("✳ Fix login bug");
        Assert.Equal(AgentAttention.Idle, tracker.Current.Attention);
        Assert.Equal(AgentSource.Structured, tracker.Current.Source);
    }

    [Fact]
    public void TitlesNeverReplaceAReportedState()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=gemini;state=working"));
        tracker.ObserveTitle("Gemini CLI"); // never spun, so no spinner stopped
        Assert.Equal(AgentAttention.Working, tracker.Current.Attention);

        tracker.ObserveEvent(Structured("agent;state=needs-approval"));
        tracker.ObserveTitle("⠐ Gemini CLI");
        tracker.ObserveTitle("Gemini CLI");
        Assert.Equal(AgentAttention.NeedsApproval, tracker.Current.Attention);
    }

    [Fact]
    public void SpinnerWithoutAnAgentIsIgnored()
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("make");
        tracker.ObserveTitle("⠐ building");
        Assert.False(tracker.Current.IsAgent);
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
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

    // ---- the adapter latch is scoped to the agent that reported ----

    private static ProgramStatus Report(ProgramStatusState state, string app = "") =>
        ProgramStatus.Parse($"{(int)state};0;-1\u001F\u001F{app}\u001F\u001F")
        ?? throw new InvalidOperationException("unparsed");

    [Fact]
    public void BellsCountAgainForAnAgentStartedAfterTheAdapterOne()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=complete"));
        tracker.ObserveCommand("codex"); // no adapter installed for this one

        Assert.True(tracker.ObserveEvent(AgentOsc.Bell()));
        Assert.Equal(AgentAttention.Signal, tracker.Current.Attention);
    }

    [Fact]
    public void Osc9CountsAgainAfterAShellTitleRetiresTheAdapterAgent()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=working"));
        tracker.ObserveTitle("bash");
        tracker.ObserveProcesses(["bash", "codex"]);

        Assert.True(tracker.ObserveEvent(AgentOsc.ParseNotify("Task finished")));
        Assert.Equal(AgentAttention.Signal, tracker.Current.Attention);
        Assert.Equal("codex", tracker.Current.Key);
    }

    [Fact]
    public void BellsCountAgainAfterTheAdapterSaysTheAgentEnded()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=working"));
        tracker.ObserveEvent(Structured("agent;id=shell"));
        tracker.ObserveProcesses(["codex"]);

        Assert.True(tracker.ObserveEvent(AgentOsc.Bell()));
        Assert.Equal(AgentSource.Process, tracker.Current.Source);
    }

    [Fact]
    public void SourceIsDetectionOnceTheAdapterRecordIsGone()
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("claude");
        tracker.ObserveProgramStatus(Report(ProgramStatusState.Working, "claude"));
        Assert.Equal(AgentSource.Structured, tracker.Current.Source);

        tracker.ObserveProgramStatus(ProgramStatus.Cleared);
        Assert.Equal("claude", tracker.Current.Key);
        Assert.Equal(AgentSource.Command, tracker.Current.Source);
        // Its adapter has stopped reporting, so a bell is news again.
        Assert.True(tracker.ObserveEvent(AgentOsc.Bell()));
    }

    [Fact]
    public void SourceIsTheCommandForAnAgentStartedAfterAnAdapterOne()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=working"));
        tracker.ObserveCommand("codex");
        Assert.Equal(AgentSource.Command, tracker.Current.Source);
    }

    [Fact]
    public void BellsStayIgnoredWhileTheAdapterAgentRuns()
    {
        var tracker = new AgentTracker();
        tracker.ObserveCommand("claude");
        tracker.ObserveEvent(Structured("agent;id=claude;state=complete"));
        tracker.ObserveTitle("claude"); // a title naming the same agent is no reset
        tracker.ObserveUserInput();

        Assert.False(tracker.ObserveEvent(AgentOsc.Bell()));
    }

    [Fact]
    public void LiftingSuppressionDoesNotBringBackAStaleBadge()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=needs-approval;label=Delete%20file"));
        tracker.SetManualOverride("none");
        // While suppressed, the agent exits; nothing is observed.
        tracker.ObserveCommand("ls");
        tracker.SetManualOverride(null);

        Assert.False(tracker.Current.IsAgent);
        Assert.Equal(AgentAttention.None, tracker.Current.Attention);
        Assert.Null(tracker.Current.Label);
    }

    [Fact]
    public void ClearingASessionDefaultOfNoneForgetsWhatWasShowingBefore()
    {
        var tracker = new AgentTracker();
        tracker.ObserveEvent(Structured("agent;id=claude;state=needs-answer"));
        tracker.SessionDefault = AgentIdentities.None;
        tracker.SessionDefault = null;

        Assert.Equal(AgentSnapshot.Empty, tracker.Current);
    }
}
