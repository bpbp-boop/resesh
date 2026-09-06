using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Resesh.Terminal;

/// <summary>Floating command history for marks owned by the native terminal buffer.</summary>
internal sealed class NativeTerminalCommandsPanel : Grid
{
    private const int AnsiBrightRedIndex = 9;
    private const int AnsiBrightGreenIndex = 10;
    private const uint ErrorMarkCategory = 1;
    private const uint SuccessMarkCategory = 3;
    internal const double PreferredWidth = 400;
    internal const double MaximumHeight = 520;
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

    internal event Action? CloseRequested;
    internal event Action<ulong>? JumpRequested;
    internal event Action<ulong>? CopyRequested;
    internal event Action? DesiredHeightChanged;
    internal double DesiredHeight { get; private set; } = 88;

    internal NativeTerminalCommandsPanel()
    {
        _copyCommand = new DelegateCommand(parameter =>
        {
            if (parameter is ulong id)
                CopyRequested?.Invoke(id);
        });
        _list.ItemTemplate = CommandTemplate;
        Width = PreferredWidth;
        _surface.BorderThickness = new Thickness(1);
        _surface.CornerRadius = ThemeCornerRadius("OverlayCornerRadius", new CornerRadius(8));
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(this, "NativeTerminalCommandsPanel");
        AutomationProperties.SetName(this, "Commands");

        _title.Text = "Commands";
        _title.Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style;
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _count.Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style;
        _count.Margin = new Thickness(8, 0, 0, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;

        _close.Content = new FontIcon { Glyph = "\uE711", FontSize = 10 };
        _close.MinWidth = 24;
        _close.Width = 24;
        _close.Height = 24;
        _close.Padding = new Thickness(0);
        _close.Background = _transparentBrush;
        _close.BorderThickness = new Thickness(0);
        _close.CornerRadius = ThemeCornerRadius("ControlCornerRadius", new CornerRadius(4));
        _close.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetAutomationId(_close, "NativeTerminalCommandsClose");
        AutomationProperties.SetName(_close, "Close commands");
        ToolTipService.SetToolTip(_close, "Close commands");
        _close.Click += (_, _) => CloseRequested?.Invoke();

        var header = new Grid { Margin = new Thickness(10, 5, 6, 5) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_title);
        Grid.SetColumn(_count, 1);
        header.Children.Add(_count);
        Grid.SetColumn(_close, 3);
        header.Children.Add(_close);

        AutomationProperties.SetAutomationId(_list, "NativeTerminalCommandsList");
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
        layout.Children.Add(new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _borderBrush,
            Child = header,
        });
        Grid.SetRow(_list, 1);
        layout.Children.Add(_list);
        _surface.Child = layout;
        Children.Add(_surface);
        ConfigureThemeResources();
        ApplyTheme(NativeTerminalThemeCatalog.Find("dark"), "Cascadia Mono");
    }

