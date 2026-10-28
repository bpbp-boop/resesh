using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.ViewModels;
using Resesh.Core.Local;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Dialogs;

/// <summary>
/// Editor for local shell profiles, laid out like <see cref="SessionEditDialog"/>: a Shell
/// section (name, folder, the LocalTarget, shell integration, "Make default"), an Appearance
/// section (icon, color, overrides) and Notes. Separate from the SSH editor because
/// the two share little beyond the Appearance section.
/// </summary>
public sealed partial class LocalProfileEditDialog : ContentDialog
{
    private const string DefaultIconName = "Default terminal glyph";

    // WinUI's multi-line TextBox ends lines with a bare CR; text pasted in may carry LF or CRLF.
    private static readonly char[] LineBreaks = ['\r', '\n'];

    private readonly Session? _existing;

    /// <summary>Selected icon key: null = the default local glyph, SessionIcons.None = explicitly none.</summary>
    private string? _selectedIcon;

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
        Title = existing is null ? "New local profile" : "Edit local profile";
        ShellIntegrationSwitch.IsOn = existing is { ShellIntegration: not ShellIntegrationMode.Disabled };

        FolderBox.ItemsSource = localFolderPaths.ToList();
        FolderBox.Text = FolderPaths.Normalize(existing?.FolderPath ?? defaultFolder);

        foreach (var (name, _) in SessionEditDialog.ColorChoices)
            ColorBox.Items.Add(name);
        ColorBox.SelectedIndex = Math.Max(0, Array.FindIndex(SessionEditDialog.ColorChoices, c => c.Hex == existing?.ColorTag));

        PopulateIconPicker(existing?.Icon);

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
            NotesBox.Text = existing.Notes;
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

        NameBox.TextChanged += (_, _) => ClearFieldError(NameBox, NameError);
        ExecutableBox.TextChanged += (_, _) => ClearFieldError(ExecutableBox, ExecutableError);
        EnvironmentBox.TextChanged += (_, _) => ClearFieldError(EnvironmentBox, EnvironmentError);

        if (initialTarget != SessionSettingsTarget.General)
            SectionBar.SelectedItem = AppearanceSection;
        DialogSizing.FitSections(this, ProfileForm, SectionHost, preferredWidth: 600, preferredHeight: 440);
        Opened += (_, _) => DispatcherQueue.TryEnqueue(() => InitialFocus(initialTarget)?.Focus(FocusState.Programmatic));
    }

    private Control? InitialFocus(SessionSettingsTarget target) => target switch
    {
        SessionSettingsTarget.Theme => OverrideThemeBox,
        SessionSettingsTarget.FontFamily => OverrideFontFamilyBox,
        SessionSettingsTarget.FontSize => OverrideFontSizeBox,
        SessionSettingsTarget.Scrollback => OverrideScrollbackBox,
        SessionSettingsTarget.AlwaysRecord => OverrideRecordingBox,
        SessionSettingsTarget.CommandHistory => OverrideHistoryBox,
        _ => NameBox,
    };

    private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // Fires during InitializeComponent for the initially-selected item, before the panels exist.
        if (ShellPanel is null || AppearancePanel is null || NotesBox is null)
            return;

        var selected = sender.SelectedItem;
        ShellPanel.Visibility = selected == ShellSection ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = selected == AppearanceSection ? Visibility.Visible : Visibility.Collapsed;
        NotesBox.Visibility = selected == NotesSection ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- icon picker ----

    private void PopulateIconPicker(string? currentKey)
    {
        var entries = App.Icons.PickerEntries();
        entries[0] = entries[0] with { Name = DefaultIconName }; // "Auto-detect" is SSH-only
        // A key whose file has gone missing (deleted custom icon) still round-trips.
        if (!string.IsNullOrEmpty(currentKey)
            && !entries.Any(e => string.Equals(e.Key, currentKey, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(new Icons.IconChoice(currentKey, $"{currentKey} (missing)", null, ""));
        }
        IconGrid.ItemsSource = entries;
        _selectedIcon = string.IsNullOrEmpty(currentKey) ? null : currentKey;
        UpdateIconButton(entries.First(e => string.Equals(e.Key, _selectedIcon, StringComparison.OrdinalIgnoreCase)));
    }

    private void IconGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        var choice = (Icons.IconChoice)e.ClickedItem;
        _selectedIcon = choice.Key;
        UpdateIconButton(choice);
        IconFlyout.Hide();
    }

    private void UpdateIconButton(Icons.IconChoice choice)
    {
        // The button shows the icon at 16, not the picker-tile 24 — fetch its own size.
        var image = App.Icons.GetImage(choice.Key, Icons.SessionIconCatalog.ListIconSize);
        IconButtonImage.Source = image;
        IconButtonImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        IconButtonText.Text = choice.Name;
    }

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

    private static void ClearFieldError(Control field, TextBlock error)
    {
        error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetItemStatus(field, "");
    }

    private static void SetFieldError(Control field, TextBlock error, string message)
    {
        error.Text = message;
        error.Visibility = Visibility.Visible;
        AutomationProperties.SetItemStatus(field, message);
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Control? firstInvalid = null;
        void Validate(Control field, TextBlock error, string? message)
        {
            ClearFieldError(field, error);
            if (message is null)
                return;
            SetFieldError(field, error, message);
            firstInvalid ??= field;
        }

        Validate(NameBox, NameError, string.IsNullOrWhiteSpace(NameBox.Text) ? "Enter a profile name." : null);
        Validate(ExecutableBox, ExecutableError,
            string.IsNullOrWhiteSpace(ExecutableBox.Text) ? "Enter the program to run." : null);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var badLines = new List<string>();
        foreach (var line in EnvironmentBox.Text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                badLines.Add($"\"{line}\"");
            else
                environment[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        Validate(EnvironmentBox, EnvironmentError,
            badLines.Count == 0 ? null : $"Not NAME=value: {string.Join(", ", badLines)}.");

        if (firstInvalid is not null)
        {
            // Every validated field lives on the Shell section.
            SectionBar.SelectedItem = ShellSection;
            firstInvalid.Focus(FocusState.Programmatic);
            firstInvalid.StartBringIntoView();
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
                    .Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                StartingDirectory = StartDirBox.Text.Trim(),
                Environment = environment.Count > 0 ? environment : null,
            },
            ColorTag = SessionEditDialog.ColorChoices[Math.Max(0, ColorBox.SelectedIndex)].Hex,
            Icon = _selectedIcon,
            Agent = _existing?.Agent,
            Notes = NotesBox.Text,
            Overrides = overrides.IsEmpty ? null : overrides,
        };
    }
}
