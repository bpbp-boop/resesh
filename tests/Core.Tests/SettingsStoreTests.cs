using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void WindowAndSessionsPaneState_RoundTripsThroughSettingsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                WindowPlacement = new WindowPlacement(120, 80, 1440, 900, IsMaximized: true),
                SessionsPaneOpen = false,
                SessionsRailTab = "recordings",
            });

            var loaded = new SettingsStore(path);
            loaded.Load();

            Assert.Equal(new WindowPlacement(120, 80, 1440, 900, IsMaximized: true), loaded.Current.WindowPlacement);
            Assert.False(loaded.Current.SessionsPaneOpen);
            Assert.Equal("recordings", loaded.Current.SessionsRailTab);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecordRecentSession_KeepsNewestUniqueSessionsWithinLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-settings-{Guid.NewGuid():N}.json");
        try
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var third = Guid.NewGuid();
            var store = new SettingsStore(path);

            store.RecordRecentSession(first, maximumCount: 2);
            store.RecordRecentSession(second, maximumCount: 2);
            store.RecordRecentSession(first, maximumCount: 2);
            store.RecordRecentSession(third, maximumCount: 2);

            var loaded = new SettingsStore(path);
            loaded.Load();
            Assert.Equal(new[] { third, first }, loaded.Current.RecentSessionIds);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
