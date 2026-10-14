using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Models;

namespace Resesh.App.Dialogs;

/// <summary>
/// The fields that describe one tunnel, with an Add button. Shared by the session editor
/// (tunnels saved with the session) and the Tunnels dialog (tunnels on a live connection).
/// </summary>
public sealed partial class TunnelForm : UserControl
{
    public TunnelForm()
    {
        InitializeComponent();
        UpdateFields();
    }

    /// <summary>Raised with a valid tunnel when Add is pressed. The owner calls
    /// <see cref="Reset"/> to accept it, or <see cref="ShowError"/> to refuse it.</summary>
    public event Action<Tunnel>? TunnelAdded;

    private TunnelKind Kind => KindBox.SelectedIndex switch
    {
        1 => TunnelKind.Remote,
        2 => TunnelKind.Dynamic,
        _ => TunnelKind.Local,
    };

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFields();

    private void UpdateFields()
    {
        // SelectionChanged fires mid-InitializeComponent, before later controls exist.
        if (DestinationHostBox is null || DestinationPortBox is null || HintText is null || BindAddressBox is null)
            return;
        var kind = Kind;
        DestinationHostBox.Visibility = DestinationPortBox.Visibility =
            kind == TunnelKind.Dynamic ? Visibility.Collapsed : Visibility.Visible;
        BindAddressBox.Header = kind == TunnelKind.Remote ? "Listen on (on the host)" : "Listen on";
        DestinationHostBox.PlaceholderText = kind == TunnelKind.Remote
            ? "localhost, or a host this computer can reach"
            : "db.internal, or localhost for the SSH host itself";
        HintText.Text = kind switch
        {
            TunnelKind.Remote => "The SSH host listens, and its connections come out of this computer. "
                + "Most servers only allow listening on 127.0.0.1 unless GatewayPorts is on.",
            TunnelKind.Dynamic => "A SOCKS proxy on this computer. Point a browser at it to reach whatever the SSH host can reach.",
            _ => "This computer listens, and its connections come out of the SSH host. "
                + "Listening on 0.0.0.0 offers the port to everything on your network.",
        };
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var kind = Kind;
        var tunnel = new Tunnel
        {
            Kind = kind,
            BindAddress = BindAddressBox.Text.Trim(),
            BindPort = double.IsNaN(BindPortBox.Value) ? 0 : (int)BindPortBox.Value,
            DestinationHost = kind == TunnelKind.Dynamic ? "" : DestinationHostBox.Text.Trim(),
            DestinationPort = kind == TunnelKind.Dynamic || double.IsNaN(DestinationPortBox.Value)
                ? 0
                : (int)DestinationPortBox.Value,
        };
        if (tunnel.Validate() is { } invalid)
        {
            ShowError(invalid);
            return;
        }
        ShowError(null);
        TunnelAdded?.Invoke(tunnel);
    }

    /// <summary>Shows why a tunnel was refused under the fields, or clears it with null.</summary>
    public void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetItemStatus(BindPortBox, message ?? "");
    }

    /// <summary>Clears the ports and destination for the next tunnel; the kind and listen address stay.</summary>
    public void Reset()
    {
        ShowError(null);
        BindPortBox.Value = double.NaN;
        DestinationHostBox.Text = "";
        DestinationPortBox.Value = double.NaN;
    }

    /// <summary>Why <paramref name="tunnel"/> cannot join <paramref name="existing"/>: two
    /// listeners cannot share a port on the same side of the connection.</summary>
    public static string? Conflict(Tunnel tunnel, IEnumerable<Tunnel> existing) =>
        existing.Any(other => other.Id != tunnel.Id && other.BindPort == tunnel.BindPort
            && (other.Kind == TunnelKind.Remote) == (tunnel.Kind == TunnelKind.Remote))
            ? $"Another tunnel already listens on port {tunnel.BindPort}."
            : null;

    /// <summary>One tunnel as a line of text with a single action beside it.</summary>
    public static Grid Row(string text, string action, Action invoke, string? detail = null)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
        {
            label.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        row.Children.Add(label);
        var button = new Button { Content = action, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, $"{action} {text}");
        button.Click += (_, _) => invoke();
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }
}
