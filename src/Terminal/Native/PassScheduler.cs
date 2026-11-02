namespace Resesh.Terminal.Native;

/// <summary>
/// One runner at a time, and no request lost. A request either starts a runner (the caller
/// queues it) or tells the running one to go round again; the runner asks <see cref="TryIdle"/>
/// after each pass and stops only when nothing arrived meanwhile. Every transition is a single
/// compare-exchange from the state actually observed, so a request that loses a race with the
/// runner going idle sees the idle state on its retry and starts a new runner. (An unconditional
/// "busy, go again" write here once landed after the runner went idle, leaving "go again" set with
/// no runner, and every later request believed one was coming.)
/// </summary>
internal sealed class PassScheduler
{
    private const int Idle = 0, Running = 1, Again = 2;

    private int _state;

    /// <summary>Asks for a pass. True: no runner was active, and the caller must start one.</summary>
    public bool Request()
    {
        while (true)
        {
            switch (Volatile.Read(ref _state))
            {
                case Idle:
                    if (Interlocked.CompareExchange(ref _state, Running, Idle) == Idle)
                        return true;
                    break;
                case Running:
                    if (Interlocked.CompareExchange(ref _state, Again, Running) == Running)
                        return false;
                    break;
                default: // Again: the runner has yet to look; it will.
                    return false;
            }
        }
    }

    /// <summary>The runner finished a pass. True: it is now idle and must stop. False: a request
    /// arrived during the pass and it must run another.</summary>
    public bool TryIdle()
    {
        while (true)
        {
            if (Interlocked.CompareExchange(ref _state, Running, Again) == Again)
                return false;
            if (Interlocked.CompareExchange(ref _state, Idle, Running) == Running)
                return true;
        }
    }

    internal bool IsIdle => Volatile.Read(ref _state) == Idle;
}
