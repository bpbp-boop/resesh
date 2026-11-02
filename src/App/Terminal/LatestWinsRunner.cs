namespace Resesh.App.Terminal;

/// <summary>
/// Runs one asynchronous operation at a time for a stream of requested values, and only for
/// the newest one: values requested while an operation runs replace each other, and the last
/// of them runs when it finishes. Scrubbing or playing a recording asks for a seek on every
/// timeline change; without this each seek would build its snapshot and queue behind the
/// others. Not thread-safe: call it from one thread (the UI thread).
/// </summary>
internal sealed class LatestWinsRunner<T>(Func<T, Task> run, Action<Exception> failed)
{
    private T _pending = default!;
    private bool _hasPending;
    private bool _stopped;

    /// <summary>Whether an operation is in flight.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Runs <paramref name="value"/> now if nothing is running, otherwise after the
    /// running operation, unless a newer value is requested first.</summary>
    public void Request(T value)
    {
        if (_stopped)
            return;
        _pending = value;
        _hasPending = true;
        if (!IsRunning)
            _ = PumpAsync();
    }

    /// <summary>Drops any waiting value and refuses new ones. A running operation finishes.</summary>
    public void Stop()
    {
        _stopped = true;
        _hasPending = false;
        _pending = default!;
    }

    private async Task PumpAsync()
    {
        IsRunning = true;
        try
        {
            while (_hasPending && !_stopped)
            {
                var value = _pending;
                _hasPending = false;
                _pending = default!;
                try
                {
                    await run(value);
                }
                catch (Exception exception)
                {
                    if (!_stopped)
                        failed(exception);
                }
            }
        }
        finally
        {
            IsRunning = false;
        }
    }
}
