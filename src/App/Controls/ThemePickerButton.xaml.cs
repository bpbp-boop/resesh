using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Resesh.Core.Storage;
using Windows.UI;

namespace Resesh.App.Controls;

/// <summary>A compact theme choice with searchable light and dark galleries.
/// Unknown ids, including legacy "system", display verbatim and stay unchanged until a pick.</summary>
public sealed partial class ThemePickerButton : UserControl
{
    public static readonly DependencyProperty ThemeIdProperty = DependencyProperty.Register(
        nameof(ThemeId), typeof(string), typeof(ThemePickerButton), new PropertyMetadata(null, ChoiceUpdated));
    public static readonly DependencyProperty InheritLabelProperty = DependencyProperty.Register(
        nameof(InheritLabel), typeof(string), typeof(ThemePickerButton), new PropertyMetadata(null, ChoiceUpdated));

    private static readonly IReadOnlyList<ThemeChoice> LightChoices = ThemeCatalog.All.Where(theme => theme.IsLight).ToArray();
    private static readonly IReadOnlyList<ThemeChoice> DarkChoices = ThemeCatalog.All.Where(theme => !theme.IsLight).ToArray();
    private readonly Rectangle[] _colors = new Rectangle[4];

    public ThemePickerButton()
    {
        InitializeComponent();
        for (var i = 0; i < _colors.Length; i++)
        {
            _colors[i] = new Rectangle { Width = 8, Height = 12 };
            PreviewColors.Children.Add(_colors[i]);
        }
        LightThemes.Themes = LightChoices;
        DarkThemes.Themes = DarkChoices;
        ShowChoice();
    }

    public string? ThemeId
    {
        get => (string?)GetValue(ThemeIdProperty);
        set => SetValue(ThemeIdProperty, value);
    }

    public string? InheritLabel
    {
        get => (string?)GetValue(InheritLabelProperty);
        set => SetValue(InheritLabelProperty, value);
    }

    public Control FocusTarget => PickerButton;

    /// <summary>Raised only when the user chooses a different theme or inherit.</summary>
    public event EventHandler? SelectionChanged;

    private static void ChoiceUpdated(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ThemePickerButton)sender).ShowChoice();

    private void ShowChoice()
    {
        if (PickerButton is null)
            return;
        var theme = ThemeCatalog.Contains(ThemeId) ? ThemeCatalog.Find(ThemeId) : null;
        var label = theme?.Name ?? ThemeId ?? InheritLabel ?? "Choose theme";
        ChoiceName.Text = label;
        AutomationProperties.SetName(PickerButton, $"Theme: {label}");
        InheritButton.Content = InheritLabel;
        InheritButton.Visibility = InheritLabel is null ? Visibility.Collapsed : Visibility.Visible;
        Preview.Visibility = theme is null ? Visibility.Collapsed : Visibility.Visible;
        if (theme is not null)
        {
            Preview.Background = Brush(theme.Palette.Background);
            _colors[0].Fill = Brush(theme.Palette.Foreground);
            for (var i = 1; i < _colors.Length; i++)
                _colors[i].Fill = Brush(theme.Palette.Ansi[i]);
        }
        LightThemes.SelectedTheme = theme?.IsLight == true ? theme : null;
        DarkThemes.SelectedTheme = theme?.IsLight == false ? theme : null;
    }

    private void PickerFlyout_Opened(object? sender, object args)
    {
        Filter.FocusTarget.Focus(FocusState.Programmatic);
        LightThemes.ScrollToSelection();
        DarkThemes.ScrollToSelection();
    }

    private void Filter_QueryChanged(object? sender, EventArgs args)
    {
        LightThemes.Query = Filter.Query;
        DarkThemes.Query = Filter.Query;
    }

    private void Gallery_SelectionChanged(object? sender, ThemeChoice theme) => Pick(theme.Id);

    private void InheritButton_Click(object sender, RoutedEventArgs args) => Pick(null);

    private void Pick(string? id)
    {
        var changed = !string.Equals(ThemeId, id, StringComparison.Ordinal);
        ThemeId = id;
        PickerFlyout.Hide();
        if (changed)
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static SolidColorBrush Brush(uint rgb) => new(Color.FromArgb(255,
        (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}
