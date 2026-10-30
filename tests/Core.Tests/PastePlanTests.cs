using System.Text;
using Resesh.Core.Input;

namespace Resesh.Core.Tests;

public sealed class PastePlanTests
{
    [Theory]
    [InlineData("show version", false)]
    [InlineData("show version\n", true)]
    [InlineData("a\r\nb", true)]
    [InlineData("a\rb", true)]
    public void PastesThatPressEnter_AskFirst(string text, bool expected) =>
        Assert.Equal(expected, PastePlan.NeedsConfirmation(text, bracketedPaste: false));

    [Fact]
    public void BracketedPaste_NeverAsks() =>
        Assert.False(PastePlan.NeedsConfirmation("rm -rf build\nmake\n", bracketedPaste: true));

    [Fact]
    public void LineBreaks_CountsEachStyleOnce() =>
        Assert.Equal(3, PastePlan.LineBreaks("a\r\nb\rc\nd"));

    [Fact]
    public void LineDelay_SendsWholeLinesEndingInCarriageReturn()
    {
        var chunks = PastePlan.Chunks("interface Gi0/1\r\n description uplink\r\nend", 100, 0);

        Assert.Equal(["interface Gi0/1\r", " description uplink\r", "end"], chunks.Select(c => Encoding.UTF8.GetString(c.Bytes)));
        Assert.Equal([100, 100, 0], chunks.Select(c => (int)c.DelayAfter.TotalMilliseconds));
        Assert.Equal([true, true, false], chunks.Select(c => c.EndsLine));
    }

    [Fact]
    public void CharacterDelay_SendsOneTextElementAtATime()
    {
        var chunks = PastePlan.Chunks("aé👍\n", 50, 10);

        Assert.Equal(["a", "é", "👍", "\r"], chunks.Select(c => Encoding.UTF8.GetString(c.Bytes)));
        Assert.Equal([10, 10, 10, 50], chunks.Select(c => (int)c.DelayAfter.TotalMilliseconds));
    }

    [Fact]
    public void Paced_OnlyWithADelay()
    {
        Assert.False(PastePlan.IsPaced(0, 0));
        Assert.True(PastePlan.IsPaced(1, 0));
        Assert.True(PastePlan.IsPaced(0, 1));
    }

    [Fact]
    public async Task SendAsync_WritesEveryChunkAndReportsLines()
    {
        var written = new List<string>();
        var lines = new List<int>();
        var chunks = PastePlan.Chunks("one\ntwo\nthree", 1, 0);

        await PastePlan.SendAsync(chunks, bytes => written.Add(Encoding.UTF8.GetString(bytes)), new SyncProgress(lines.Add));

        Assert.Equal(["one\r", "two\r", "three"], written);
        Assert.Equal([1, 2], lines);
    }

    [Fact]
    public async Task SendAsync_StopsWhenCancelled()
    {
        using var cancel = new CancellationTokenSource();
        var written = new List<byte[]>();
        var chunks = PastePlan.Chunks("one\ntwo\nthree\n", 10_000, 0);

        var send = PastePlan.SendAsync(chunks, written.Add, cancellationToken: cancel.Token);
        cancel.Cancel();
        await send;

        Assert.Single(written);
    }

    [Fact]
    public void Preview_ShowsFirstLinesAndCountsTheRest()
    {
        var text = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\n";
        Assert.Equal("line 1\nline 2\n… and 8 more", PastePlan.Preview(text, maxLines: 2));
    }

    // Progress<T> posts to a captured context; tests want the report inline.
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
