using Resesh.Core.Backend;
using Resesh.Core.Layout;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class ReliabilityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("resesh-reliability-").FullName;
    private string FilePath(string name) => Path.Combine(_directory, name);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task ClosingDuringStartupDisposesLateBackendWithoutWaitingForStartup()
    {
        using var owner = new TerminalConnectionLifetime();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend();
        var startup = owner.StartAsync(backend, () =>
        {
            entered.SetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            owner.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, backend.DisposeCount);
        }
        finally { release.Set(); }
        await backend.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, backend.DisposeCount);
    }

    [Fact]
    public async Task FailedStartupDisposesBackendAndSuccessfulStartupHasOneOwner()
    {
        using var failedOwner = new TerminalConnectionLifetime();
        var failed = new FakeBackend();
        await Assert.ThrowsAsync<IOException>(() => failedOwner.StartAsync(failed, () => throw new IOException("startup")));
        Assert.Equal(1, failed.DisposeCount);
        using var owner = new TerminalConnectionLifetime();
        var live = new FakeBackend();
        await owner.StartAsync(live, () => { });
        Assert.Equal(0, live.DisposeCount);
        owner.Dispose();
        owner.Dispose();
        Assert.Equal(1, live.DisposeCount);
    }

    [Fact]
    public async Task CloseBeforeStartupPreventsLaunchingAndDisposesSuppliedBackend()
    {
        var owner = new TerminalConnectionLifetime();
        owner.Dispose();
        var backend = new FakeBackend();
        var launched = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.StartAsync(backend, () => launched = true));
        await backend.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(launched);
        Assert.Equal(1, backend.DisposeCount);
    }

    [Fact]
    public void CleanupContinuesAfterSaveAndBackendFailures()
    {
        var completed = new List<string>();
        var errors = new List<Exception>();
        CleanupActions.Run(errors.Add,
            () => throw new IOException("save"),
            () => { completed.Add("first tab"); throw new InvalidOperationException("stop"); },
            () => completed.Add("second tab"),
            () => completed.Add("recording"));
        Assert.Equal(["first tab", "second tab", "recording"], completed);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public async Task CancellingSshHandshakeReleasesConnectionToAnUnresponsiveServer()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        using var session = new SshTerminalSession(new KnownHostsStore(FilePath("hosts.json")));
        var connection = Task.Run(() => session.Connect(new Resesh.Core.Models.Session
        {
            Host = "127.0.0.1",
            Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port,
            Username = "test",
        }, "test", "xterm", 80, 24, cancellationToken: cancellation.Token));
        try
        {
            using var preflight = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var handshake = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(session.IsConnected);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Fact]
    public void FailedSettingsSavePreservesMemoryAndDiskAndCanBeRetried()
    {
        var path = FilePath("settings.json");
        var store = new SettingsStore(path);
        var original = new AppSettings { TreePaneWidth = 417.25 };
        store.Save(original);
        var disk = File.ReadAllText(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => store.Save(original with { TreePaneWidth = 900 }));
            Assert.Same(original, store.Current);
            Assert.Equal(disk, File.ReadAllText(path));
        }
        store.Save(original with { TreePaneWidth = 900 });
        var reloaded = new SettingsStore(path);
        reloaded.Load();
        Assert.Equal(900, reloaded.Current.TreePaneWidth);
    }

    [Fact]
    public void SettingsRecoverBackupAndDoNotReplaceItWithCorruptPrimary()
    {
        var path = FilePath("settings.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { TreePaneWidth = 417.25 });
        store.Save(new AppSettings { TreePaneWidth = 650 });
        File.WriteAllText(path, "broken");
        store.Load();
        Assert.Equal(417.25, store.Current.TreePaneWidth);
        Assert.NotNull(store.LoadWarning);
        store.Save(store.Current with { FontSize = 16 });
        Assert.Contains("417.25", File.ReadAllText(path + ".bak"));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("rename")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("reorder")]
    [InlineData("last")]
    public void FailedWorkspaceMutationPreservesAllState(string operation)
    {
        var path = FilePath("workspaces.json");
        var store = new WorkspaceStore(path);
        var layout = UnequalLayout();
        var first = store.SaveAs("first", layout);
        var second = store.SaveAs("second", layout);
        store.SaveLastLayout(layout);
        var previous = store.Workspaces.ToArray();
        var last = store.LastLayout;
        var disk = File.ReadAllText(path);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Action mutation = operation switch
        {
            "add" => () => store.SaveAs("third", layout),
            "rename" => () => store.Rename(first.Id, "renamed"),
            "update" => () => store.Update(first.Id, new WorkspaceLayout()),
            "delete" => () => store.Delete(first.Id),
            "reorder" => () => store.Reorder([second.Id, first.Id]),
            _ => () => store.SaveLastLayout(new WorkspaceLayout()),
        };
        Assert.ThrowsAny<IOException>(mutation);
        Assert.Equal(previous, store.Workspaces);
        Assert.Same(last, store.LastLayout);
        Assert.Equal(disk, File.ReadAllText(path));
    }

    [Fact]
    public void WorkspaceCoordinatorReportsFailedUpdateAndPreservesExactPaneSizes()
    {
        var path = FilePath("workspaces.json");
        var store = new WorkspaceStore(path);
        var layout = UnequalLayout();
        var notices = new List<string>();
        var refreshes = 0;
        var coordinator = new WorkspaceCoordinator(store, () => layout, () => refreshes++, (title, _) => notices.Add(title));
        Assert.True(coordinator.SaveAs("work"));
        var workspace = Assert.Single(store.Workspaces);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            layout = new WorkspaceLayout();
            Assert.False(coordinator.Update(workspace.Id));
        }
        Assert.Equal(2, refreshes);
        Assert.Single(notices);
        store.Load();
        var restored = Assert.Single(store.Workspaces).Layout!;
        Assert.Equal(new[] { 417.25, 903.75 }, restored.Sizes);
        Assert.Equal(new[] { 211.125, 618.875 }, restored.Children[1].Sizes);
    }

    [Fact]
    public void CorruptHostKeysBlockTrustChecksAndWritesUntilRepaired()
    {
        var path = FilePath("known_hosts.json");
        File.WriteAllText(path, "broken");
        var store = new KnownHostsStore(path);
        store.Load();
        Assert.NotNull(store.LoadError);
        Assert.Throws<IOException>(() => store.Check("host", 22, "ssh-ed25519", "new"));
        Assert.Throws<IOException>(() => store.Accept("host", 22, "ssh-ed25519", "new"));
        Assert.Throws<IOException>(() => store.Merge(new Dictionary<string, KnownHostEntry>()));
        Assert.Equal("broken", File.ReadAllText(path));
        File.WriteAllText(path, "{\"host:22\":{\"KeyType\":\"ssh-ed25519\",\"Sha256\":\"old\"}}");
        store.Load();
        Assert.Null(store.LoadError);
        Assert.Equal(HostKeyVerdict.Mismatch, store.Check("host", 22, "ssh-ed25519", "new"));
    }

    [Fact]
    public void HostKeysRecoverBackupAndFailedAcceptanceDoesNotChangeTrust()
    {
        var path = FilePath("known_hosts.json");
        var store = new KnownHostsStore(path);
        store.Accept("host", 22, "ssh-ed25519", "old");
        store.Accept("other", 22, "ssh-ed25519", "other");
        File.WriteAllText(path, "broken");
        store.Load();
        Assert.NotNull(store.LoadWarning);
        Assert.Null(store.LoadError);
        Assert.Equal(HostKeyVerdict.Mismatch, store.Check("host", 22, "ssh-ed25519", "new"));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => store.Accept("host", 22, "ssh-ed25519", "new"));
            Assert.Equal(HostKeyVerdict.Match, store.Check("host", 22, "ssh-ed25519", "old"));
        }
        store.Accept("host", 22, "ssh-ed25519", "new");
        Assert.Contains("old", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void LayoutRemappingPreservesMeasurementsAndOffsetsNestedGroups()
    {
        var layout = UnequalLayout().Layout!;
        var kept = WorkspaceLayoutTransform.Remap(layout, new Dictionary<int, int> { [0] = 0, [1] = 1, [2] = 2 })!;
        Assert.Equal(layout.Sizes, kept.Sizes);
        Assert.Equal(layout.Children[1].Sizes, kept.Children[1].Sizes);
        var offset = WorkspaceLayoutTransform.Offset(kept, 3);
        Assert.Equal(3, offset.Children[0].GroupIndex);
        Assert.Equal(5, offset.Children[1].Children[1].GroupIndex);
        Assert.Equal(layout.Children[1].Sizes, offset.Children[1].Sizes);
        var pruned = WorkspaceLayoutTransform.Remap(layout, new Dictionary<int, int> { [0] = 0, [2] = 1 })!;
        Assert.Equal(layout.Sizes, pruned.Sizes);
        Assert.Equal(1, pruned.Children[1].GroupIndex);
        Assert.Null(WorkspaceLayoutTransform.Remap(layout, new Dictionary<int, int>()));
    }

    [Fact]
    public void NewInstallationDoesNotReportMissingDirectoriesAsCorruption()
    {
        var settings = new SettingsStore(FilePath("new/settings.json"));
        settings.Load();
        Assert.Null(settings.LoadWarning);
        Assert.False(settings.Current.OnboardingCompleted);
        var hosts = new KnownHostsStore(FilePath("new/known_hosts.json"));
        hosts.Load();
        Assert.Null(hosts.LoadError);
        Assert.Equal(HostKeyVerdict.Unknown, hosts.Check("host", 22, "ssh-ed25519", "key"));
    }

    private static WorkspaceLayout UnequalLayout() => new()
    {
        Groups = [new WorkspaceGroup(), new WorkspaceGroup(), new WorkspaceGroup()],
        Layout = new WorkspaceLayoutNode
        {
            Orientation = SplitOrientation.Columns,
            Sizes = [417.25, 903.75],
            Children =
            [
                new() { GroupIndex = 0 },
                new()
                {
                    Orientation = SplitOrientation.Rows,
                    Sizes = [211.125, 618.875],
                    Children = [new() { GroupIndex = 1 }, new() { GroupIndex = 2 }],
                },
            ],
        },
    };

    private sealed class FakeBackend : ITerminalBackend
    {
        public event TerminalOutputHandler? OutputReceived { add { } remove { } }
        public int DisposeCount;
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Write(byte[] data) { }
        public void Resize(int columns, int rows) { }
        public void Stop() => Dispose();
        public void Dispose() { Interlocked.Increment(ref DisposeCount); Disposed.TrySetResult(); }
    }
}
