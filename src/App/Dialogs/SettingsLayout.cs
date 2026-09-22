using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Resesh.App.Dialogs;

/// <summary>Shared section headings and field alignment for every Settings tab.</summary>
internal static class SettingsLayout
{
    internal static Grid SettingRow(string text, FrameworkElement field, bool stacked)
    {
        var control = field as Control ?? (field as Panel)?.Children.OfType<Control>().FirstOrDefault();
        switch (control)
        {
            case ComboBox combo: combo.Header = null; break;
            case TextBox box: box.Header = null; break;
            case NumberBox number: number.Header = null; break;
            case ToggleSwitch toggle: toggle.Header = null; break;
        }
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (control is not null)
        {
            AutomationProperties.SetLabeledBy(control, label);
            AutomationProperties.SetName(control, text);
        }
        var row = new Grid { ColumnSpacing = 16, RowSpacing = 4, MinHeight = 36 };
        row.Children.Add(label);
        row.Children.Add(field);
        ConfigureSettingRow(row, stacked);
        return row;
    }

    internal static void ConfigureSettingRow(Grid row, bool stacked)
    {
        row.ColumnDefinitions.Clear();
        row.RowDefinitions.Clear();
        // Keep labels compact and give inputs the remaining width on every Settings tab.
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(240),
        });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (stacked)
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        else
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var field = (FrameworkElement)row.Children[1];
        Grid.SetRow(field, stacked ? 1 : 0);
        Grid.SetColumn(field, stacked ? 0 : 1);
        field.HorizontalAlignment = field is ToggleSwitch ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        field.VerticalAlignment = VerticalAlignment.Center;
    }

    internal static Border SettingsGroup(string title, params UIElement[] rows)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var row in rows)
            panel.Children.Add(row);
        return SettingsSection(title, panel);
    }

    // A Grid also supports sections whose list must fill the available height.
    internal static Border SettingsSection(string title, FrameworkElement content)
    {
        var panel = new Grid { RowSpacing = 10 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        });
        Grid.SetRow(content, 1);
        panel.Children.Add(content);
        return new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = (Brush)Application.Current.Resources["SettingsCardBorderBrush"],
            Padding = new Thickness(0, 0, 0, 12),
            Child = panel,
        };
    }

    internal static Border PreviewSurface(UIElement child) => new()
    {
        Padding = new Thickness(12, 10, 12, 10),
        CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"],
        BorderThickness = new Thickness(1),
        BorderBrush = (Brush)Application.Current.Resources["SettingsCardBorderBrush"],
        Background = (Brush)Application.Current.Resources["SettingsCardBackgroundBrush"],
        Child = child,
    };
}
