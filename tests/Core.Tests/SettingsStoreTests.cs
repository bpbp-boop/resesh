using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void WindowSessionsPaneAndStatusBarState_RoundTripsThroughSettingsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                WindowPlacement = new WindowPlacement(120, 80, 1440, 900, IsMaximized: true),
                SessionsPaneOpen = false,
                ShowStatusBar = false,
                SessionsRailTab = "recordings",
                SessionsPaneLayout = SessionsPaneLayouts.Dock,
                ReopenLastLayoutAtStartup = true,
            });

            var loaded = new SettingsStore(path);
            loaded.Load();

            Assert.Equal(new WindowPlacement(120, 80, 1440, 900, IsMaximized: true), loaded.Current.WindowPlacement);
            Assert.False(loaded.Current.SessionsPaneOpen);
            Assert.False(loaded.Current.ShowStatusBar);
            Assert.Equal("recordings", loaded.Current.SessionsRailTab);
            Assert.Equal(SessionsPaneLayouts.Dock, loaded.Current.SessionsPaneLayout);
            Assert.True(loaded.Current.ReopenLastLayoutAtStartup);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NewSettingsDefaultToShowingStatusBar()
    {
        Assert.True(new AppSettings().ShowStatusBar);
    }

    [Fact]
    public void NewSettingsLetTheSessionsPaneLayoutFollowTheWindowWidth()
    {
        Assert.Equal(SessionsPaneLayouts.Automatic, new AppSettings().SessionsPaneLayout);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedUpdate_LeavesCurrentUnchangedAndDoesNotLeakIntoNextSave(bool recordRecent)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sessions-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var originalSession = Guid.NewGuid();
            var rejectedSession = Guid.NewGuid();
            var acceptedSession = Guid.NewGuid();
            var store = new SettingsStore(path);
            store.Save(new AppSettings { RecentSessionIds = new[] { originalSession } });
            var original = store.Current;
            var originalContents = File.ReadAllText(path);
            Directory.CreateDirectory(path + ".tmp");

            var error = Record.Exception(() =>
            {
                if (recordRecent)
                    store.RecordRecentSession(rejectedSession);
                else
                    store.Save(original with { Theme = "rejected", RecentSessionIds = new[] { rejectedSession } });
            });

            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected failure: {error}");
            Assert.Same(original, store.Current);
            Assert.Equal(new[] { originalSession }, store.Current.RecentSessionIds);
            Assert.Equal(originalContents, File.ReadAllText(path));

            Directory.Delete(path + ".tmp");
            store.RecordRecentSession(acceptedSession);

            var loaded = new SettingsStore(path);
            loaded.Load();
            Assert.Equal(original.Theme, loaded.Current.Theme);
            Assert.Equal(new[] { acceptedSession, originalSession }, loaded.Current.RecentSessionIds);
            Assert.DoesNotContain(rejectedSession, loaded.Current.RecentSessionIds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedSave_AfterBackupRecoveryPreservesBackupUntilSuccessfulSave()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sessions-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { Theme = "recovered" });
            store.Save(new AppSettings { Theme = "previous-primary" });
            var backupContents = File.ReadAllText(path + ".bak");
            File.WriteAllText(path, "{invalid");
            store.Load();
            var recovered = store.Current;
            var warning = store.LoadWarning;
            Assert.Equal("recovered", recovered.Theme);
            Assert.NotNull(warning);
            Directory.CreateDirectory(path + ".tmp");

            var error = Record.Exception(() => store.Save(recovered with { Theme = "rejected" }));

            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected failure: {error}");
            Assert.Same(recovered, store.Current);
            Assert.Equal(warning, store.LoadWarning);
            Assert.Equal(backupContents, File.ReadAllText(path + ".bak"));

            Directory.Delete(path + ".tmp");
            store.Save(recovered with { FontSize = 18 });
            Assert.Equal(backupContents, File.ReadAllText(path + ".bak"));
            var loaded = new SettingsStore(path);
            loaded.Load();
            Assert.Equal("recovered", loaded.Current.Theme);
            Assert.Equal(18, loaded.Current.FontSize);

            var successfulContents = File.ReadAllText(path);
            store.Save(store.Current with { FontSize = 20 });
            Assert.Equal(successfulContents, File.ReadAllText(path + ".bak"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_NewSettingsFileMarksOnboardingIncomplete()
    {
        var path = Path.Combine(Path.GetTempPath(), $"resesh-new-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);

            store.Load();

            Assert.False(store.Current.OnboardingCompleted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacySettingsDoesNotForceOnboarding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"resesh-legacy-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"theme\":\"dark\"}");
            var store = new SettingsStore(path);

            store.Load();

            Assert.Null(store.Current.OnboardingCompleted);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
