using System.Runtime.CompilerServices;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

/// <summary>Timed socket waits must not leave failures for the finalizer to report.</summary>
public sealed class SocketWaitTests
{
    private sealed class MarkerException : Exception;

    [Fact]
    public void AbandonedOperationThatFailsLaterIsObserved()
    {
        var reported = false;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            if (e.Exception.InnerExceptions.Any(inner => inner is MarkerException))
                reported = true;
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            TimeOutThenFail();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(reported);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [Fact]
    public void ReturnsTheResultWhenTheOperationFinishesInTime() =>
        Assert.Equal(7, SshConnectionFactory.WaitObserved(Task.FromResult(7), TimeSpan.FromSeconds(5), default));

    // Kept out of line so no local still references the task when the test collects.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TimeOutThenFail()
    {
        // Continuations run inline, so nothing queued still holds the task when the test collects.
        var operation = new TaskCompletionSource<int>();
        Assert.Throws<TimeoutException>(() =>
            SshConnectionFactory.WaitObserved(operation.Task, TimeSpan.FromMilliseconds(20), default));
        operation.SetException(new MarkerException()); // what disposing the socket does to the read
    }
}
