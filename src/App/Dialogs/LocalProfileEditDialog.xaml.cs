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
    /// <summary>Selected icon key: null = the default terminal glyph, SessionIcons.None = none.</summary>
    private string? SelectedIcon => IconPicker.Selected?.Key;

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

        AppearanceForm.Load(existing);
        PopulateIconPicker(existing?.Icon);

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
        }

        NameBox.TextChanged += (_, _) => ClearFieldError(NameBox, NameError);
        ExecutableBox.TextChanged += (_, _) => ClearFieldError(ExecutableBox, ExecutableError);
        EnvironmentBox.TextChanged += (_, _) => ClearFieldError(EnvironmentBox, EnvironmentError);

        if (initialTarget != SessionSettingsTarget.General)
            SectionBar.SelectedItem = AppearanceSection;
        DialogSizing.FitSections(this, ProfileForm, SectionHost, preferredWidth: 600, preferredHeight: 440);
        Opened += (_, _) => DispatcherQueue.TryEnqueue(() => InitialFocus(initialTarget)?.Focus(FocusState.Programmatic));
    }

    private Control? InitialFocus(SessionSettingsTarget target) => AppearanceForm.FocusTarget(target) ?? NameBox;

    private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // Fires during InitializeComponent for the initially-selected item, before the panels exist.
        if (ShellPanel is null || AppearancePanel is null || IconPanel is null || NotesBox is null)
            return;

        var selected = sender.SelectedItem;
        ShellPanel.Visibility = selected == ShellSection ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = selected == AppearanceSection ? Visibility.Visible : Visibility.Collapsed;
        IconPanel.Visibility = selected == IconSection ? Visibility.Visible : Visibility.Collapsed;
        NotesBox.Visibility = selected == NotesSection ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- icon picker ----

    private void PopulateIconPicker(string? currentKey)
    {
        // "Auto-detect" is SSH-only; a local shell without a choice shows the terminal glyph.
        var choices = Icons.SessionIconChoices.Build(
            currentKey, out var selected, unsetName: DefaultIconName, unsetText: "", unsetGlyph: "");
        IconPicker.SetChoices(choices, selected);
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

        // Edits the saved profile rather than building a new one, so whatever this dialog
        // doesn't show (paste delays, terminal type, anything a later version adds) survives.
        var basis = _existing ?? new Session { Id = Guid.NewGuid() };
        Result = AppearanceForm.ApplyTo(basis with
        {
            Kind = SessionKind.Local,
            ShellIntegration = ShellIntegrationSwitch.IsOn ? ShellIntegrationMode.Automatic : ShellIntegrationMode.Disabled,
            Name = NameBox.Text.Trim(),
            FolderPath = FolderPaths.Normalize(FolderBox.Text),
            Local = (basis.Local ?? new LocalTarget()) with
            {
                Executable = ExecutableBox.Text.Trim(),
                Arguments = ArgumentsBox.Text
                    .Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                StartingDirectory = StartDirBox.Text.Trim(),
                Environment = environment.Count > 0 ? environment : null,
            },
            Icon = SelectedIcon,
            Notes = NotesBox.Text,
        });
    }
}
