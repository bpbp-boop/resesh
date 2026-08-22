using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Local;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Dialogs;

/// <summary>
/// Editor for local shell profiles: identity (name, folder under Local, icon, color),
/// the LocalTarget (executable, arguments, starting directory, environment overrides),
/// terminal overrides, and "Make default". Built programmatically — the SSH editor's
/// XAML is connection-shaped and shares almost nothing with this form.
/// </summary>
public sealed class LocalProfileEditDialog : ContentDialog
{
    private static readonly (string Name, string? Hex)[] ColorChoices =
    [
        ("None", null),
        ("Red", "#E74856"),
        ("Orange", "#FF8C00"),
        ("Yellow", "#FFB900"),
        ("Green", "#10893E"),
        ("Blue", "#0078D7"),
        ("Purple", "#886CE4"),
    ];

    private readonly Session? _existing;
    private readonly TextBox _name = new() { Header = "Name" };
    private readonly ComboBox _folder = new() { Header = "Folder (under Local)", IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _executable = new() { Header = "Executable", PlaceholderText = @"e.g. %SystemRoot%\System32\cmd.exe" };
    private readonly TextBox _arguments = new()
    {
        Header = "Arguments (one per line — no quoting needed)",
        AcceptsReturn = true,
        Height = 72,
        TextWrapping = TextWrapping.NoWrap,
    };
    private readonly TextBox _startDir = new() { Header = "Starting directory", PlaceholderText = "Blank = your user profile folder" };
    private readonly TextBox _environment = new()
    {
        Header = "Environment overrides (NAME=value per line; empty value removes)",
        AcceptsReturn = true,
        Height = 72,
        TextWrapping = TextWrapping.NoWrap,
    };
    private readonly ComboBox _icon = new() { Header = "Icon", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _color = new() { Header = "Color tag", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _overrideTheme = new()
    {
        Header = "Theme override",
        ItemsSource = new[] { new ThemeChoice("", "Use app setting") }.Concat(ThemeCatalog.All).ToList(),
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBox _overrideFontFamily = new() { Header = "Font family override", PlaceholderText = "Blank = app setting" };
    private readonly NumberBox _overrideFontSize = new()
    {
        Header = "Font size override",
        Value = double.NaN,
        Minimum = 8,
        Maximum = 32,
        PlaceholderText = "App setting",
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
    };
    private readonly NumberBox _overrideScrollback = new()
    {
        Header = "Scrollback override",
        Value = double.NaN,
        Minimum = 1000,
        Maximum = 100000,
        SmallChange = 1000,
        PlaceholderText = "App setting",
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
    };
    private readonly ComboBox _overrideRecording = new()
    {
        Header = "Automatic recording",
        ItemsSource = new[] { "Use app setting", "Always record", "Never record" },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly CheckBox _makeDefault = new() { Content = "Make this the default local profile (+ Session, Ctrl+Shift+T)" };
    private readonly TextBlock _validation = new()
    {
        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE7, 0x48, 0x56)),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>The saved profile, or null if the dialog was cancelled.</summary>
    public Session? Result { get; private set; }

    /// <summary>Whether "Make default" was checked when saved.</summary>
    public bool MakeDefault => _makeDefault.IsChecked == true;

    public LocalProfileEditDialog(IEnumerable<string> localFolderPaths, Session? existing, string defaultFolder,
        bool isCurrentDefault, SessionSettingsTarget initialTarget = SessionSettingsTarget.General)
    {
        _existing = existing;
        Title = existing is null ? "New Local Profile" : "Edit Local Profile";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        PrimaryButtonClick += OnPrimaryButtonClick;

        _folder.ItemsSource = localFolderPaths.ToList();
        _folder.Text = FolderPaths.Normalize(existing?.FolderPath ?? defaultFolder);

        foreach (var (name, _) in ColorChoices)
            _color.Items.Add(name);
        _color.SelectedIndex = Math.Max(0, Array.FindIndex(ColorChoices, c => c.Hex == existing?.ColorTag));

        var iconEntries = App.Icons.PickerEntries();
        iconEntries[0] = iconEntries[0] with { Name = "No icon (default glyph)" }; // "Auto-detect" is SSH-only
        _icon.ItemsSource = iconEntries;
        _icon.DisplayMemberPath = "Name";
        var currentIcon = existing?.Icon;
        var iconIndex = iconEntries.FindIndex(e => string.Equals(e.Key, currentIcon, StringComparison.OrdinalIgnoreCase));
        _icon.SelectedIndex = Math.Max(0, iconIndex);

        _makeDefault.IsChecked = isCurrentDefault;
        _makeDefault.IsEnabled = !isCurrentDefault; // unset by picking another default, not here

        if (existing is not null)
        {
            _name.Text = existing.Name;
            var target = existing.Local ?? new LocalTarget();
            _executable.Text = target.Executable;
            _arguments.Text = string.Join(Environment.NewLine, target.Arguments);
            _startDir.Text = target.StartingDirectory;
            _environment.Text = target.Environment is null
                ? ""
                : string.Join(Environment.NewLine, target.Environment.Select(kv => $"{kv.Key}={kv.Value}"));
            if (existing.Overrides is { } overrides)
            {
                _overrideTheme.SelectedItem = ThemeCatalog.All.FirstOrDefault(theme => theme.Id == overrides.Theme)
                    ?? _overrideTheme.Items[0];
                _overrideFontFamily.Text = overrides.FontFamily ?? "";
                if (overrides.FontSize is { } fontSize)
                    _overrideFontSize.Value = fontSize;
                if (overrides.Scrollback is { } scrollback)
                    _overrideScrollback.Value = scrollback;
                _overrideRecording.SelectedIndex = overrides.AlwaysRecord switch
                {
                    true => 1,
                    false => 2,
                    null => 0,
                };
            }
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(_validation);
        panel.Children.Add(_name);
        panel.Children.Add(_folder);
        panel.Children.Add(_executable);
        panel.Children.Add(_arguments);
        panel.Children.Add(_startDir);
        panel.Children.Add(_environment);
        panel.Children.Add(_icon);
        panel.Children.Add(_color);
        panel.Children.Add(_overrideTheme);
        panel.Children.Add(_overrideFontFamily);
        panel.Children.Add(_overrideFontSize);
        panel.Children.Add(_overrideScrollback);
        panel.Children.Add(_overrideRecording);
        panel.Children.Add(_makeDefault);

        if (existing is { BuiltIn: true })
        {
            var reset = new Button { Content = "Reset to discovered defaults" };
            reset.Click += (_, _) =>
            {
                if (LocalShellDiscovery.FindDefaults(existing.Id) is { } defaults)
                {
                    _name.Text = defaults.Name;
                    _executable.Text = defaults.Target.Executable;
                    _arguments.Text = string.Join(Environment.NewLine, defaults.Target.Arguments);
                    _startDir.Text = "";
                    _environment.Text = "";
                }
            };
            panel.Children.Add(reset);
        }

        Content = new ScrollViewer { MaxHeight = 560, Content = panel };
        Opened += (_, _) => DispatcherQueue.TryEnqueue(() =>
            InitialFocus(initialTarget)?.Focus(FocusState.Programmatic));
    }

    private Control? InitialFocus(SessionSettingsTarget target) => target switch
    {
        SessionSettingsTarget.Theme => _overrideTheme,
        SessionSettingsTarget.FontFamily => _overrideFontFamily,
        SessionSettingsTarget.FontSize => _overrideFontSize,
        SessionSettingsTarget.Scrollback => _overrideScrollback,
        SessionSettingsTarget.AlwaysRecord => _overrideRecording,
        _ => null,
    };

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(_name.Text))
            errors.Add("Name is required.");
        if (string.IsNullOrWhiteSpace(_executable.Text))
            errors.Add("Executable is required.");

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in _environment.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                errors.Add($"Environment line \"{line}\" is not NAME=value.");
            else
                environment[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (errors.Count > 0)
        {
            _validation.Text = string.Join(" ", errors);
            _validation.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        var overrides = new TerminalOverrides
        {
            Theme = (_overrideTheme.SelectedItem as ThemeChoice)?.Id is { Length: > 0 } theme ? theme : null,
            FontFamily = string.IsNullOrWhiteSpace(_overrideFontFamily.Text) ? null : _overrideFontFamily.Text.Trim(),
            FontSize = double.IsNaN(_overrideFontSize.Value) ? null : (int)_overrideFontSize.Value,
            Scrollback = double.IsNaN(_overrideScrollback.Value) ? null : (int)_overrideScrollback.Value,
            AlwaysRecord = _overrideRecording.SelectedIndex switch
            {
                1 => true,
                2 => false,
                _ => null,
            },
            EnabledRules = _existing?.Overrides?.EnabledRules,
            DisabledRules = _existing?.Overrides?.DisabledRules,
        };
        Result = new Session
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            Kind = SessionKind.Local,
            BuiltIn = _existing?.BuiltIn ?? false,
            Name = _name.Text.Trim(),
            FolderPath = FolderPaths.Normalize(_folder.Text),
            Local = new LocalTarget
            {
                Executable = _executable.Text.Trim(),
                Arguments = _arguments.Text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                StartingDirectory = _startDir.Text.Trim(),
                Environment = environment.Count > 0 ? environment : null,
            },
            ColorTag = ColorChoices[Math.Max(0, _color.SelectedIndex)].Hex,
            Icon = (_icon.SelectedItem as Icons.IconChoice)?.Key,
            Notes = _existing?.Notes ?? "",
            Overrides = overrides.IsEmpty ? null : overrides,
        };
    }
}
