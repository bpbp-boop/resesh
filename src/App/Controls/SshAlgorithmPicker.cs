using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.App.Controls;

/// <summary>
/// Turns SSH algorithms on and off, one expander per kind with a check box per algorithm in
/// SSH.NET's order. In Settings it edits the app defaults; in a session it can also leave a
/// kind following those defaults (<see cref="FollowsApp"/>), shown read-only until changed.
/// </summary>
public sealed partial class SshAlgorithmPicker : UserControl
{
    private sealed class Group
    {
        public required SshAlgorithmKind Kind { get; init; }
        public required Expander Expander { get; init; }
        public CheckBox? Follow { get; init; }
        public required List<CheckBox> Boxes { get; init; }
        public required TextBlock Error { get; init; }
    }

    private readonly List<Group> _groups = [];
    private SshAlgorithms? _value;
    private bool _updating;

    /// <summary>True in a session: each kind can follow the app settings.</summary>
    public bool FollowsApp { get; }

    /// <summary>What the app settings turn off, shown for kinds that follow them.</summary>
    public SshAlgorithms? AppDefaults { get; private set; }

    public event Action? Changed;

    public SshAlgorithmPicker(bool followsApp)
    {
        FollowsApp = followsApp;
        var panel = new StackPanel { Spacing = 4 };
        foreach (var kind in SshAlgorithmPolicy.Kinds)
            panel.Children.Add(BuildGroup(kind));
        Content = panel;
        Refresh();
    }

    /// <summary>The choices shown; null when nothing is set.</summary>
    public SshAlgorithms? Value
    {
        get => _value;
        set
        {
            _value = value is { IsEmpty: false } ? value : null;
            Refresh();
        }
    }

    public void SetAppDefaults(SshAlgorithms? appDefaults)
    {
        AppDefaults = appDefaults;
        Refresh();
    }

    /// <summary>Why the choices cannot be used (a kind with everything off), or null.</summary>
    public string? Error => _groups
        .Where(group => Offered(group.Kind).Count == 0)
        .Select(group => $"Turn on at least one {SshAlgorithmPolicy.Title(group.Kind).ToLowerInvariant()} algorithm.")
        .FirstOrDefault();

    /// <summary>Opens the first kind with a problem and focuses it.</summary>
    public void ShowError()
    {
        var group = _groups.FirstOrDefault(g => Offered(g.Kind).Count == 0);
        if (group is null)
            return;
        group.Expander.IsExpanded = true;
        group.Boxes.FirstOrDefault()?.Focus(FocusState.Programmatic);
        group.Expander.StartBringIntoView();
    }

    private IReadOnlyList<string> Offered(SshAlgorithmKind kind) => SshAlgorithmPolicy.Offered(kind, _value, AppDefaults);

    private bool Follows(SshAlgorithmKind kind) => FollowsApp && SshAlgorithmPolicy.Disabled(_value, kind) is null;

    private UIElement BuildGroup(SshAlgorithmKind kind)
    {
        var body = new StackPanel { Spacing = 6 };
        CheckBox? follow = null;
        if (FollowsApp)
        {
            follow = new CheckBox { Content = "Use the app setting" };
            follow.Click += (_, _) =>
            {
                // Choosing for this session starts from what the app setting turns off.
                var disabled = follow.IsChecked == true
                    ? null
                    : SshAlgorithmPolicy.Disabled(AppDefaults, kind) ?? [];
                Update(kind, disabled);
            };
            body.Children.Add(follow);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var allOn = new HyperlinkButton { Content = "Turn all on", Padding = new Thickness(4, 2, 4, 2) };
        allOn.Click += (_, _) => Update(kind, []);
        var legacyOff = new HyperlinkButton { Content = "Turn legacy off", Padding = new Thickness(4, 2, 4, 2) };
        legacyOff.Click += (_, _) => Update(kind, SshAlgorithmPolicy.Supported(kind).Where(SshAlgorithmPolicy.IsLegacy));
        actions.Children.Add(allOn);
        actions.Children.Add(legacyOff);
        body.Children.Add(actions);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var names = SshAlgorithmPolicy.Supported(kind);
        var rows = (names.Count + 1) / 2;
        for (var row = 0; row < rows; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var boxes = new List<CheckBox>();
        for (var index = 0; index < names.Count; index++)
        {
            var name = names[index];
            var legacy = SshAlgorithmPolicy.IsLegacy(name);
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            label.Children.Add(new TextBlock { Text = name });
            if (legacy)
                label.Children.Add(new TextBlock
                {
                    Text = "legacy",
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
            var box = new CheckBox { Content = label, Tag = name, MinWidth = 0 };
            AutomationProperties.SetName(box, legacy ? $"{name}, legacy" : name);
            box.Click += (_, _) => Toggle(kind);
            // Fill down the first column, then the second, so the list still reads in order.
            Grid.SetColumn(box, index / rows);
            Grid.SetRow(box, index % rows);
            grid.Children.Add(box);
            boxes.Add(box);
        }
        body.Children.Add(grid);

        var error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        body.Children.Add(error);

        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = body,
        };
        _groups.Add(new Group { Kind = kind, Expander = expander, Follow = follow, Boxes = boxes, Error = error });
        return expander;
    }

    private void Toggle(SshAlgorithmKind kind)
    {
        if (_updating)
            return;
        var group = _groups.First(g => g.Kind == kind);
        Update(kind, group.Boxes.Where(box => box.IsChecked != true).Select(box => (string)box.Tag));
    }

    private void Update(SshAlgorithmKind kind, IEnumerable<string>? disabled)
    {
        _value = SshAlgorithmPolicy.With(_value, kind, disabled);
        Refresh();
        Changed?.Invoke();
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            foreach (var group in _groups)
            {
                var follows = Follows(group.Kind);
                var offered = Offered(group.Kind);
                var total = SshAlgorithmPolicy.Supported(group.Kind).Count;
                if (group.Follow is { } follow)
                    follow.IsChecked = follows;
                foreach (var box in group.Boxes)
                {
                    box.IsChecked = offered.Contains((string)box.Tag);
                    box.IsEnabled = !follows;
                }
                var off = total - offered.Count;
                var state = off == 0 ? "all on" : $"{off} of {total} off";
                var header = $"{SshAlgorithmPolicy.Title(group.Kind)} · {(follows ? $"app setting, {state}" : state)}";
                group.Expander.Header = header;
                AutomationProperties.SetName(group.Expander, header);
                group.Error.Text = offered.Count == 0 ? "Everything is off, so no connection could agree on one. Turn at least one on." : "";
                group.Error.Visibility = offered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally
        {
            _updating = false;
        }
    }
}
