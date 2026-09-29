using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>
/// Lets the user resume one of the connection's running shells, start another, or clear
/// out the detached ones and start fresh.
/// </summary>
public sealed partial class TmuxSessionPickerDialog : ContentDialog
{
    private readonly int _detached;
    private bool _doubleTapped;
    private bool _endRequested;

    public IReadOnlyList<TmuxSessionItem> Items { get; }
    public string Summary { get; }
    public bool HasDetached => _detached > 0;
    public string EndDetachedText => _detached == 1
        ? "End the detached shell and start new"
        : $"End {_detached} detached shells and start new";

    public TmuxSessionPickerDialog(string profileName, IReadOnlyList<TmuxSessionInfo> sessions)
    {
        _detached = sessions.Count(session => session.AttachedClients == 0);
        Items = [.. sessions.Select(session => TmuxSessionItem.ForSession(session)), TmuxSessionItem.StartNew("Start a new session")];
        var summary = _detached == sessions.Count
            ? $"{sessions.Count} earlier shells are still running on this host."
            : _detached == 0
                ? $"{sessions.Count} shells are running and attached somewhere else."
                : $"{sessions.Count} shells are running: {_detached} detached, {sessions.Count - _detached} attached somewhere else.";
        Summary = summary + " Resume one, or start a new session.";
        InitializeComponent();
        Title = $"Resume a Session — {profileName}";
        Bindings.Update(); // realize ItemsSource now, so the first row can be preselected
        Picker.SelectedIndex = 0;
    }

    /// <summary>Shows the picker; the choice, or null when cancelled.</summary>
    public async Task<TmuxChoice?> ChooseAsync(Func<ContentDialog, Task<ContentDialogResult>> show)
    {
        DialogSizing.FitContent(this, Body, XamlRoot);
        var result = await show(this);
        if (_endRequested)
            return new TmuxChoice.EndDetachedAndStartNew();
        if (result != ContentDialogResult.Primary && !_doubleTapped)
            return null;
        return Picker.SelectedItem switch
        {
            TmuxSessionItem { Session: { } session } => new TmuxChoice.Resume(session.Slot),
            TmuxSessionItem { IsNew: true } => new TmuxChoice.StartNew(),
            _ => null,
        };
    }

    private void Picker_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PrimaryButtonText = Picker.SelectedItem is TmuxSessionItem { IsSession: true } ? "Resume" : "Start New";

    private void Picker_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _doubleTapped = true;
        Hide();
    }

    private void EndDetached_Click(object sender, RoutedEventArgs e)
    {
        _endRequested = true;
        Hide();
    }
}
