using Sessions.Core.Agents;
using Sessions.Core.Local;
using Sessions.Core.Models;

namespace Sessions.Core.Tests;

/// <summary>
/// The local half of agent awareness end to end: a real ConPTY shell, a real child process
/// inside its job object, and the identity that falls out of the job's process list.
/// </summary>
public class AgentLocalDetectionTests
{
    [Fact]
    public async Task JobProcessNames_SeeAnAgentStartedInTheShell()
    {
        // A harmless long-running executable wearing an agent's name: what identifies an
        // agent locally is the process in the tab's job, not anything it prints.
        var directory = Path.Combine(Path.GetTempPath(), "sessions-agent-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var fakeAgent = Path.Combine(directory, "claude.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), fakeAgent);

        var session = new Session
        {
            Kind = SessionKind.Local,
            Name = "agent-detection",
            Local = new LocalTarget
            {
                Executable = @"%SystemRoot%\System32\cmd.exe",
                Arguments = ["/k"],
                StartingDirectory = directory,
            },
        };

        var local = new LocalTerminalSession();
        try
        {
            local.Start(session, 80, 24);
            Assert.Contains("cmd", local.GetJobProcessNames(), StringComparer.OrdinalIgnoreCase);
            Assert.Null(AgentDetection.FromProcessNames(local.GetJobProcessNames()));

            local.Write(System.Text.Encoding.UTF8.GetBytes("\"" + fakeAgent + "\" -t 127.0.0.1\r\n"));

            var deadline = DateTime.UtcNow.AddSeconds(30);
            IReadOnlyList<string> names = [];
            while (DateTime.UtcNow < deadline)
            {
                names = local.GetJobProcessNames();
                if (AgentDetection.FromProcessNames(names) is not null)
                    break;
                await Task.Delay(200);
            }

            Assert.Equal("claude", AgentDetection.FromProcessNames(names));
            Assert.Contains("cmd", names, StringComparer.OrdinalIgnoreCase);

            // And the tracker turns that into a live agent identity for the tab.
            var tracker = new AgentTracker();
            Assert.True(tracker.ObserveProcesses(names));
            Assert.Equal("claude", tracker.Current.Key);
        }
        finally
        {
            local.Stop(); // the job object takes the fake agent down with the shell
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The killed process may still hold the image file for a moment; the temp
                // directory is disposable either way.
            }
        }
    }

    [Fact]
    public void JobProcessNames_AreEmptyAfterTheShellIsStopped()
    {
        var session = new Session
        {
            Kind = SessionKind.Local,
            Name = "agent-detection-stopped",
            Local = new LocalTarget { Executable = @"%SystemRoot%\System32\cmd.exe", Arguments = ["/k"] },
        };

        var local = new LocalTerminalSession();
        local.Start(session, 80, 24);
        Assert.NotEmpty(local.GetJobProcessNames());
        local.Stop();
        Assert.Empty(local.GetJobProcessNames());
    }
}
