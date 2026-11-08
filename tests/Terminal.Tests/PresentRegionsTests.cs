using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

public sealed class PresentRegionsTests
{
    [Fact]
    public void NewBuffersCopyEverythingUntilEachHasBeenPresented()
    {
        var regions = new PresentRegions(2);
        regions.Add(0, 10);
        Assert.Null(regions.Take(100));
        regions.Add(20, 30);
        Assert.Null(regions.Take(100));
        regions.Add(40, 50);
        // The buffer presented two frames ago lacks the previous frame's band and this one's.
        Assert.Equal([(20, 30), (40, 50)], regions.Take(100));
    }

    [Fact]
    public void BandsAreCopiedUntilEveryBufferHasThem()
    {
        var regions = Settled();
        regions.Add(10, 20);
        Assert.Equal([(10, 20)], regions.Take(100));
        Assert.Equal([(10, 20)], regions.Take(100)); // the other buffer
        Assert.Empty(regions.Take(100));
    }

    [Fact]
    public void OverlappingAndTouchingBandsMergeAndAreClipped()
    {
        var regions = Settled();
        regions.Add(-5, 10);
        regions.Add(30, 40);
        regions.Add(8, 20);
        regions.Add(20, 25);
        regions.Add(90, 130);
        Assert.Equal([(0, 25), (30, 40), (90, 100)], regions.Take(100));
    }

    [Fact]
    public void InvalidatingCopiesEverythingForEveryBuffer()
    {
        var regions = Settled();
        regions.Add(10, 20);
        regions.Take(100);
        regions.InvalidateAll();
        Assert.Null(regions.Take(100));
        Assert.Null(regions.Take(100));
        regions.Add(50, 60);
        Assert.Equal([(50, 60)], regions.Take(100));
    }

    private static PresentRegions Settled()
    {
        var regions = new PresentRegions(2);
        regions.Take(100);
        regions.Take(100);
        Assert.Empty(regions.Take(100));
        return regions;
    }
}
