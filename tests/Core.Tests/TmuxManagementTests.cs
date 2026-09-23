using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class TmuxManagementTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Management_OnlyIncludesCanonicalProfileSessionsAndActiveWindowPane()
    {
        var name = TmuxPersistence.SessionName(Id, 0);
        var output = $"{name}-2|11|0|1700000000|python3|/work/a|b\r\n"
            + $"{name}|11|2|1700000001|bash|/home/me\n"
            + $"{name}|01|2|1700000001|wrong-window|/wrong\n"
            + $"{name}|10|2|1700000001|wrong-pane|/wrong\n"
            + $"{name}-02|11|0|1700000000|bad-alias|/wrong\n"
            + "unrelated|11|0|1700000000|sh|/wrong\n";

        var sessions = TmuxPersistence.ParseManagedSessions(output, Id);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(0, sessions[0].Slot);
        Assert.Equal("bash", sessions[0].CurrentCommand);
        Assert.Equal(2, sessions[0].AttachedClients);
        Assert.Equal(1, sessions[1].Slot);
        Assert.Equal("/work/a|b", sessions[1].CurrentPath);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), sessions[1].CreatedAt);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("-1")]
    [InlineData("9223372036854775807")]
    public void Management_InvalidAgeDoesNotHideAnOtherwiseManageableSession(string age)
    {
        var name = TmuxPersistence.SessionName(Id, 0);
        var session = Assert.Single(TmuxPersistence.ParseManagedSessions($"{name}|11|0|{age}|bash|/tmp", Id));
        Assert.Null(session.CreatedAt);
    }

    [Fact]
    public void Management_RejectsMalformedRows()
    {
        var name = TmuxPersistence.SessionName(Id, 0);
        Assert.Empty(TmuxPersistence.ParseManagedSessions($"{name}|11|-1|0|sh|/tmp\n{name}|11|oops|0|sh|/tmp\npartial", Id));
    }

    [Fact]
    public void ExplicitResume_CannotCreateAReplacementSession()
    {
        var command = TmuxPersistence.ResumeCommand(Id, 1);
        Assert.Contains($"attach-session -t ={TmuxPersistence.SessionName(Id, 1)}", command);
        Assert.DoesNotContain("new-session", command);
        Assert.DoesNotContain("attach-session -d", command);
    }

    private static TmuxSessionInfo Shell(int slot, int attached = 0, int minutesAgo = 0) =>
        new(slot, TmuxPersistence.SessionName(Id, slot), "/home/me", attached,
            DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), "bash");

    private static readonly IReadOnlySet<int> NoTabs = new HashSet<int>();

    [Fact]
    public void Ask_StartsFreshWhenNothingIsRunning()
    {
        var plan = Assert.IsType<TmuxConnectPlan.StartNew>(
            TmuxPersistence.PlanConnect([], NoTabs, DetachedSessionAction.Ask));
        Assert.Equal(0, plan.Slot);
        Assert.Empty(plan.EndFirst);
    }

    [Fact]
    public void Ask_ResumesALoneDetachedShellWithoutAsking()
    {
        var plan = TmuxPersistence.PlanConnect([Shell(0), Shell(1)], new HashSet<int> { 0 }, DetachedSessionAction.Ask);
        Assert.Equal(new TmuxConnectPlan.Resume(1), plan);
    }

    [Fact]
    public void Ask_NeverSilentlyMirrorsAShellAttachedElsewhere()
    {
        var plan = Assert.IsType<TmuxConnectPlan.Choose>(
            TmuxPersistence.PlanConnect([Shell(0, attached: 1)], NoTabs, DetachedSessionAction.Ask));
        Assert.Equal(1, plan.NewSlot);
    }

    [Fact]
    public void Ask_OffersDetachedShellsFirstNewestFirst()
    {
        var plan = Assert.IsType<TmuxConnectPlan.Choose>(TmuxPersistence.PlanConnect(
            [Shell(0, attached: 1, minutesAgo: 1), Shell(1, minutesAgo: 90), Shell(2, minutesAgo: 5), Shell(3)],
            new HashSet<int> { 3 }, DetachedSessionAction.Ask));
        Assert.Equal([2, 1, 0], plan.Sessions.Select(session => session.Slot));
        Assert.Equal(4, plan.NewSlot);
    }

    [Fact]
    public void StartNew_KeepsEveryExistingShell()
    {
        var plan = Assert.IsType<TmuxConnectPlan.StartNew>(
            TmuxPersistence.PlanConnect([Shell(0), Shell(1)], NoTabs, DetachedSessionAction.StartNew));
        Assert.Equal(2, plan.Slot);
        Assert.Empty(plan.EndFirst);
    }

    [Fact]
    public void EndDetached_SparesTabOwnedAndAttachedShellsAndReusesFreedSlots()
    {
        var plan = Assert.IsType<TmuxConnectPlan.StartNew>(TmuxPersistence.PlanConnect(
            [Shell(0), Shell(1, attached: 1), Shell(2), Shell(3)],
            new HashSet<int> { 3 }, DetachedSessionAction.EndDetachedAndStartNew));
        Assert.Equal([0, 2], plan.EndFirst);
        Assert.Equal(0, plan.Slot);
    }

    [Fact]
    public void KillCommand_EndsEachSessionIndependently()
    {
        var command = TmuxPersistence.KillCommand(Id, [2, 0, 2]);
        Assert.Equal(
            $"{TmuxPersistence.KillCommand(Id, 0)}; {TmuxPersistence.KillCommand(Id, 2)}",
            command);
        Assert.DoesNotContain("\\;", command);
    }

    [Theory]
    [InlineData("no server running on /tmp/tmux-1000/resesh-app", true)]
    [InlineData("error connecting to /tmp/tmux-1000/resesh-app (No such file or directory)", true)]
    [InlineData("error connecting to /tmp/tmux-1000/resesh-app (Permission denied)", false)]
    [InlineData("tmux: command not found", false)]
    public void MissingServer_IsEmptyButOtherErrorsRemainVisible(string error, bool absent)
    {
        Assert.Equal(absent, TmuxPersistence.IsServerAbsent(new SshCommandResult(false, "", error)));
    }
}
