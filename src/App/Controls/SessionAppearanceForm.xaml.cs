using Microsoft.UI.Xaml.Controls;
using Resesh.App.ViewModels;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.App.Controls;

/// <summary>
/// The colour tag and terminal overrides, shared by the SSH and local session editors.
/// <see cref="ApplyTo"/> writes only the fields the user changed: a value the pickers can't
/// show exactly (a lowercase or unlisted colour, a theme this build doesn't know, a size out
/// of range) is kept as it was saved instead of being replaced by the nearest choice.
/// </summary>
public sealed partial class SessionAppearanceForm : UserControl
{
    private IReadOnlyList<SessionColorChoice> _colors = SessionColors.All;
    private Shown _loaded = new(0, null, "", double.NaN, double.NaN, 0, 0, 0);

    /// <summary>What the controls show, to tell which ones the user changed.</summary>
    private sealed record Shown(
        int Color, string? Theme, string FontFamily, double FontSize, double Scrollback,
        int Mouse, int Recording, int History);

    public SessionAppearanceForm()
    {
        InitializeComponent();
        NumberBoxRanges.Limit(OverrideFontSizeBox, SettingRanges.FontSize);
        NumberBoxRanges.Limit(OverrideScrollbackBox, SettingRanges.Scrollback);
        OverrideMouseBox.ItemsSource = MouseCaptureChoice.OverrideNames();
    }

    /// <summary>Shows <paramref name="session"/>'s colour and overrides; null for a new session.</summary>
    public void Load(Session? session)
    {
        _colors = SessionColors.ChoicesFor(session?.ColorTag);
        ColorBox.Items.Clear();
        foreach (var choice in _colors)
            ColorBox.Items.Add(choice.Name);
        ColorBox.SelectedIndex = SessionColors.IndexIn(_colors, session?.ColorTag);

        var overrides = session?.Overrides ?? new TerminalOverrides();
        OverrideThemePicker.ThemeId = overrides.Theme;
        OverrideFontFamilyBox.FamilyName = overrides.FontFamily ?? "";
        OverrideFontSizeBox.Value = overrides.FontSize ?? double.NaN;
        OverrideScrollbackBox.Value = overrides.Scrollback ?? double.NaN;
        OverrideMouseBox.SelectedIndex = MouseCaptureChoice.OverrideIndex(overrides.MouseCapture);
        OverrideRecordingBox.SelectedIndex = OverrideIndex(overrides.AlwaysRecord);
        OverrideHistoryBox.SelectedIndex = OverrideIndex(overrides.KeepCommandHistory);
        _loaded = Current();
    }

    /// <summary><paramref name="basis"/> with the colour and overrides the user changed here.
    /// Highlight rule deltas belong to the tab's Highlighting menu and pass through.</summary>
    public Session ApplyTo(Session basis)
    {
        var now = Current();
        T Keep<T>(Func<Shown, object?> shown, T saved, Func<T> edited) =>
            Equals(shown(now), shown(_loaded)) ? saved : edited();

        var saved = basis.Overrides ?? new TerminalOverrides();
        var overrides = saved with
        {
            Theme = Keep(s => s.Theme, saved.Theme, () => now.Theme),
            FontFamily = Keep(s => s.FontFamily, saved.FontFamily,
                () => string.IsNullOrWhiteSpace(now.FontFamily) ? null : now.FontFamily.Trim()),
            FontSize = Keep(s => s.FontSize, saved.FontSize, () => Whole(now.FontSize, SettingRanges.FontSize)),
            Scrollback = Keep(s => s.Scrollback, saved.Scrollback, () => Whole(now.Scrollback, SettingRanges.Scrollback)),
            MouseCapture = Keep(s => s.Mouse, saved.MouseCapture, () => MouseCaptureChoice.OverrideAt(now.Mouse)),
            AlwaysRecord = Keep(s => s.Recording, saved.AlwaysRecord, () => OverrideAt(now.Recording)),
            KeepCommandHistory = Keep(s => s.History, saved.KeepCommandHistory, () => OverrideAt(now.History)),
        };
        return basis with
        {
            ColorTag = Keep(s => s.Color, basis.ColorTag, () => _colors[Math.Max(0, now.Color)].Hex),
            // An all-null overrides object is stored as null so sessions.json stays clean.
            Overrides = overrides.IsEmpty ? null : overrides,
        };
    }

    /// <summary>The control for a session settings command to focus; null for other targets.</summary>
    public Control? FocusTarget(SessionSettingsTarget target) => target switch
    {
        SessionSettingsTarget.Theme => OverrideThemePicker.FocusTarget,
        SessionSettingsTarget.FontFamily => OverrideFontFamilyBox.FocusTarget,
        SessionSettingsTarget.FontSize => OverrideFontSizeBox,
        SessionSettingsTarget.Scrollback => OverrideScrollbackBox,
        SessionSettingsTarget.MouseCapture => OverrideMouseBox,
        SessionSettingsTarget.AlwaysRecord => OverrideRecordingBox,
        SessionSettingsTarget.CommandHistory => OverrideHistoryBox,
        _ => null,
    };

    private Shown Current() => new(
        ColorBox.SelectedIndex,
        OverrideThemePicker.ThemeId,
        OverrideFontFamilyBox.FamilyName ?? "",
        OverrideFontSizeBox.Value,
        OverrideScrollbackBox.Value,
        OverrideMouseBox.SelectedIndex,
        OverrideRecordingBox.SelectedIndex,
        OverrideHistoryBox.SelectedIndex);

    private static int? Whole(double value, IntRange range) => double.IsNaN(value) ? null : range.Clamp((int)value);

    private static int OverrideIndex(bool? value) => value switch
    {
        true => 1,
        false => 2,
        null => 0,
    };

    private static bool? OverrideAt(int index) => index switch
    {
        1 => true,
        2 => false,
        _ => null,
    };
}
