using Microsoft.UI.Dispatching;
using Resesh.App.ViewModels;
using Resesh.Core.Backend;
using Resesh.Core.Models;
using Resesh.Core.Serial;

namespace Resesh.App.Terminal;

/// <summary>Serial console tabs: open the port, report an unplugged adapter plainly, and
/// reconnect by themselves when that port comes back.</summary>
public sealed partial class TerminalTabView
{
    private static readonly TimeSpan SerialPortPollInterval = TimeSpan.FromSeconds(1.5);
    // PnP can remove COMx from SERIALCOMM slightly after ReadFile fails on unplug, and
    // create the registry entry slightly before the driver accepts CreateFile on replug.
    private static readonly TimeSpan[] SerialUnplugSettleDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(350)];
    private static readonly TimeSpan SerialReplugSettleDelay = TimeSpan.FromMilliseconds(250);
    private const int MaxSerialReplugRetries = 3;

    private DispatcherQueueTimer? _serialPortWatch;
    private string? _watchedSerialPort;
    private TerminalConnectionLifetime? _watchedConnection;
    private bool _serialConnectFromWatch;
    private int _serialWatchRetries;

    private async Task ConnectSerialAsync(bool isReconnect)
    {
        var connection = _connection;
        var fromWatch = _serialConnectFromWatch;
        _serialConnectFromWatch = false;
        if (!fromWatch)
            _serialWatchRetries = 0;
        StopSerialPortWatch();
        var target = Session.Serial ?? new SerialTarget();
        var portName = SerialTarget.NormalizePortName(target.PortName) ?? target.PortName;
        try
        {
            if (isReconnect)
                _terminal.WriteDivider();
            _terminal.WriteNotice($"Opening {target.Describe()} …");

            var serial = new SerialTerminalSession();
            FollowOutput(serial, connection);
            serial.Closed += ex => DispatcherQueue.TryPostAsync(() => HandleSerialClosedAsync(connection, portName, ex));

            await connection.StartAsync(serial, () => serial.Open(target));
            connection.Token.ThrowIfCancellationRequested();

            _serialWatchRetries = 0;
            _backend = serial;
            // No summary: the tab's endpoint already reads "serial COM5 9600 8N1".
            _tab.MarkConnected();
            _terminal.NotifyConnected();
            FocusTerminalOnConnect();
        }
        catch (SerialSessionException ex) when (ex.PortMissing)
        {
            _serialWatchRetries = 0;
            if (ConnectFailed(connection, $"{ex.Message} Plug in the console cable; this tab connects when {portName} appears."))
                WatchForSerialPort(portName, connection);
        }
        catch (SerialSessionException ex) when (fromWatch
            && ex.InnerException is IOException or UnauthorizedAccessException
            && _serialWatchRetries < MaxSerialReplugRetries)
        {
            _serialWatchRetries++;
            if (ConnectFailed(connection, $"{portName} is not ready yet. Retrying …"))
                WatchForSerialPort(portName, connection);
        }
        catch (SerialSessionException ex)
        {
            _serialWatchRetries = 0;
            ConnectFailed(connection, ex.Message);
        }
        catch (Exception ex)
        {
            _serialWatchRetries = 0;
            ConnectFailed(connection, FailureMessage(ex));
        }
    }

    private async Task HandleSerialClosedAsync(TerminalConnectionLifetime connection, string portName, Exception? ex)
    {
        if (!ConnectionEnded(connection))
            return;
        var present = await IsSerialPortPresentAfterFailureAsync(portName, connection.Token);
        if (_disposed || connection.Token.IsCancellationRequested || _tab.State != TabConnectionState.Disconnected)
            return;
        if (present)
            _terminal.NotifyDisconnected($"{portName} stopped working: {ex?.Message ?? "the driver closed it"}.");
        else
        {
            // Re-read the session after the async presence check: an edit during the settle
            // window may have changed the COM port or switched away from serial.
            var watchPort = Session.IsSerial ? SerialTarget.NormalizePortName(Session.Serial?.PortName) : null;
            if (watchPort is not null)
            {
                _terminal.NotifyDisconnected(string.Equals(watchPort, portName, StringComparison.OrdinalIgnoreCase)
                    ? $"{portName} was unplugged. This tab reconnects when it is plugged back in."
                    : $"{portName} was unplugged. This tab connects when {watchPort} appears.");
                WatchForSerialPort(watchPort, connection);
            }
            else
            {
                _terminal.NotifyDisconnected($"{portName} was unplugged.");
            }
        }
    }

    private static async Task<bool> IsSerialPortPresentAfterFailureAsync(string portName, CancellationToken token)
    {
        try
        {
            if (!await Task.Run(() => SerialPortList.Exists(portName), token))
                return false;
            foreach (var delay in SerialUnplugSettleDelays)
            {
                await Task.Delay(delay, token);
                if (!await Task.Run(() => SerialPortList.Exists(portName), token))
                    return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>Polls for <paramref name="portName"/> while this tab is disconnected from it.
    /// USB adapters come back under the same name, so the tab reconnects when it does. Ends
    /// when the user disconnects, reconnects or closes the tab (the attempt is cancelled).</summary>
    private void WatchForSerialPort(string portName, TerminalConnectionLifetime connection)
    {
        StopSerialPortWatch();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = SerialPortPollInterval;
        var checking = false;
        timer.OnTickAsync(async () =>
        {
            if (_disposed || connection.Token.IsCancellationRequested || _tab.State != TabConnectionState.Disconnected)
            {
                if (ReferenceEquals(_serialPortWatch, timer))
                    StopSerialPortWatch();
                return;
            }
            if (checking)
                return;
            checking = true;
            try
            {
                if (!await Task.Run(() => SerialPortList.Exists(portName)))
                    return;
                await Task.Delay(SerialReplugSettleDelay, connection.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                checking = false;
            }
            if (!ReferenceEquals(_serialPortWatch, timer) || _disposed || connection.Token.IsCancellationRequested)
                return;
            StopSerialPortWatch();
            _serialConnectFromWatch = true;
            await ConnectAsync(isReconnect: true);
        });
        _serialPortWatch = timer;
        _watchedSerialPort = portName;
        _watchedConnection = connection;
        timer.Start();
    }

    /// <summary>Keeps an active port watch aligned if the user edits the session's protocol or
    /// COM port while the tab is waiting for an adapter.</summary>
    private void OnSessionEditedForSerialWatch()
    {
        if (_serialPortWatch is null || _watchedConnection is not { } connection)
            return;
        if (!Session.IsSerial)
        {
            StopSerialPortWatch();
            return;
        }
        var newPort = SerialTarget.NormalizePortName(Session.Serial?.PortName);
        if (newPort is null)
            StopSerialPortWatch();
        else if (!string.Equals(newPort, _watchedSerialPort, StringComparison.OrdinalIgnoreCase))
            WatchForSerialPort(newPort, connection);
    }

    private void StopSerialPortWatch()
    {
        _serialPortWatch?.Stop();
        _serialPortWatch = null;
        _watchedSerialPort = null;
        _watchedConnection = null;
    }
}
