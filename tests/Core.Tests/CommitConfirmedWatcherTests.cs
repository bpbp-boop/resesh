using System.Text;
using Resesh.Core.NetworkDevices;

namespace Resesh.Core.Tests;

public sealed class CommitConfirmedWatcherTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();

    private CommitConfirmedWatcher Watcher() => new(_clock);

    private static bool Feed(CommitConfirmedWatcher watcher, string text) =>
        watcher.Feed(Encoding.UTF8.GetBytes(text));

    private void AssertArmed(CommitConfirmedWatcher watcher, TimeSpan left)
    {
        Assert.Equal(CommitTimerStatus.Armed, watcher.Timer.Status);
        Assert.Equal(_clock.Now + left, watcher.Timer.Deadline);
    }

    [Fact]
    public void JunosArmsFromItsOwnMessageAndConfirmsOnCommit()
    {
        var watcher = Watcher();
        Feed(watcher, "[edit]\r\nadmin@mx1# ");
        Feed(watcher, "commit confirmed 5\r\n");
        Assert.Equal(CommitTimerStatus.None, watcher.Timer.Status);

        Assert.True(Feed(watcher, "commit confirmed will be automatically rolled back in 5 minutes unless confirmed\r\ncommit complete\r\n\r\n# commit confirmed will be rolled back in 5 minutes\r\n[edit]\r\nadmin@mx1# "));
        AssertArmed(watcher, TimeSpan.FromMinutes(5));

        Feed(watcher, "commit\r\ncommit complete\r\n\r\n[edit]\r\nadmin@mx1# ");
        Assert.Equal(CommitTimer.None, watcher.Timer);
    }

    [Fact]
    public void JunosCommitCheckConfirmsAndAReminderKeepsTheTimerSteady()
    {
        var watcher = Watcher();
        Feed(watcher, "admin@mx1# commit confirmed\r\ncommit confirmed will be automatically rolled back in 10 minutes unless confirmed\r\ncommit complete\r\n[edit]\r\nadmin@mx1# ");
        AssertArmed(watcher, TimeSpan.FromMinutes(10));
        var deadline = watcher.Timer.Deadline;

        _clock.Now += TimeSpan.FromSeconds(90);
        Feed(watcher, "show | compare\r\n\r\n# commit confirmed will be rolled back in 8 minutes\r\n[edit]\r\nadmin@mx1# ");
        Assert.Equal(deadline, watcher.Timer.Deadline);

        Feed(watcher, "commit check\r\nconfiguration check succeeds\r\n[edit]\r\nadmin@mx1# ");
        Assert.Equal(CommitTimerStatus.None, watcher.Timer.Status);
    }

    [Fact]
    public void JunosSaysWhenItRolledBack()
    {
        var watcher = Watcher();
        Feed(watcher, "admin@mx1# commit confirmed 1\r\ncommit confirmed will be automatically rolled back in 1 minutes unless confirmed\r\n[edit]\r\nadmin@mx1# ");
        Feed(watcher, "\r\nBroadcast Message from root@mx1\r\n        (no tty) at 9:01 UTC...\r\n\r\nCommit was not confirmed; automatic rollback complete.\r\n\r\n");
        Assert.Equal(CommitTimerStatus.RolledBack, watcher.Timer.Status);

        // A commit armed straight after the rollback counts down, though its reminder lands within
        // a minute of the rollback.
        _clock.Now += TimeSpan.FromSeconds(30);
        Feed(watcher, "\r\n# commit confirmed will be rolled back in 0 minutes\r\n[edit]\r\nadmin@mx1# ");
        AssertArmed(watcher, TimeSpan.Zero);
    }

    [Fact]
    public void AFailedCommitArmsNothing()
    {
        var watcher = Watcher();
        Feed(watcher, "admin@mx1# commit confirmed 5\r\nerror: configuration check-out failed\r\n[edit]\r\nadmin@mx1# ");
        Assert.Equal(CommitTimerStatus.None, watcher.Timer.Status);
    }

    [Fact]
    public void IosXrCountsInSecondsAndConfirmsOnCommit()
    {
        var watcher = Watcher();
        Feed(watcher, "RP/0/RP0/CPU0:pe1(config)#commit confirmed 300\r\nThu Nov 17 09:00:00.000 UTC\r\nRP/0/RP0/CPU0:pe1(config)#");
        AssertArmed(watcher, TimeSpan.FromSeconds(300));

        Feed(watcher, "commit\r\nThu Nov 17 09:01:00.000 UTC\r\nRP/0/RP0/CPU0:pe1(config)#");
        Assert.Equal(CommitTimerStatus.None, watcher.Timer.Status);
    }

    [Theory]
    [InlineData("commit confirmed minutes 3", 180)]
    [InlineData("commit confirmed", 600)]
    [InlineData("commit confirmed 45 label lab", 45)]
    public void IosXrDurations(string command, int seconds)
    {
        var watcher = Watcher();
        Feed(watcher, $"RP/0/RSP0/CPU0:pe2(config-if)#{command}\r\nRP/0/RSP0/CPU0:pe2(config-if)#");
        AssertArmed(watcher, TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void IosXrWithNothingToCommitArmsNothing()
    {
        var watcher = Watcher();
        Feed(watcher, "RP/0/RP0/CPU0:pe1(config)#commit confirmed 60\r\n% No configuration changes to commit.\r\nRP/0/RP0/CPU0:pe1(config)#");
        Assert.Equal(CommitTimerStatus.None, watcher.Timer.Status);
    }

    [Fact]
    public void MdCliConfirmsOnlyWithAccept()
    {
        var watcher = Watcher();
        Feed(watcher, "[ex:/configure]\r\nA:admin@sr1# commit confirmed 5\r\n\r\n[ex:/configure]\r\n!A:admin@sr1# ");
        AssertArmed(watcher, TimeSpan.FromMinutes(5));

        Feed(watcher, "commit\r\n\r\n[ex:/configure]\r\n!A:admin@sr1# ");
        AssertArmed(watcher, TimeSpan.FromMinutes(5));

        Feed(watcher, "commit confirmed accept\r\n\r\n[ex:/configure]\r\nA:admin@sr1# ");
        Assert.Equal(CommitTimerStatus.None, watcher.Timer.Status);
    }

    [Fact]
    public void MdCliCancelRollsBack()
    {
        var watcher = Watcher();
        Feed(watcher, "*(ex)A:admin@sr1# commit confirmed\r\n!(ex)A:admin@sr1# ");
        AssertArmed(watcher, TimeSpan.FromMinutes(10));

        Feed(watcher, "commit confirmed cancel\r\n(ex)A:admin@sr1# ");
        Assert.Equal(CommitTimerStatus.RolledBack, watcher.Timer.Status);
    }

    [Fact]
    public void EchoSplitAcrossReadsWithColourAndBackspaceStillCounts()
    {
        var watcher = Watcher();
        Feed(watcher, "\x1b[1madmin@mx1#\x1b[0m ");
        foreach (var ch in "commit confirmd")
            Feed(watcher, ch.ToString());
        Feed(watcher, "\bed 2\r");
        Feed(watcher, "\ncommit confirmed will be automatically rolled back in 2 minutes unless confirmed\r\n[edit]\r\nadmin@mx1# ");
        AssertArmed(watcher, TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void ShellPromptsAndOtherOutputLeaveItAlone()
    {
        var watcher = Watcher();
        Assert.False(Feed(watcher, "user@host:~$ git commit -m 'commit confirmed 5'\r\n[main 1a2b3c] commit confirmed 5\r\nuser@host:~$ "));
        Assert.Equal(CommitTimer.None, watcher.Timer);
    }

    [Fact]
    public void CountdownCountsDownThenLingersAndGoes()
    {
        var deadline = _clock.Now + TimeSpan.FromSeconds(125);
        var armed = new CommitTimer(CommitTimerStatus.Armed, deadline);

        Assert.Equal("2:05", CommitCountdown.For(armed, _clock.Now)!.Text);
        Assert.False(CommitCountdown.For(armed, _clock.Now)!.Urgent);
        Assert.Equal("0:01", CommitCountdown.For(armed, deadline - TimeSpan.FromMilliseconds(300))!.Text);
        Assert.True(CommitCountdown.For(armed, deadline - TimeSpan.FromSeconds(59))!.Urgent);
        Assert.Equal("0:00", CommitCountdown.For(armed, deadline + TimeSpan.FromMinutes(1))!.Text);
        Assert.Null(CommitCountdown.For(armed, deadline + CommitCountdown.Linger));
        Assert.Null(CommitCountdown.For(CommitTimer.None, _clock.Now));
    }

    [Fact]
    public void CountdownSaysWhenItRolledBack()
    {
        var rolledBack = new CommitTimer(CommitTimerStatus.RolledBack, _clock.Now);

        Assert.Equal("rolled back", CommitCountdown.For(rolledBack, _clock.Now + TimeSpan.FromMinutes(1))!.Text);
        Assert.Null(CommitCountdown.For(rolledBack, _clock.Now + CommitCountdown.Linger));
    }

    [Fact]
    public void JunosRollbackCompareThenCommitConfirms()
    {
        var watcher = Watcher();
        Feed(watcher, "admin@mx1# commit confirmed 5\r\ncommit confirmed will be automatically rolled back in 5 minutes unless confirmed\r\ncommit complete\r\n\r\n# commit confirmed will be rolled back in 5 minutes\r\n[edit]\r\nadmin@mx1# ");
        Feed(watcher, "rollback 1\r\nload complete\r\n\r\n# commit confirmed will be rolled back in 4 minutes\r\n[edit]\r\nadmin@mx1# ");
        Feed(watcher, "show | compare\r\n[edit interfaces ge-0/0/0 unit 0 family inet]\r\n-       address 192.0.2.1/24;\r\n\r\n# commit confirmed will be rolled back in 4 minutes\r\n[edit]\r\nadmin@mx1# ");
        Assert.Equal(CommitTimerStatus.Armed, watcher.Timer.Status);

        Assert.True(Feed(watcher, "commit\r\ncommit complete\r\n\r\n[edit]\r\nadmin@mx1# "));
        Assert.Equal(CommitTimer.None, watcher.Timer);
    }

    [Fact]
    public void JunosCommitAndQuitConfirmsAtTheOperationalPrompt()
    {
        var watcher = Watcher();
        Feed(watcher, "root@core01.adl1# commit confirmed 5\r\ncommit confirmed will be automatically rolled back in 5 minutes unless confirmed\r\ncommit complete\r\n\r\n# commit confirmed will be rolled back in 5 minutes\r\n[edit]\r\nroot@core01.adl1# ");
        AssertArmed(watcher, TimeSpan.FromMinutes(5));

        Assert.True(Feed(watcher, "commit and-quit\r\ncommit complete\r\nExiting configuration mode\r\n\r\nroot@core01.adl1> "));
        Assert.Equal(CommitTimer.None, watcher.Timer);
    }

    [Fact]
    public void CommitTypedAtTheOperationalPromptConfirmsNothing()
    {
        var watcher = Watcher();
        Feed(watcher, "root@mx1# commit confirmed 5\r\ncommit confirmed will be automatically rolled back in 5 minutes unless confirmed\r\nroot@mx1# exit\r\nExiting configuration mode\r\n\r\nroot@mx1> ");
        Feed(watcher, "commit\r\n       ^\r\nunknown command.\r\nroot@mx1> ");
        AssertArmed(watcher, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void IosXrConfirmedAfterOtherOptionsStillArms()
    {
        var watcher = Watcher();
        Feed(watcher, "RP/0/RP0/CPU0:pe1(config)#commit confirmed 300\r\nRP/0/RP0/CPU0:pe1(config)#");
        _clock.Now += TimeSpan.FromSeconds(30);

        // A new trial commit restarts the timer; it does not confirm the old one.
        Feed(watcher, "commit best-effort confirmed 60\r\nRP/0/RP0/CPU0:pe1(config)#");
        AssertArmed(watcher, TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData("commit confirmed persist-id 123", 10)]
    [InlineData("commit confirmed 5 persist-id 123", 5)]
    [InlineData("commit persist-id 7 confirmed", 10)]
    public void MdCliOptionValuesAreNotTheTimeout(string command, int minutes)
    {
        var watcher = Watcher();
        Feed(watcher, $"A:admin@sr1# {command}\r\n!A:admin@sr1# ");
        AssertArmed(watcher, TimeSpan.FromMinutes(minutes));
    }

    [Theory]
    [InlineData("commit comment \"confirmed 5\"")]
    [InlineData("commit label confirmed")]
    public void ConfirmedInsideAValueIsAPlainCommit(string command)
    {
        var watcher = Watcher();
        Feed(watcher, "RP/0/RP0/CPU0:pe1(config)#commit confirmed 300\r\nRP/0/RP0/CPU0:pe1(config)#");

        Feed(watcher, $"{command}\r\nRP/0/RP0/CPU0:pe1(config)#");
        Assert.Equal(CommitTimerStatus.None, watcher.Timer.Status); // a plain commit confirms
    }

    [Theory]
    [InlineData("RP/0/RP0/CPU0:pe1(config)#", "commit bogus", "                     ^\r\n% Invalid input detected at '^' marker.")]
    [InlineData("RP/0/RP0/CPU0:pe1(config)#", "commit comm", "% Incomplete command.")]
    [InlineData("admin@mx1#", "commit bogus", "                ^\r\nsyntax error, expecting <command>.")]
    public void ASyntaxErrorKeepsTheTimer(string prompt, string command, string error)
    {
        var watcher = Watcher();
        Feed(watcher, $"{prompt}commit confirmed 300\r\n{prompt}");
        var armed = watcher.Timer;
        Assert.Equal(CommitTimerStatus.Armed, armed.Status);

        Feed(watcher, $"{command}\r\n{error}\r\n{prompt}");
        Assert.Equal(armed, watcher.Timer);
    }

    [Theory]
    [InlineData("99999999999")]
    [InlineData("200000000")]
    public void AnImpossibleReminderIsIgnoredRatherThanThrown(string minutes)
    {
        var watcher = Watcher();

        Assert.False(Feed(watcher, $"# commit confirmed will be rolled back in {minutes} minutes\r\n"));
        Assert.Equal(CommitTimer.None, watcher.Timer);
    }

    [Fact]
    public void AnImpossibleTimeoutFallsBackToTheDefault()
    {
        var watcher = Watcher();
        Feed(watcher, "A:admin@sr1# commit confirmed 99999999999\r\n!A:admin@sr1# ");
        AssertArmed(watcher, TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void AReconnectDropsAnUnansweredCommitButKeepsTheTimer()
    {
        var watcher = Watcher();
        Feed(watcher, "RP/0/RP0/CPU0:pe1(config)#commit confirmed 300\r\nRP/0/RP0/CPU0:pe1(config)#");
        var armed = watcher.Timer;
        Feed(watcher, "commit\r\n"); // the connection drops before the device answers

        watcher.ResetConnection();
        Feed(watcher, "\r\nRP/0/RP0/CPU0:pe1#");
        Assert.Equal(armed, watcher.Timer);
    }

    [Fact]
    public void AZeroMinuteReminderIsTheLastMinute()
    {
        var watcher = Watcher();
        // The commit that armed it was not seen; the reminder alone says time is nearly up.
        Assert.True(Feed(watcher, "\r\n# commit confirmed will be rolled back in 0 minutes\r\n[edit]\r\nadmin@mx1# "));
        AssertArmed(watcher, TimeSpan.Zero);
        Assert.True(CommitCountdown.For(watcher.Timer, _clock.Now)!.Urgent);

        // A timer already known to end within the minute keeps its own deadline.
        var known = Watcher();
        Feed(known, "admin@mx1# commit confirmed 1\r\ncommit confirmed will be automatically rolled back in 1 minutes unless confirmed\r\n[edit]\r\nadmin@mx1# ");
        _clock.Now += TimeSpan.FromSeconds(20);
        var deadline = known.Timer.Deadline;
        Feed(known, "show | compare\r\n\r\n# commit confirmed will be rolled back in 0 minutes\r\n[edit]\r\nadmin@mx1# ");
        Assert.Equal(deadline, known.Timer.Deadline);
    }

    [Theory]
    [InlineData("commit confirmed 5 # confirmed accept", 5)]
    [InlineData("commit confirmed # 30", 10)]
    [InlineData("commit confirmed 5 comment \"# not a comment\"", 5)]
    public void MdCliIgnoresAnUnquotedComment(string command, int minutes)
    {
        var watcher = Watcher();
        Feed(watcher, $"A:admin@sr1# {command}\r\n!A:admin@sr1# ");
        AssertArmed(watcher, TimeSpan.FromMinutes(minutes));
    }

    [Theory]
    [InlineData("commit confirmed timeout 5", 5)]
    [InlineData("commit confirmed timeout 5 persist-id 123", 5)]
    public void MdCliTakesTheOptionalTimeoutKeyword(string command, int minutes)
    {
        var watcher = Watcher();
        Feed(watcher, $"A:admin@sr1# {command}\r\n!A:admin@sr1# ");
        AssertArmed(watcher, TimeSpan.FromMinutes(minutes));
    }
}
