using System.Text.Json;

namespace Resesh.Core.Tests;

public sealed class NativeTerminalBaselineTests
{
    [Fact]
    public void ParityFixtureContainsEveryPhaseZeroCapability()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Terminal", "vt-parity.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var ids = cases.Select(item => item.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Contains("normal-and-alternate-buffers", ids);
        Assert.Contains("unicode-wide-and-combining", ids);
        Assert.Contains("colors-and-line-rendition", ids);
        Assert.Contains("osc-8-link", ids);
        Assert.Contains("metadata-osc-order-and-chunking", ids);
        Assert.Contains("search-highlights-marks-and-bookmarks", ids);
        Assert.Contains("clipboard-policy", ids);

        var metadata = cases.Single(item =>
            item.GetProperty("id").GetString() == "metadata-osc-order-and-chunking");
        var codes = metadata.GetProperty("expected").GetProperty("events").EnumerateArray()
            .Select(item => item.GetProperty("code").GetInt32())
            .ToArray();
        Assert.Equal([7, 133, 3008, 7377, 9, 777], codes);
        Assert.True(metadata.GetProperty("chunks").GetArrayLength() > 1);
    }
}
