using Resesh.App.Terminal;

namespace Resesh.AppLogic.Tests;

public sealed class LatestWinsRunnerTests
{
    private sealed class Recorder
    {
        public List<double> Started { get; } = [];
        public List<TaskCompletionSource> Pending { get; } = [];
        public List<Exception> Failures { get; } = [];

        public LatestWinsRunner<double> Create() => new(value =>
        {
            Started.Add(value);
            var completion = new TaskCompletionSource();
            Pending.Add(completion);
            return completion.Task;
        }, Failures.Add);
    }

    [Fact]
    public void Runs_one_at_a_time_and_only_the_newest_waiting_value()
    {
        var recorder = new Recorder();
        var runner = recorder.Create();

        runner.Request(1);
        runner.Request(2);
        runner.Request(3);
        Assert.Equal([1d], recorder.Started);
        Assert.True(runner.IsRunning);

        recorder.Pending[0].SetResult();
        Assert.Equal([1d, 3d], recorder.Started);

        recorder.Pending[1].SetResult();
        Assert.Equal([1d, 3d], recorder.Started);
        Assert.False(runner.IsRunning);

        runner.Request(4);
        Assert.Equal([1d, 3d, 4d], recorder.Started);
    }

    [Fact]
    public void A_failure_is_reported_and_the_next_value_still_runs()
    {
        var recorder = new Recorder();
        var runner = recorder.Create();

        runner.Request(1);
        runner.Request(2);
        recorder.Pending[0].SetException(new InvalidOperationException("seek failed"));

        Assert.Single(recorder.Failures);
        Assert.Equal([1d, 2d], recorder.Started);
    }

    [Fact]
    public void Stop_drops_the_waiting_value_and_refuses_new_ones()
    {
        var recorder = new Recorder();
        var runner = recorder.Create();

        runner.Request(1);
        runner.Request(2);
        runner.Stop();
        recorder.Pending[0].SetException(new ObjectDisposedException("terminal"));
        runner.Request(3);

        Assert.Equal([1d], recorder.Started);
        Assert.Empty(recorder.Failures);
        Assert.False(runner.IsRunning);
    }
}
