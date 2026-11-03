using Resesh.Core.Sftp;

namespace Resesh.Core.Tests;

/// <summary>The one working-directory policy every folder reader shares.</summary>
public sealed class WorkingDirectoryTrackerTests
{
    private const string Machine = "workstation";

    private static WorkingDirectoryTracker Tracker(params string[] hosts)
    {
        var tracker = new WorkingDirectoryTracker();
        tracker.SetKnownHosts(hosts.Length == 0 ? [Machine] : hosts);
        return tracker;
    }

    [Fact]
    public void NewestReportWinsWhateverItsKind()
    {
        var tracker = Tracker("server");
        tracker.ObserveReport(new("server", "/from/osc7"));
        tracker.ObserveReport(new("", "/from/osc3008")); // OSC 3008 may leave the host out
        Assert.Equal("/from/osc3008", tracker.ReportedPath);
        tracker.ObserveReport(new("server", "/again/osc7"));
        Assert.Equal("/again/osc7", tracker.Known());
    }

    [Fact]
    public void FirstPromptAfterAReportSpellsTheSamePlace()
    {
        var tracker = Tracker("server");
        tracker.ObserveReport(new("server", "/home/me/src"));
        Assert.False(tracker.ObservePrompt("~/src", null));
        Assert.False(tracker.ObservePrompt("~/src", null)); // the next prompt, same folder

        Assert.Equal("/home/me/src", tracker.ReportedPath);
        Assert.Equal("/home/me/src", tracker.Known());
    }

    [Fact]
    public void ShellThatMovesWithoutReportingMakesTheReportStale()
    {
        // PowerShell reports C:\repo, then cmd started from it changes drive.
        var tracker = Tracker();
        tracker.ObserveReport(new(Machine, @"C:\repo"));
        tracker.ObservePrompt(@"C:\repo", null);   // PowerShell's own prompt
        tracker.ObservePrompt(@"C:\repo", null);   // cmd's first prompt, same folder
        Assert.True(tracker.ObservePrompt(@"D:\work", null));

        Assert.Null(tracker.ReportedPath);
        Assert.Equal(@"D:\work", tracker.Known());

        // Back in PowerShell, its next report is current again.
        tracker.ObserveReport(new(Machine, @"C:\repo"));
        Assert.Equal(@"C:\repo", tracker.Known());
    }

    [Fact]
    public void LocationThatIsNotAFolderStillRetiresTheOldFolder()
    {
        var tracker = Tracker();
        tracker.ObserveReport(new(Machine, @"C:\repo"));
        tracker.ObservePrompt(@"C:\repo", null);
        tracker.ObservePrompt(@"HKLM:\Software", null); // Set-Location HKLM: sends no report

        Assert.Null(tracker.ReportedPath);
        Assert.Equal(@"HKLM:\Software", tracker.PromptPath); // readers check it is a folder
    }

    [Fact]
    public void AnotherHostsReportsHideTheScreenEntirely()
    {
        var tracker = Tracker("server");
        tracker.ObserveReport(new("server", "/srv"));
        tracker.ObservePrompt("/srv", null);
        tracker.ObserveReport(new("inner", "/home/other")); // ssh to another host
        tracker.ObservePrompt("/home/other", null);

        Assert.True(tracker.HostMismatch);
        Assert.Null(tracker.ReportedPath);
        Assert.Null(tracker.Known());
        Assert.True(tracker.HasEvidence);

        tracker.ObserveReport(new("server", "/srv")); // back on the connection's own host
        Assert.Equal("/srv", tracker.Known());
    }

    [Fact]
    public void NetworkCliPromptIsAModeNotAFolder()
    {
        var tracker = Tracker("router");
        tracker.ObservePrompt("configure · interface", "cisco");
        Assert.Null(tracker.PromptPath);
        Assert.Null(tracker.Known());
        Assert.False(tracker.HasEvidence);
    }

    [Fact]
    public void PromptOnlyShellsUseThePrompt()
    {
        var tracker = Tracker("server");
        tracker.ObservePrompt("/var/log", null);
        Assert.Equal("/var/log", tracker.Known());
        Assert.Null(tracker.Known(includePrompt: false));
    }

    [Fact]
    public void ResetForgetsTheLastConnection()
    {
        var tracker = Tracker("server");
        tracker.ObserveReport(new("server", "/srv"));
        tracker.ObservePrompt("/srv", null);
        tracker.Reset();

        Assert.Null(tracker.Known());
        Assert.False(tracker.HasEvidence);
        // A new report associates with its own first prompt, not the old one.
        tracker.ObserveReport(new("server", "/home/me"));
        tracker.ObservePrompt("~", null);
        Assert.Equal("/home/me", tracker.ReportedPath);
    }
}
