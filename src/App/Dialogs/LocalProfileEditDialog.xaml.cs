using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.ViewModels;
using Resesh.Core.Local;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Dialogs;

/// <summary>
/// Editor for local shell profiles: identity (name, folder under Local, icon, color),
/// the LocalTarget (executable, arguments, starting directory, environment overrides),
/// terminal overrides, and "Make default". Separate from the SSH editor, whose form is
/// connection-shaped and shares almost nothing with this one.
/// </summary>
public sealed partial class LocalProfileEditDialog : ContentDialog
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

    /// <summary>The saved profile, or null if the dialog was cancelled.</summary>
    public Session? Result { get; private set; }

    /// <summary>Whether "Make default" was checked when saved.</summary>
    public bool MakeDefault => MakeDefaultBox.IsChecked == true;

    public LocalProfileEditDialog(IEnumerable<string> localFolderPaths, Session? existing, string defaultFolder,
        bool isCurrentDefault, SessionSettingsTarget initialTarget = SessionSettingsTarget.General)
    {
        InitializeComponent();
        DialogTheme.Apply(this);
        _existing = existing;
        Title = existing is null ? "New Local Profile" : "Edit Local Profile";
        ShellIntegrationSwitch.IsOn = existing is { ShellIntegration: not ShellIntegrationMode.Disabled };

        FolderBox.ItemsSource = localFolderPaths.ToList();
        FolderBox.Text = FolderPaths.Normalize(existing?.FolderPath ?? defaultFolder);

        foreach (var (name, _) in ColorChoices)
            ColorBox.Items.Add(name);
        ColorBox.SelectedIndex = Math.Max(0, Array.FindIndex(ColorChoices, c => c.Hex == existing?.ColorTag));

        var iconEntries = App.Icons.PickerEntries();
        iconEntries[0] = iconEntries[0] with { Name = "No icon (default glyph)" }; // "Auto-detect" is SSH-only
        IconBox.ItemsSource = iconEntries;
        var iconIndex = iconEntries.FindIndex(e => string.Equals(e.Key, existing?.Icon, StringComparison.OrdinalIgnoreCase));
        IconBox.SelectedIndex = Math.Max(0, iconIndex);

        OverrideThemeBox.ItemsSource = new[] { new ThemeChoice("", "Use app setting") }.Concat(ThemeCatalog.All).ToList();
        OverrideThemeBox.SelectedIndex = 0;

        MakeDefaultBox.IsChecked = isCurrentDefault;
        MakeDefaultBox.IsEnabled = !isCurrentDefault; // unset by picking another default, not here
        ResetButton.Visibility = existing is { BuiltIn: true } ? Visibility.Visible : Visibility.Collapsed;

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            var target = existing.Local ?? new LocalTarget();
            ExecutableBox.Text = target.Executable;
            ArgumentsBox.Text = string.Join(Environment.NewLine, target.Arguments);
            StartDirBox.Text = target.StartingDirectory;
            EnvironmentBox.Text = target.Environment is null
                ? ""
                : string.Join(Environment.NewLine, target.Environment.Select(kv => $"{kv.Key}={kv.Value}"));
            if (existing.Overrides is { } overrides)
            {
                OverrideThemeBox.SelectedItem = ThemeCatalog.All.FirstOrDefault(theme => theme.Id == overrides.Theme)
                    ?? OverrideThemeBox.Items[0];
                OverrideFontFamilyBox.Text = overrides.FontFamily ?? "";
                if (overrides.FontSize is { } fontSize)
                    OverrideFontSizeBox.Value = fontSize;
                if (overrides.Scrollback is { } scrollback)
                    OverrideScrollbackBox.Value = scrollback;
                OverrideRecordingBox.SelectedIndex = overrides.AlwaysRecord switch
                {
                    true => 1,
                    false => 2,
                    null => 0,
                };
                OverrideHistoryBox.SelectedIndex = overrides.KeepCommandHistory switch
                {
                    true => 1,
                    false => 2,
                    null => 0,
                };
            }
        }

        Opened += (_, _) => DispatcherQueue.TryEnqueue(() =>
            InitialFocus(initialTarget)?.Focus(FocusState.Programmatic));
    }

    private Control? InitialFocus(SessionSettingsTarget target) => target switch
    {
        SessionSettingsTarget.Theme => OverrideThemeBox,
        SessionSettingsTarget.FontFamily => OverrideFontFamilyBox,
        SessionSettingsTarget.FontSize => OverrideFontSizeBox,
        SessionSettingsTarget.Scrollback => OverrideScrollbackBox,
        SessionSettingsTarget.AlwaysRecord => OverrideRecordingBox,
        SessionSettingsTarget.CommandHistory => OverrideHistoryBox,
        _ => null,
    };

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_existing is null || LocalShellDiscovery.FindDefaults(_existing.Id) is not { } defaults)
            return;
        NameBox.Text = defaults.Name;
        ExecutableBox.Text = defaults.Target.Executable;
        ArgumentsBox.Text = string.Join(Environment.NewLine, defaults.Target.Arguments);
        StartDirBox.Text = "";
        EnvironmentBox.Text = "";
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            errors.Add("Name is required.");
        if (string.IsNullOrWhiteSpace(ExecutableBox.Text))
            errors.Add("Executable is required.");

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in EnvironmentBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                errors.Add($"Environment line \"{line}\" is not NAME=value.");
            else
                environment[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (errors.Count > 0)
        {
            ValidationText.Text = string.Join(" ", errors);
            ValidationText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        var overrides = new TerminalOverrides
        {
            Theme = (OverrideThemeBox.SelectedItem as ThemeChoice)?.Id is { Length: > 0 } theme ? theme : null,
            FontFamily = string.IsNullOrWhiteSpace(OverrideFontFamilyBox.Text) ? null : OverrideFontFamilyBox.Text.Trim(),
            FontSize = double.IsNaN(OverrideFontSizeBox.Value) ? null : (int)OverrideFontSizeBox.Value,
            Scrollback = double.IsNaN(OverrideScrollbackBox.Value) ? null : (int)OverrideScrollbackBox.Value,
            AlwaysRecord = OverrideRecordingBox.SelectedIndex switch
            {
                1 => true,
                2 => false,
                _ => null,
            },
            KeepCommandHistory = OverrideHistoryBox.SelectedIndex switch
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
            ShellIntegration = ShellIntegrationSwitch.IsOn ? ShellIntegrationMode.Automatic : ShellIntegrationMode.Disabled,
            BuiltIn = _existing?.BuiltIn ?? false,
            Name = NameBox.Text.Trim(),
            FolderPath = FolderPaths.Normalize(FolderBox.Text),
            Local = new LocalTarget
            {
                Executable = ExecutableBox.Text.Trim(),
                Arguments = ArgumentsBox.Text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                StartingDirectory = StartDirBox.Text.Trim(),
                Environment = environment.Count > 0 ? environment : null,
            },
            ColorTag = ColorChoices[Math.Max(0, ColorBox.SelectedIndex)].Hex,
            Icon = (IconBox.SelectedItem as Icons.IconChoice)?.Key,
            Notes = _existing?.Notes ?? "",
            Overrides = overrides.IsEmpty ? null : overrides,
        };
    }
}
