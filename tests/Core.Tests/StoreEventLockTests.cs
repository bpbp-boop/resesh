using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class StoreEventLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"resesh-store-lock-{Guid.NewGuid():N}");

    public StoreEventLockTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void SessionStore_RaisesChangedOutsideLock()
    {
        var store = new SessionStore(Path.Combine(_dir, "sessions.json"));
        store.Load();
        var observedCount = 0;
        store.Changed += () =>
        {
            // A subscriber blocking on another thread that reads the store deadlocks if _gate is still held.
            var readTask = Task.Run(() => store.Sessions.Count);
            Assert.True(readTask.Wait(TimeSpan.FromSeconds(5)), "SessionStore.Changed fired while holding its lock.");
            observedCount = readTask.Result;
        };

        store.Add(new Session { Name = "alpha", Host = "10.0.0.1", Username = "ops" });
        Assert.Equal(1, observedCount);
    }

    [Fact]
    public void WorkspaceStore_RaisesChangedOutsideLock()
    {
        var store = new WorkspaceStore(Path.Combine(_dir, "workspaces.json"));
        store.Load();
        var observedCount = 0;
        store.Changed += () =>
        {
            var readTask = Task.Run(() => store.Workspaces.Count);
            Assert.True(readTask.Wait(TimeSpan.FromSeconds(5)), "WorkspaceStore.Changed fired while holding its lock.");
            observedCount = readTask.Result;
        };

        store.SaveAs("Ops", new WorkspaceLayout());
        Assert.Equal(1, observedCount);
    }

    [Fact]
    public void HighlightsStore_RaisesChangedOutsideLock()
    {
        var store = new HighlightsStore(Path.Combine(_dir, "highlights.json"));
        store.Load();
        var observedCount = 0;
        store.Changed += () =>
        {
            var readTask = Task.Run(() => store.AllRules.Count);
            Assert.True(readTask.Wait(TimeSpan.FromSeconds(5)), "HighlightsStore.Changed fired while holding its lock.");
            observedCount = readTask.Result;
        };

        store.SaveCustom(new HighlightRule
        {
            Id = "custom-lock-test",
            Name = "Lock Test",
            Pattern = "ERROR",
            Color = "#FF0000",
        });
        Assert.True(observedCount > 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
