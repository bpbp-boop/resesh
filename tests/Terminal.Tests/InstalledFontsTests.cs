namespace Resesh.Terminal.Tests;

/// <summary>The font pickers' list of installed monospaced fonts.</summary>
public class InstalledFontsTests
{
    [Fact]
    public async Task ListsMonospacedFontsAndLeavesOutProportionalOnes()
    {
        var fonts = await InstalledFonts.MonospaceFamiliesAsync();

        // Both ship with every supported Windows version.
        Assert.Contains("Consolas", fonts);
        Assert.DoesNotContain("Segoe UI", fonts);
    }
}
