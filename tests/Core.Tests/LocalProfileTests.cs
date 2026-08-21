using System.Text;
using Resesh.Core.Local;
using Resesh.Core.Models;
using Resesh.Core.Search;
using Resesh.Core.Storage;

// The suite only runs on Windows (ConPTY, registry); silences CA1416 for the Windows-only APIs.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace Resesh.Core.Tests;

public class LocalShellDiscoveryTests
{
    [Fact]
    public void StableId_IsDeterministic_AndCaseInsensitive()
    {
        Assert.Equal(LocalShellDiscovery.StableId("pwsh"), LocalShellDiscovery.StableId("pwsh"));
        Assert.Equal(LocalShellDiscovery.StableId("PWSH"), LocalShellDiscovery.StableId("pwsh"));
        Assert.NotEqual(LocalShellDiscovery.StableId("pwsh"), LocalShellDiscovery.StableId("cmd"));
    }

    [Fact]
    public void Discover_FindsCommandPrompt_WithStableId()
    {
        // cmd.exe ships with Windows; its profile must always be discovered.
        var shells = LocalShellDiscovery.Discover();
        var cmd = shells.FirstOrDefault(s => s.Id == LocalShellDiscovery.StableId("cmd"));
        Assert.NotNull(cmd);
        Assert.Equal("Command Prompt", cmd.Name);
        Assert.EndsWith("cmd.exe", cmd.Target.Executable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyncBuiltIns_RenamesLegacyPowerShellLabel_AndPreservesUserEdit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SessionStore(path);
            store.Load();
            var id = LocalShellDiscovery.StableId("powershell");
            store.Add(new Session
            {
                Id = id,
                Kind = SessionKind.Local,
                BuiltIn = true,
                Name = "Windows PowerShell",
                Local = new LocalTarget { Executable = "powershell.exe" },
            });

            LocalShellDiscovery.SyncBuiltIns(store);
            Assert.Equal("PowerShell 5.1", store.Find(id)!.Name);

            store.Update(store.Find(id)! with { Name = "My Legacy Shell" });
            LocalShellDiscovery.SyncBuiltIns(store);
            Assert.Equal("My Legacy Shell", store.Find(id)!.Name);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void SyncBuiltIns_AddsOnce_AndPreservesUserEdits()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SessionStore(path);
            store.Load();
            var available = LocalShellDiscovery.SyncBuiltIns(store);
            Assert.Contains(LocalShellDiscovery.StableId("cmd"), available);
            var count = store.Sessions.Count;

            // A user edit survives a re-sync (existing records are never overwritten).
            var cmd = store.Find(LocalShellDiscovery.StableId("cmd"))!;
            store.Update(cmd with { Name = "My Prompt" });
            LocalShellDiscovery.SyncBuiltIns(store);
            Assert.Equal(count, store.Sessions.Count);
            Assert.Equal("My Prompt", store.Find(cmd.Id)!.Name);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }
}

public class LocalTargetModelTests
{
    [Fact]
    public void PreV61Record_WithNoKind_LoadsAsSsh()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-test-{Guid.NewGuid():N}.json");
        try
        {
            // Shape written by v1..v6.0: no kind, no local target.
            File.WriteAllText(path, """
                {
                  "sessions": [
                    {
                      "id": "11111111-1111-1111-1111-111111111111",
                      "name": "web-01",
                      "folderPath": "Prod",
                      "host": "web01.example.com",
                      "port": 22,
                      "username": "ops",
                      "authMethod": "password"
                    }
                  ],
                  "folders": [ "Prod" ]
                }
                """, Encoding.UTF8);
            var store = new SessionStore(path);
            store.Load();

            var session = Assert.Single(store.Sessions);
            Assert.Equal(SessionKind.Ssh, session.Kind);
            Assert.False(session.IsLocal);
            Assert.Null(session.Local);
            Assert.Equal("web01.example.com", session.Host);
            Assert.Equal(["Prod"], store.FoldersOf(SessionKind.Ssh));
            Assert.Empty(store.FoldersOf(SessionKind.Local));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void LocalProfile_RoundTrips_ThroughTheStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SessionStore(path);
            store.Load();
            var profile = new Session
            {
                Kind = SessionKind.Local,
                Name = "Dev Shell",
                FolderPath = "Tools",
                Local = new LocalTarget
                {
                    Executable = @"C:\Program Files\PowerShell\7\pwsh.exe",
                    Arguments = ["-NoLogo", "-WorkingDirectory", @"C:\repos"],
                    StartingDirectory = @"C:\repos",
                    Environment = new Dictionary<string, string> { ["FOO"] = "bar" },
                },
            };
            store.Add(profile);

            var reloaded = new SessionStore(path);
            reloaded.Load();
            var read = reloaded.Find(profile.Id)!;
            Assert.Equal(SessionKind.Local, read.Kind);
            Assert.True(read.IsLocal);
            Assert.Equal(profile.Local.Executable, read.Local!.Executable);
            Assert.Equal(profile.Local.Arguments, read.Local.Arguments);
            Assert.Equal(@"C:\repos", read.Local.StartingDirectory);
            Assert.Equal("bar", read.Local.Environment!["FOO"]);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void LocalAndSshFolders_AreSeparateNamespaces()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SessionStore(path);
            store.Load();
            store.CreateFolder("Tools", SessionKind.Ssh);
            store.CreateFolder("Tools", SessionKind.Local);
            store.Add(new Session { Kind = SessionKind.Local, Name = "P", FolderPath = "Tools" });
            store.Add(new Session { Kind = SessionKind.Ssh, Name = "S", FolderPath = "Tools", Host = "h" });

            // Renaming the local folder moves only the local profile.
            store.RenameFolder("Tools", "Shells", SessionKind.Local);
            Assert.Equal("Shells", store.Sessions.Single(s => s.IsLocal).FolderPath);
            Assert.Equal("Tools", store.Sessions.Single(s => !s.IsLocal).FolderPath);
            Assert.Contains("Shells", store.FoldersOf(SessionKind.Local));
            Assert.Contains("Tools", store.FoldersOf(SessionKind.Ssh));

            // Deleting the SSH folder removes only the SSH session.
            var removed = store.DeleteFolder("Tools", SessionKind.Ssh);
            Assert.Single(removed);
            Assert.False(removed[0].IsLocal);
            Assert.Single(store.Sessions);
            Assert.True(store.Sessions[0].IsLocal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void Search_MatchesLocalExecutable()
    {
        var profile = new Session
        {
            Kind = SessionKind.Local,
            Name = "Shell",
            Local = new LocalTarget { Executable = @"C:\Program Files\PowerShell\7\pwsh.exe" },
        };
        Assert.True(SessionSearch.Matches(profile, "pwsh"));
        Assert.False(SessionSearch.Matches(profile, "cmd"));
    }

    [Fact]
    public void Capabilities_FollowTargetKind()
    {
        var ssh = SessionCapabilities.For(new Session { Host = "h" });
        Assert.True(ssh.RemoteFiles);
        Assert.True(ssh.HostKeys);
        Assert.True(ssh.RemoteSession);
        Assert.False(ssh.LocalWorkingFolder);
        Assert.Equal("Disconnect", ssh.StopVerb);
        Assert.Equal("Reconnect", ssh.StartAgainVerb);

        var local = SessionCapabilities.For(new Session { Kind = SessionKind.Local, Local = new LocalTarget() });
        Assert.False(local.RemoteFiles);
        Assert.False(local.HostKeys);
        Assert.False(local.RemoteSession);
        Assert.True(local.LocalWorkingFolder);
        Assert.Equal("Stop", local.StopVerb);
        Assert.Equal("Restart", local.StartAgainVerb);
    }
}

public class LocalTerminalSessionTests
{
    [Fact]
    public void Quote_FollowsWindowsArgumentRules()
    {
        Assert.Equal("plain", LocalTerminalSession.Quote("plain"));
        Assert.Equal("\"two words\"", LocalTerminalSession.Quote("two words"));
        Assert.Equal("\"\"", LocalTerminalSession.Quote(""));
        Assert.Equal("\"a\\\"b\"", LocalTerminalSession.Quote("a\"b"));
        // No quoting needed → backslashes pass through untouched.
        Assert.Equal(@"trailing\\", LocalTerminalSession.Quote(@"trailing\\"));
        // Quoted (has a space) → backslashes before the closing quote must double.
        Assert.Equal("\"a b\\\\\"", LocalTerminalSession.Quote("a b\\"));
        Assert.Equal(@"C:\no\spaces.exe", LocalTerminalSession.Quote(@"C:\no\spaces.exe"));
    }

    [Fact]
    public async Task Start_RunsProcess_CapturesOutput_AndReportsExit()
    {
        var session = new Session
        {
            Kind = SessionKind.Local,
            Name = "test",
            Local = new LocalTarget
            {
                Executable = @"%SystemRoot%\System32\cmd.exe",
                Arguments = ["/c", "echo conpty-roundtrip"],
            },
        };

        using var local = new LocalTerminalSession();
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        local.OutputReceived += data => { lock (output) output.Append(Encoding.UTF8.GetString(data)); };
        local.Exited += code => exited.TrySetResult(code);

        local.Start(session, 80, 24);
        Assert.True(local.ProcessId > 0);

        var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, code);
        // Give the pipe reader a beat to drain the tail of the output.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (output)
            {
                if (output.ToString().Contains("conpty-roundtrip"))
                    return;
            }
            await Task.Delay(100);
        }
        lock (output)
            Assert.Contains("conpty-roundtrip", output.ToString());
    }

