using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

/// <summary>The annotation worker's one-runner, no-lost-request scheduling.</summary>
public class PassSchedulerTests
{
    [Fact]
    public void FirstRequestStartsARunnerAndLaterOnesDoNot()
    {
        var s = new PassScheduler();
        Assert.True(s.Request());
        Assert.False(s.Request());
        Assert.False(s.Request());
    }

    [Fact]
    public void RequestDuringAPassRunsOneMorePass()
    {
        var s = new PassScheduler();
        Assert.True(s.Request());
        Assert.False(s.Request()); // arrives mid-pass
        Assert.False(s.TryIdle()); // so the runner goes again
        Assert.True(s.TryIdle()); // and stops after that
        Assert.True(s.IsIdle);
    }

    [Fact]
    public void RequestAfterTheRunnerWentIdleStartsANewRunner()
    {
        // The lost wakeup: a request that found a runner busy, then lost the race with it going
        // idle, used to leave the state "again" with no runner, so no request ever started one.
        var s = new PassScheduler();
        Assert.True(s.Request());
        Assert.True(s.TryIdle());
        Assert.True(s.Request());
        Assert.True(s.TryIdle());
        Assert.True(s.Request());
    }

    [Fact]
    public void ConcurrentRequestsAreNeverLost()
    {
        var s = new PassScheduler();
        var requested = 0L;
        var served = 0L;
        var runners = 0;
        var maxRunners = 0;
        var pending = new CountdownEvent(1);

        void Run()
        {
            var now = Interlocked.Increment(ref runners);
            InterlockedMax(ref maxRunners, now);
            do
            {
                Interlocked.Exchange(ref served, Interlocked.Read(ref requested));
                Thread.SpinWait(50);
            } while (!s.TryIdle());
            Interlocked.Decrement(ref runners);
            pending.Signal();
        }

        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                Interlocked.Increment(ref requested);
                if (s.Request())
                {
                    pending.AddCount();
                    ThreadPool.UnsafeQueueUserWorkItem(_ => Run(), null);
                }
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        pending.Signal();
        Assert.True(pending.Wait(TimeSpan.FromSeconds(10)), "a runner never finished");
        Assert.True(s.IsIdle);
        // Every request was seen by a pass that started after it.
        Assert.Equal(Interlocked.Read(ref requested), Interlocked.Read(ref served));
        Assert.Equal(1, maxRunners);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
