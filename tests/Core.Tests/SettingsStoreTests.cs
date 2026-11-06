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
    public void Load_SettingsFromAnOlderVersionKeepDefaultsForMissingValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "fontSize": 16, "pinnedSessionIds": [], "recentSessionIds": [] }""");

            var store = new SettingsStore(path);
            store.Load();

            // Lists compare by reference in a record; everything else must equal the defaults.
            var expected = new AppSettings { FontSize = 16 };
            Assert.Null(store.LoadWarning);
            Assert.Empty(store.Current.PinnedSessionIds);
            Assert.Equal(expected, store.Current with
            {
                PinnedSessionIds = expected.PinnedSessionIds,
                RecentSessionIds = expected.RecentSessionIds,
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_WritesCamelCaseNamesAndNullValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-settings-{Guid.NewGuid():N}.json");
        try
        {
            new SettingsStore(path).Save(new AppSettings());

            var json = File.ReadAllText(path);
            Assert.Contains("\"showStatusBar\": true", json);
            Assert.Contains("\"windowPlacement\": null", json);
            Assert.Contains("\n  \"appearance\": \"dark\"", json);
            Assert.Contains("\n  \"lightTheme\": \"light\"", json);
            Assert.Contains("\n  \"darkTheme\": \"dark\"", json);
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
                    store.Save(original with { DarkTheme = "rejected", RecentSessionIds = new[] { rejectedSession } });
            });

            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected failure: {error}");
            Assert.Same(original, store.Current);
            Assert.Equal(new[] { originalSession }, store.Current.RecentSessionIds);
            Assert.Equal(originalContents, File.ReadAllText(path));

            Directory.Delete(path + ".tmp");
            store.RecordRecentSession(acceptedSession);

            var loaded = new SettingsStore(path);
            loaded.Load();
            Assert.Equal(original.DarkTheme, loaded.Current.DarkTheme);
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
            store.Save(new AppSettings { DarkTheme = "recovered" });
            store.Save(new AppSettings { DarkTheme = "previous-primary" });
            var backupContents = File.ReadAllText(path + ".bak");
            File.WriteAllText(path, "{invalid");
            store.Load();
            var recovered = store.Current;
            var warning = store.LoadWarning;
            Assert.Equal("recovered", recovered.DarkTheme);
            Assert.NotNull(warning);
            Directory.CreateDirectory(path + ".tmp");

            var error = Record.Exception(() => store.Save(recovered with { DarkTheme = "rejected" }));

            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected failure: {error}");
            Assert.Same(recovered, store.Current);
            Assert.Equal(warning, store.LoadWarning);
            Assert.Equal(backupContents, File.ReadAllText(path + ".bak"));

            Directory.Delete(path + ".tmp");
            store.Save(recovered with { FontSize = 18 });
            Assert.Equal(backupContents, File.ReadAllText(path + ".bak"));
            var loaded = new SettingsStore(path);
            loaded.Load();
            Assert.Equal("recovered", loaded.Current.DarkTheme);
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
            File.WriteAllText(path, "{\"fontSize\":14}");
            var store = new SettingsStore(path);

            store.Load();

            Assert.Null(store.Current.OnboardingCompleted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Changed_ReportsBeforeAndAfterOnlyOnceASaveHasLanded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var changes = new List<(AppSettings Before, AppSettings After)>();
            store.Changed += (before, after) =>
            {
                // A follower reads the store, so it must already hold the new settings.
                Assert.Same(after, store.Current);
                changes.Add((before, after));
            };
            var original = store.Current;

            store.Save(original with { ShowStatusBar = false });
            store.Save(store.Current); // nothing differs: nobody is told
            store.RecordRecentSession(Guid.NewGuid());

            Assert.Equal(2, changes.Count);
            Assert.Same(original, changes[0].Before);
            Assert.False(changes[0].After.ShowStatusBar);
            Assert.Single(changes[1].After.RecentSessionIds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Changed_StaysQuietWhenTheSaveFails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(store.Current);
            var raised = false;
            store.Changed += (_, _) => raised = true;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsAny<IOException>(() => store.Save(store.Current with { FontSize = 20 }));

            Assert.False(raised);
            Assert.Equal(14, store.Current.FontSize);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
