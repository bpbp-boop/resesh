namespace Resesh.Core.Backend;

/// <summary>Owns one startup attempt and its backend, including completion after tab closure.</summary>
public sealed class TerminalConnectionLifetime : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private ITerminalBackend? _backend;
    private bool _disposed;
    private bool _started;

    public CancellationToken Token => _cancellation.Token;

    public async Task StartAsync(ITerminalBackend backend, Action start)
    {
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("A connection attempt can start only once.");
            _started = true;
        }
        // Do not cancel Task.Run itself: its body must dispose the supplied backend.
        await Task.Run(() =>
        {
            var adopted = false;
            try
            {
                Token.ThrowIfCancellationRequested();
                start();
                lock (_gate)
                {
                    Token.ThrowIfCancellationRequested();
                    _backend = backend;
                    adopted = true;
                }
            }
            finally
            {
                if (!adopted) backend.Dispose();
            }
        }).WaitAsync(Token);
        Token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        ITerminalBackend? backend;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation.Cancel();
            backend = _backend;
            _backend = null;
        }
        backend?.Dispose();
        // The token remains usable by a startup worker that is still unwinding.
    }
}
