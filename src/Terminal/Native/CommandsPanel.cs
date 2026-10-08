using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Resesh.Terminal.Native;

/// <summary>Floating list of the terminal's command marks (terminal.html's commands panel):
/// an exit-status dot, the command, when it ran, and a copy-output button. Clicking a row
/// jumps to the command. Adapted from the parked native-terminal branch.</summary>
internal sealed class CommandsPanel : Grid
{
    internal const double PreferredWidth = 420;
    internal const double MaximumHeight = 520;
    private const int AnsiBrightRed = 9, AnsiBrightGreen = 10;

    private readonly TextBlock _count = new();
    private readonly ListView _list = new();
    private readonly ICommand _copyCommand;
    private readonly Border _surface = new();
    private readonly TextBlock _title = new();
    private readonly Button _close = new();
    private readonly SolidColorBrush _backgroundBrush = new();
    private readonly SolidColorBrush _foregroundBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _borderBrush = new();
    private readonly SolidColorBrush _hoverBrush = new();
    private readonly SolidColorBrush _pressedBrush = new();
    private readonly SolidColorBrush _selectedBrush = new();
    private readonly SolidColorBrush _successBrush = new();
    private readonly SolidColorBrush _failureBrush = new();
    private readonly SolidColorBrush _transparentBrush = new(Microsoft.UI.Colors.Transparent);
    private long _fingerprint = -1;

    internal event Action? CloseRequested;
    internal event Action<long>? JumpRequested;
    internal event Action<long>? CopyRequested;

