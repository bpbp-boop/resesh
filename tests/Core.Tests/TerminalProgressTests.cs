using Resesh.Core.Models;

namespace Resesh.Core.Tests;

public sealed class TerminalProgressTests
{
    private static readonly TerminalProgress Half = TerminalProgress.Percent(50);

    [Theory]
    [InlineData("4;1;42", TerminalProgressState.Normal, 42)]
    [InlineData("4;1", TerminalProgressState.Normal, 0)]
    [InlineData("4;1;250", TerminalProgressState.Normal, 100)]
    [InlineData("4;0", TerminalProgressState.None, 0)]
    [InlineData("4;0;80", TerminalProgressState.None, 0)]
    [InlineData("4", TerminalProgressState.None, 0)]
    [InlineData("4;", TerminalProgressState.None, 0)]
    [InlineData("4;3", TerminalProgressState.Indeterminate, 50)]
    [InlineData("4;2;10", TerminalProgressState.Error, 10)]
    [InlineData("4;4;70", TerminalProgressState.Paused, 70)]
    public void ParsesConEmuProgress(string payload, TerminalProgressState state, int value) =>
        Assert.Equal(new TerminalProgress(state, value), TerminalProgress.ParseOsc9(payload, Half));

    [Fact]
    public void ErrorAndPausedWithoutPercentKeepTheCurrentValue()
    {
        Assert.Equal(new TerminalProgress(TerminalProgressState.Error, 50), TerminalProgress.ParseOsc9("4;2", Half));
        Assert.Equal(new TerminalProgress(TerminalProgressState.Paused, 50), TerminalProgress.ParseOsc9("4;4;", Half));
        Assert.Equal(new TerminalProgress(TerminalProgressState.Error, 0), TerminalProgress.ParseOsc9("4;2", TerminalProgress.None));
        Assert.Equal(new TerminalProgress(TerminalProgressState.Error, 0), TerminalProgress.ParseOsc9("4;2", TerminalProgress.Indeterminate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("9;C:\\\\Users")]
    [InlineData("4;5;10")]
    [InlineData("4;x;10")]
    [InlineData("4;1;-5")]
    [InlineData("4;1; 5")]
    [InlineData("4;1;99999999999")]
    [InlineData("4;1;5;extra")]
    [InlineData("41;1;5")]
    public void IgnoresEverythingElse(string? payload) =>
        Assert.Null(TerminalProgress.ParseOsc9(payload, Half));

    [Theory]
    [InlineData(0, 0, TerminalProgressState.Indeterminate, 0)]
    [InlineData(0, 200, TerminalProgressState.Normal, 0)]
    [InlineData(50, 200, TerminalProgressState.Normal, 25)]
    [InlineData(300, 200, TerminalProgressState.Normal, 100)]
    [InlineData(4_000_000_000_000, 8_000_000_000_000, TerminalProgressState.Normal, 50)]
    public void TransfersReportPercentOfKnownTotals(long done, long total, TerminalProgressState state, int value) =>
        Assert.Equal(new TerminalProgress(state, value), TerminalProgress.FromTransfer(done, total));

    [Theory]
    [InlineData(1, 4, 0, 100, 0)]
    [InlineData(1, 4, 100, 100, 25)]
    [InlineData(3, 4, 50, 100, 62)]
    [InlineData(4, 4, 100, 100, 100)]
    [InlineData(9, 4, 100, 100, 100)]
    [InlineData(0, 0, 50, 100, 50)]
    public void MultiFileTransfersFillOnceAcrossTheBatch(int index, int count, long done, long total, int value) =>
        Assert.Equal(TerminalProgress.Percent(value), TerminalProgress.FromTransfer(done, total, index, count));

    [Fact]
    public void CombineShowsTroubleFirstThenTheLeastCompleteTask()
    {
        var error = new TerminalProgress(TerminalProgressState.Error, 90);
        var paused = new TerminalProgress(TerminalProgressState.Paused, 10);
        var busy = TerminalProgress.Indeterminate;
        var quarter = TerminalProgress.Percent(25);

        Assert.Equal(TerminalProgress.None, TerminalProgress.Combine());
        Assert.Equal(TerminalProgress.None, TerminalProgress.Combine(TerminalProgress.None, TerminalProgress.None));
        Assert.Equal(busy, TerminalProgress.Combine(TerminalProgress.None, busy));
        Assert.Equal(quarter, TerminalProgress.Combine(busy, Half, quarter));
        Assert.Equal(quarter, TerminalProgress.Combine(quarter, Half));
        Assert.Equal(paused, TerminalProgress.Combine(quarter, paused, busy));
        Assert.Equal(error, TerminalProgress.Combine(paused, error, quarter));
        Assert.Equal(TerminalProgress.Percent(0), TerminalProgress.Combine(Half, TerminalProgress.Percent(0)));
    }
}
