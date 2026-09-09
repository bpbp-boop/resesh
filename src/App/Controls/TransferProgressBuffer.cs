namespace Resesh.App.Controls;

internal readonly record struct TransferProgress(
    string Verb, string Name, int Index, int Count, long Done, long Total);

/// <summary>
/// Keeps only the latest transfer snapshot for the UI to sample. A slow UI never
/// accumulates a queue of per-chunk updates. Inactive operations discard reports.
/// </summary>
internal sealed class TransferProgressBuffer
{
    private readonly object _gate = new();
    private bool _active;
    private TransferProgress? _latest;

    public void Start()
    {
        lock (_gate)
        {
            _latest = null;
            _active = true;
        }
    }

    public void Report(TransferProgress progress)
    {
        lock (_gate)
        {
            if (_active)
                _latest = progress;
        }
    }

    public TransferProgress? TakeLatest()
    {
        lock (_gate)
        {
            var latest = _latest;
            _latest = null;
            return latest;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _active = false;
            _latest = null;
        }
    }
}
