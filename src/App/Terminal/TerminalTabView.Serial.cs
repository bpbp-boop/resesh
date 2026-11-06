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
    private DispatcherQueueTimer? _serialPortWatch;

    private async Task ConnectSerialAsync(bool isReconnect)
    {
        var connection = _connection;
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
            serial.Closed += ex => DispatcherQueue.TryPost(() =>
            {
                if (!ConnectionEnded(connection)) return;
                if (SerialPortList.Exists(portName))
                    _terminal.NotifyDisconnected($"{portName} stopped working: {ex?.Message ?? "the driver closed it"}.");
                else
                {
                    _terminal.NotifyDisconnected(
                        $"{portName} was unplugged. This tab reconnects when it is plugged back in.");
                    WatchForSerialPort(portName, connection);
                }
            });

            await connection.StartAsync(serial, () => serial.Open(target));
            connection.Token.ThrowIfCancellationRequested();

            _backend = serial;
            // No summary: the tab's endpoint already reads "serial COM5 9600 8N1".
            _tab.MarkConnected();
            _terminal.NotifyConnected();
            FocusTerminalOnConnect();
        }
        catch (SerialSessionException ex) when (ex.PortMissing)
        {
            if (ConnectFailed(connection, $"{ex.Message} Plug in the console cable; this tab connects when {portName} appears."))
                WatchForSerialPort(portName, connection);
        }
        catch (SerialSessionException ex)
        {
            ConnectFailed(connection, ex.Message);
        }
        catch (Exception ex)
        {
            ConnectFailed(connection, FailureMessage(ex));
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
        timer.OnTick(async () =>
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
            }
            finally
            {
                checking = false;
            }
            if (!ReferenceEquals(_serialPortWatch, timer) || _disposed || connection.Token.IsCancellationRequested)
                return;
            StopSerialPortWatch();
            await ConnectAsync(isReconnect: true);
        });
        _serialPortWatch = timer;
        timer.Start();
    }

    private void StopSerialPortWatch()
    {
        _serialPortWatch?.Stop();
        _serialPortWatch = null;
    }
}
