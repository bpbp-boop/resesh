using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void WindowPlacement_RoundTripsThroughSettingsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sessions-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                WindowPlacement = new WindowPlacement(120, 80, 1440, 900, IsMaximized: true),
            });

            var loaded = new SettingsStore(path);
            loaded.Load();

            Assert.Equal(new WindowPlacement(120, 80, 1440, 900, IsMaximized: true), loaded.Current.WindowPlacement);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