    [Fact]
    public async Task Stop_KillsTheProcessTree_WithoutRaisingExited()
    {
        var session = new Session
        {
            Kind = SessionKind.Local,
            Name = "test",
            Local = new LocalTarget { Executable = @"%SystemRoot%\System32\cmd.exe" }, // interactive: stays alive
        };

        var local = new LocalTerminalSession();
        var exitedRaised = false;
        local.Exited += _ => exitedRaised = true;
        local.Start(session, 80, 24);
        var pid = local.ProcessId;
        Assert.True(local.IsRunning);

        local.Stop();

        // The job object kill is synchronous; the process must be gone (or a zombie with no live handle).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                if (p.HasExited)
                    break;
            }
            catch (ArgumentException)
            {
                break; // no such process — it was killed
            }
            await Task.Delay(100);
        }
        Assert.False(exitedRaised); // a user-initiated stop is reported by the caller, not the backend
    }

    [Fact]
    public void Start_MissingExecutable_ThrowsLocalSessionException()
    {
        var session = new Session
        {
            Kind = SessionKind.Local,
            Name = "test",
            Local = new LocalTarget { Executable = @"C:\definitely\not\a\real\shell-xyz.exe" },
        };
        using var local = new LocalTerminalSession();
        Assert.Throws<LocalSessionException>(() => local.Start(session, 80, 24));
    }

    [Fact]
    public void Start_MissingStartingDirectory_ThrowsWithClearMessage()
    {
        var session = new Session
        {
            Kind = SessionKind.Local,
            Name = "test",
            Local = new LocalTarget
            {
                Executable = @"%SystemRoot%\System32\cmd.exe",
                StartingDirectory = @"C:\definitely\not\a\real\dir-xyz",
            },
        };
        using var local = new LocalTerminalSession();
        var ex = Assert.Throws<LocalSessionException>(() => local.Start(session, 80, 24));
        Assert.Contains("Starting directory", ex.Message);
    }
}
