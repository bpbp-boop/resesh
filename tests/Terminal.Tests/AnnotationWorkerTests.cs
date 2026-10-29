using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

/// <summary>Line times and the highlight lane, indexed off the UI thread.</summary>
public class AnnotationWorkerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Harness
    {
        public readonly FakeBuffer Buffer = new();
        public readonly Highlighter Highlighter = new();
        public readonly SemaphoreSlim Published = new(0);
        public int PublishedThread;
        public readonly AnnotationWorker Worker;

        public Harness(int lines, Func<int, string> text)
        {
            for (var i = 0; i < lines; i++)
                Buffer.Add(text(i));
            Highlighter.SetRules([new TerminalHighlight("err", "error", "#ff5555", ShowInOverview: true)]);
            Worker = new AnnotationWorker(Buffer, Highlighter, () =>
            {
                PublishedThread = Environment.CurrentManagedThreadId;
                Published.Release();
            }, sliceBudget: TimeSpan.FromMinutes(1));
        }

        /// <summary>Waits for publications until the ticks satisfy the condition.</summary>
        public IReadOnlyList<(int Line, uint Color)> WaitFor(Func<IReadOnlyList<(int Line, uint Color)>, bool> done)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (!done(Worker.Ticks))
            {
                var left = deadline - DateTime.UtcNow;
                Assert.True(left > TimeSpan.Zero && Published.Wait(left), "the worker did not publish in time");
            }
            return Worker.Ticks;
        }
    }

    [Fact]
    public void PublishesTicksFromAPoolThread()
    {
        var h = new Harness(30, i => i is 3 or 25 ? "an error here" : "fine");
        h.Worker.Request(0);
        var ticks = h.WaitFor(t => t.Count == 2);
        Assert.Equal([(3, 0xff5555u), (25, 0xff5555u)], ticks.OrderBy(t => t.Line));
        Assert.NotEqual(Environment.CurrentManagedThreadId, h.PublishedThread);
        h.Worker.Dispose(wait: true);
    }

    [Fact]
    public void ResetOverviewRebuildsTheLaneForNewRules()
    {
        var h = new Harness(30, i => i == 3 ? "an error" : i == 7 ? "a warning" : "fine");
        h.Worker.Request(0);
        h.WaitFor(t => t.Count == 1);
        h.Highlighter.SetRules([new TerminalHighlight("warn", "warning", "#f1fa8c", ShowInOverview: true)]);
        h.Worker.ResetOverview();
        var ticks = h.WaitFor(t => t.Count == 1 && t[0].Line == 7);
        Assert.Equal(0xf1fa8cu, ticks[0].Color);
        h.Worker.Dispose(wait: true);
    }

    [Fact]
    public void KeepsIndexingLargeScrollbackOnItsOwn()
    {
        // 2000 lines per pass: the last match is only reached by passes nobody asked for.
        var h = new Harness(5000, i => i % 1000 == 7 ? "error" : "fine");
        h.Worker.Request(0);
        var ticks = h.WaitFor(t => t.Count == 5);
        Assert.Equal([7, 1007, 2007, 3007, 4007], ticks.Select(t => t.Line).Order());
        h.Worker.Dispose(wait: true);
    }

    [Fact]
    public void DisposeReleasesMarkersAndStopsPasses()
    {
        var h = new Harness(30, i => i == 3 ? "error" : "fine");
        h.Worker.Request(1_700_000_000_000); // stamps line times, which hold markers
        h.WaitFor(t => t.Count == 1);
        Assert.NotNull(h.Worker.TimeOf(29));
        h.Worker.Dispose(wait: true);
        Assert.Equal(0, h.Buffer.LiveMarkerCount);
        h.Buffer.Add("another error");
        h.Worker.Request(0);
        Assert.False(h.Published.Wait(TimeSpan.FromMilliseconds(200)));
        Assert.Null(h.Worker.TimeOf(29));
    }
}