    internal CommandsPanel()
    {
        _copyCommand = new DelegateCommand(parameter =>
        {
            if (parameter is long id)
                CopyRequested?.Invoke(id);
        });
        _list.ItemTemplate = CommandTemplate;
        Width = PreferredWidth;
        MaxHeight = MaximumHeight;
        Margin = new Thickness(0, 8, 22, 0);
        _surface.BorderThickness = new Thickness(1);
        _surface.CornerRadius = ThemeCornerRadius("OverlayCornerRadius", new CornerRadius(8));
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(this, "CommandsPanel");
        AutomationProperties.SetName(this, "Commands");

        _title.Text = "Commands";
        _title.Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style;
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _count.Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style;
        _count.Margin = new Thickness(8, 0, 0, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;

        _close.Content = new FontIcon { Glyph = "", FontSize = 10 };
        _close.MinWidth = 24;
        _close.Width = 24;
        _close.Height = 24;
        _close.Padding = new Thickness(0);
        _close.Background = _transparentBrush;
        _close.BorderThickness = new Thickness(0);
        _close.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetName(_close, "Close commands");
        ToolTipService.SetToolTip(_close, "Close commands (Ctrl+Shift+O)");
        _close.Click += (_, _) => CloseRequested?.Invoke();

        var header = new Grid { Margin = new Thickness(10, 5, 6, 5) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_title);
        SetColumn(_count, 1);
        header.Children.Add(_count);
        SetColumn(_close, 3);
        header.Children.Add(_close);

        AutomationProperties.SetName(_list, "Command history");
        _list.SelectionMode = ListViewSelectionMode.Single;
        _list.IsItemClickEnabled = true;
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _list.Margin = new Thickness(0, 3, 0, 3);
        _list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is CommandItem command)
                JumpRequested?.Invoke(command.Id);
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = _borderBrush, Child = header });
        SetRow(_list, 1);
        layout.Children.Add(_list);
        _surface.Child = layout;
        Children.Add(_surface);
        ConfigureThemeResources();
    }

    internal bool IsOpen => Visibility == Visibility.Visible;

    internal void ApplyTheme(TerminalTheme theme, string fontFamily)
    {
        var foreground = ToColor(theme.Foreground);
        _backgroundBrush.Color = ToColor(theme.Background);
        _foregroundBrush.Color = foreground;
        _mutedBrush.Color = WithAlpha(foreground, 0xA6);
        _borderBrush.Color = WithAlpha(foreground, 0x52);
        _hoverBrush.Color = WithAlpha(foreground, 0x22);
        _pressedBrush.Color = WithAlpha(foreground, 0x36);
        _selectedBrush.Color = WithAlpha(ToColor(theme.Selection), 0x78);
        _successBrush.Color = ToColor(theme.Ansi[AnsiBrightGreen]);
        _failureBrush.Color = ToColor(theme.Ansi[AnsiBrightRed]);
        _surface.Background = _backgroundBrush;
        _surface.BorderBrush = _borderBrush;
        _title.Foreground = _foregroundBrush;
        _count.Foreground = _mutedBrush;
        _close.Foreground = _mutedBrush;
        _list.Foreground = _foregroundBrush;
        var family = fontFamily.Split(',')[0].Trim().Trim('"', '\'');
        _list.FontFamily = new FontFamily(family.Length == 0 ? "Cascadia Mono" : family);
    }

    /// <summary>Rebinds the list when the marks changed; keeps scroll position otherwise.</summary>
    internal void SetCommands(IReadOnlyList<CommandMarkInfo> marks)
    {
        var fingerprint = 1469598103934665603L;
        foreach (var mark in marks)
            fingerprint = unchecked((fingerprint ^ mark.Id ^ ((long)(mark.Exit ?? int.MinValue) << 20) ^ mark.Text.Length) * 1099511628211L);
        if (fingerprint == _fingerprint)
            return;
        _fingerprint = fingerprint;
        var items = marks.Select(m => new CommandItem(m, _copyCommand, _successBrush, _failureBrush, _mutedBrush)).ToArray();
        _count.Text = items.Length == 0 ? string.Empty : items.Length.ToString();
        _list.Header = items.Length == 0
            ? new TextBlock
            {
                Text = "No commands yet",
                Margin = new Thickness(8),
                Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
                Foreground = _mutedBrush,
            }
            : null;
        _list.ItemsSource = items;
        if (items.Length > 0)
            _list.ScrollIntoView(items[^1]);
    }

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }

    private sealed class CommandItem(CommandMarkInfo mark, ICommand copyCommand, Brush success, Brush failure, Brush unknown)
    {
        public long Id => mark.Id;
        public string Text => string.IsNullOrWhiteSpace(mark.Text) ? $"Line {mark.Line + 1}" : mark.Text;
        public string Time => DateTimeOffset.FromUnixTimeMilliseconds(mark.UnixMs).ToLocalTime().ToString("HH:mm:ss");
        public string StatusName => mark.Exit switch
        {
            0 => "Succeeded",
            { } code => $"Failed (exit {code})",
            null => "Status unknown",
        };
        public string AutomationName => $"{Text}, {StatusName.ToLowerInvariant()}, {Time}";
        public ICommand CopyCommand => copyCommand;
        public Brush StatusBrush => mark.Exit switch { 0 => success, not null => failure, null => unknown };
    }

    private static readonly DataTemplate CommandTemplate = (DataTemplate)XamlReader.Load(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid MinHeight="28" Padding="4,0,4,0" AutomationProperties.Name="{Binding AutomationName}">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto" />
              <ColumnDefinition Width="*" />
              <ColumnDefinition Width="Auto" />
              <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <Ellipse Width="7" Height="7" Margin="4,0,8,0" VerticalAlignment="Center"
                     Fill="{Binding StatusBrush}" ToolTipService.ToolTip="{Binding StatusName}" />
            <TextBlock Grid.Column="1" Text="{Binding Text}" Style="{StaticResource CaptionTextBlockStyle}"
                       TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
            <TextBlock Grid.Column="2" Text="{Binding Time}" Style="{StaticResource CaptionTextBlockStyle}"
                       Opacity="0.6" Margin="8,0,0,0" VerticalAlignment="Center" />
            <Button Grid.Column="3" Command="{Binding CopyCommand}" CommandParameter="{Binding Id}"
                    MinWidth="24" Width="24" Height="24" Margin="4,0,0,0" Padding="0"
                    Background="{ThemeResource ButtonBackground}" BorderThickness="0"
                    AutomationProperties.Name="Copy output" ToolTipService.ToolTip="Copy output">
              <FontIcon Glyph="&#xE8C8;" FontSize="11" />
            </Button>
          </Grid>
        </DataTemplate>
        """);

    private void ConfigureThemeResources()
    {
        Resources["ButtonBackground"] = _transparentBrush;
        Resources["ButtonBackgroundPointerOver"] = _hoverBrush;
        Resources["ButtonBackgroundPressed"] = _pressedBrush;
        Resources["ButtonForeground"] = _mutedBrush;
        Resources["ButtonForegroundPointerOver"] = _foregroundBrush;
        Resources["ButtonForegroundPressed"] = _foregroundBrush;
        Resources["ButtonBorderBrush"] = _transparentBrush;
        Resources["ButtonBorderBrushPointerOver"] = _transparentBrush;
        Resources["ButtonBorderBrushPressed"] = _transparentBrush;
        Resources["ListViewItemBackground"] = _transparentBrush;
        Resources["ListViewItemBackgroundPointerOver"] = _hoverBrush;
        Resources["ListViewItemBackgroundPressed"] = _pressedBrush;
        Resources["ListViewItemBackgroundSelected"] = _selectedBrush;
        Resources["ListViewItemBackgroundSelectedPointerOver"] = _selectedBrush;
        Resources["ListViewItemBackgroundSelectedPressed"] = _selectedBrush;
        Resources["ListViewItemForeground"] = _foregroundBrush;
        Resources["ListViewItemForegroundPointerOver"] = _foregroundBrush;
        Resources["ListViewItemForegroundSelected"] = _foregroundBrush;
        Resources["ListViewItemMinHeight"] = 28d;
        Resources["ListViewItemPadding"] = new Thickness(0);
    }

    internal static Color ToColor(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static CornerRadius ThemeCornerRadius(string key, CornerRadius fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is CornerRadius radius ? radius : fallback;
}
