using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.Terminal;
using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>
/// The tunnels on one connected tab: what is listening, what failed and why, with stop,
/// retry, and a form for opening another without reconnecting.
/// </summary>
public sealed partial class TunnelsDialog : ContentDialog
{
    private readonly TerminalTabView _view;
    private readonly Func<Session?> _savedSession;
    private readonly Action<Session> _saveSession;
    private bool _busy;

    /// <param name="savedSession">The tab's session as saved now, or null when it is not saved.</param>
    /// <param name="saveSession">Saves the session after its tunnel list changed.</param>
    private TunnelsDialog(TerminalTabView view, string sessionName, Func<Session?> savedSession, Action<Session> saveSession)
    {
        _view = view;
        _savedSession = savedSession;
        _saveSession = saveSession;
        InitializeComponent();
        DialogTheme.Apply(this);
        Title = $"Tunnels: {sessionName}";
        SaveBox.Visibility = savedSession() is null ? Visibility.Collapsed : Visibility.Visible;
        TunnelFields.TunnelAdded += tunnel => _ = AddAsync(tunnel);
    }

    public static async Task ManageAsync(XamlRoot xamlRoot, TerminalTabView view, string sessionName,
        Func<Session?> savedSession, Action<Session> saveSession)
    {
        var dialog = new TunnelsDialog(view, sessionName, savedSession, saveSession) { XamlRoot = xamlRoot };
        await dialog.ShowModalAsync();
    }

    private void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _view.TunnelsChanged += Render;
        Body.Width = Math.Min(560, Math.Max(240, XamlRoot.Size.Width - 96));
        Render();
    }

    private void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args) =>
        _view.TunnelsChanged -= Render;

    private void Render()
    {
        var statuses = _view.Tunnels;
        var saved = _savedSession()?.Tunnels?.Select(tunnel => tunnel.Id).ToHashSet() ?? [];
        TunnelList.Children.Clear();
        foreach (var status in statuses)
        {
            var tunnel = status.Tunnel;
            var detail = status.State switch
            {
                TunnelState.Failed => $"Failed: {status.Error}",
                TunnelState.Stopped => "Stopped",
                _ when status.Error is { } error => $"Listening. Last error: {error}",
                _ => "Listening",
            } + (saved.Contains(tunnel.Id) ? " · Saved with the session" : "");
            TunnelList.Children.Add(status.State == TunnelState.Listening
                ? TunnelForm.Row(tunnel.Describe(), "Stop", () => _ = RunAsync(() => _view.StopTunnelAsync(tunnel.Id)), detail)
                : TunnelForm.Row(tunnel.Describe(), "Start", () => _ = RunAsync(() => _view.StartTunnelAsync(tunnel)), detail));
        }
        EmptyText.Visibility = statuses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _view.CanTunnel ? "No tunnels are open." : "This tab is not connected.";
        TunnelFields.IsEnabled = _view.CanTunnel && !_busy;
        TunnelList.IsHitTestVisible = !_busy;
    }

    /// <summary>One tunnel operation at a time; the list refreshes from the tab's own event.</summary>
    private async Task RunAsync(Func<Task> work)
    {
        if (_busy)
            return;
        _busy = true;
        Render();
        try
        {
            await work();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TunnelFields.ShowError(exception.Message);
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    private Task AddAsync(Tunnel tunnel) => RunAsync(async () =>
    {
        var open = _view.Tunnels.Where(status => status.State == TunnelState.Listening).Select(status => status.Tunnel);
        if (TunnelForm.Conflict(tunnel, open) is { } conflict)
        {
            TunnelFields.ShowError(conflict);
            return;
        }
        var status = await _view.StartTunnelAsync(tunnel);
        if (status.State != TunnelState.Listening)
        {
            // Nothing is saved for a tunnel that never opened; the reason stays under the fields.
            await _view.RemoveTunnelAsync(tunnel.Id);
            TunnelFields.ShowError(status.Error ?? "The tunnel could not be opened.");
            return;
        }
        TunnelFields.Reset();
        if (SaveBox.IsChecked == true && _savedSession() is { } session)
            _saveSession(session with { Tunnels = [.. session.Tunnels ?? [], tunnel] });
    });
}