    internal void ApplyTheme(NativeTerminalApi.TerminalTheme theme, string fontFamily)
    {
        var background = NativeTerminalRuler.FromColorRef(theme.DefaultBackground);
        var foreground = NativeTerminalRuler.FromColorRef(theme.DefaultForeground);
        var selection = NativeTerminalRuler.FromColorRef(theme.DefaultSelectionBackground);
        _backgroundBrush.Color = background;
        _foregroundBrush.Color = foreground;
        _mutedBrush.Color = WithAlpha(foreground, 0xA6);
        _borderBrush.Color = WithAlpha(foreground, 0x52);
        _hoverBrush.Color = WithAlpha(foreground, 0x22);
        _pressedBrush.Color = WithAlpha(foreground, 0x36);
        _selectedBrush.Color = WithAlpha(selection, 0x78);
        _successBrush.Color = NativeTerminalRuler.FromColorRef(theme.ColorTable[AnsiBrightGreenIndex]);
        _failureBrush.Color = NativeTerminalRuler.FromColorRef(theme.ColorTable[AnsiBrightRedIndex]);
        _surface.Background = _backgroundBrush;
        _surface.BorderBrush = _borderBrush;
        _title.Foreground = _foregroundBrush;
        _count.Foreground = _mutedBrush;
        _close.Foreground = _mutedBrush;
        _list.Foreground = _foregroundBrush;
        _list.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? "Cascadia Mono" : fontFamily);
    }

    internal void SetCommands(
        IReadOnlyList<NativeTerminalApi.MarkRecord> marks,
        Func<ulong, string> textForMark)
    {
        var commands = marks
            .Where(mark => mark.Kind is NativeTerminalApi.MarkKind.ExactCommand or NativeTerminalApi.MarkKind.ApplicationCommand)
            .GroupBy(mark => mark.Row)
            .Select(MergeCommandMarks)
            .OrderBy(mark => mark.Row)
            .Select(mark => new CommandItem(
                mark,
                textForMark,
                _copyCommand,
                _successBrush,
                _failureBrush,
                _mutedBrush))
            .ToArray();
        _count.Text = commands.Length == 0 ? string.Empty : commands.Length.ToString();
        DesiredHeight = Math.Min(MaximumHeight, 40 + Math.Max(1, commands.Length) * 28);

        _list.Header = commands.Length == 0
            ? new TextBlock
            {
                Text = "No commands yet",
                Margin = new Thickness(8),
                Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
                Foreground = _mutedBrush,
            }
            : null;
        _list.ItemsSource = commands;
        if (commands.Length > 0)
            _list.ScrollIntoView(commands[^1]);
        DesiredHeightChanged?.Invoke();
    }

    private static NativeTerminalApi.MarkRecord MergeCommandMarks(
        IGrouping<int, NativeTerminalApi.MarkRecord> marks)
    {
        var applicationMark = marks.FirstOrDefault(mark =>
            mark.Kind == NativeTerminalApi.MarkKind.ApplicationCommand);
        if (applicationMark.Id == 0 || HasStatus(applicationMark))
            return applicationMark.Id == 0 ? marks.First() : applicationMark;

        var exactMark = marks.FirstOrDefault(mark =>
            mark.Kind == NativeTerminalApi.MarkKind.ExactCommand && HasStatus(mark));
        return exactMark.Id == 0
            ? applicationMark
            : applicationMark with
            {
                Category = exactMark.Category,
                ExitCode = exactMark.ExitCode,
            };
    }

    private static bool HasStatus(NativeTerminalApi.MarkRecord mark) =>
        mark.ExitCode is not null || mark.Category is ErrorMarkCategory or SuccessMarkCategory;

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }

    private sealed class CommandItem
    {
        private readonly NativeTerminalApi.MarkRecord _mark;
        private readonly Func<ulong, string> _textForMark;
        private readonly Brush _successBrush;
        private readonly Brush _failureBrush;
        private readonly Brush _unknownBrush;
        private string? _text;

        internal CommandItem(
            NativeTerminalApi.MarkRecord mark,
            Func<ulong, string> textForMark,
            ICommand copyCommand,
            Brush successBrush,
            Brush failureBrush,
            Brush unknownBrush)
        {
            _mark = mark;
            _textForMark = textForMark;
            _successBrush = successBrush;
            _failureBrush = failureBrush;
            _unknownBrush = unknownBrush;
            CopyCommand = copyCommand;
        }

        public ulong Id => _mark.Id;
        public string Text => _text ??= ReadText();
        private bool Succeeded => _mark.ExitCode == 0 ||
            (_mark.ExitCode is null && _mark.Category == SuccessMarkCategory);
        private bool Failed => _mark.ExitCode is not null || _mark.Category == ErrorMarkCategory;
        public string StatusName => Succeeded ? "Succeeded" : Failed ? "Failed" : "Status unknown";
        public string AutomationName => $"{Text}, {StatusName.ToLowerInvariant()}";
        public string CopyAutomationId => $"NativeTerminalCopyCommand{Id}";
        public ICommand CopyCommand { get; }
        public Brush StatusBrush => Succeeded ? _successBrush : Failed ? _failureBrush : _unknownBrush;

        private string ReadText()
        {
            var text = _textForMark(Id);
            return string.IsNullOrWhiteSpace(text) ? $"Line {_mark.Row + 1}" : text;
        }
    }

    private static readonly DataTemplate CommandTemplate = (DataTemplate)XamlReader.Load(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid MinHeight="28"
                Padding="4,0,4,0"
                AutomationProperties.Name="{Binding AutomationName}">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto" />
              <ColumnDefinition Width="*" />
              <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <Ellipse Width="7"
                     Height="7"
                     Margin="4,0,8,0"
                     VerticalAlignment="Center"
                     Fill="{Binding StatusBrush}"
                     ToolTipService.ToolTip="{Binding StatusName}"
                     AutomationProperties.Name="{Binding StatusName}" />
            <TextBlock Grid.Column="1"
                       Text="{Binding Text}"
                       Style="{StaticResource CaptionTextBlockStyle}"
                       TextTrimming="CharacterEllipsis"
                       VerticalAlignment="Center" />
            <Button Grid.Column="2"
                    Command="{Binding CopyCommand}"
                    CommandParameter="{Binding Id}"
                    MinWidth="24"
                    Width="24"
                    Height="24"
                    Margin="4,0,0,0"
                    Padding="0"
                    Background="{ThemeResource ButtonBackground}"
                    BorderThickness="0"
                    CornerRadius="{ThemeResource ControlCornerRadius}"
                    AutomationProperties.Name="Copy output"
                    AutomationProperties.AutomationId="{Binding CopyAutomationId}">
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
        Resources["ButtonBackgroundDisabled"] = _transparentBrush;
        Resources["ButtonForeground"] = _mutedBrush;
        Resources["ButtonForegroundPointerOver"] = _foregroundBrush;
        Resources["ButtonForegroundPressed"] = _foregroundBrush;
        Resources["ButtonForegroundDisabled"] = _mutedBrush;
        Resources["ButtonBorderBrush"] = _transparentBrush;
        Resources["ButtonBorderBrushPointerOver"] = _transparentBrush;
        Resources["ButtonBorderBrushPressed"] = _transparentBrush;
        Resources["ButtonBorderBrushDisabled"] = _transparentBrush;
        Resources["ListViewItemBackground"] = _transparentBrush;
        Resources["ListViewItemBackgroundPointerOver"] = _hoverBrush;
        Resources["ListViewItemBackgroundPressed"] = _pressedBrush;
        Resources["ListViewItemBackgroundSelected"] = _selectedBrush;
        Resources["ListViewItemBackgroundSelectedPointerOver"] = _selectedBrush;
        Resources["ListViewItemBackgroundSelectedPressed"] = _selectedBrush;
        Resources["ListViewItemForeground"] = _foregroundBrush;
        Resources["ListViewItemForegroundPointerOver"] = _foregroundBrush;
        Resources["ListViewItemForegroundSelected"] = _foregroundBrush;
        Resources["ListViewItemForegroundSelectedPointerOver"] = _foregroundBrush;
        Resources["ListViewItemForegroundSelectedPressed"] = _foregroundBrush;
        Resources["ListViewItemMinHeight"] = 28d;
        Resources["ListViewItemPadding"] = new Thickness(0);
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static CornerRadius ThemeCornerRadius(string key, CornerRadius fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is CornerRadius radius
            ? radius
            : fallback;

}
