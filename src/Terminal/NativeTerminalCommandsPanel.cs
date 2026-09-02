using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Resesh.Terminal;

/// <summary>Docked command history for marks owned by the native terminal buffer.</summary>
internal sealed class NativeTerminalCommandsPanel : Grid
{
    internal const double PreferredWidth = 400;
    private readonly TextBlock _count = new();
    private readonly ListView _list = new();
    private readonly ICommand _copyCommand;

    internal event Action? CloseRequested;
    internal event Action<ulong>? JumpRequested;
    internal event Action<ulong>? CopyRequested;

    internal NativeTerminalCommandsPanel()
    {
        _copyCommand = new DelegateCommand(parameter =>
        {
            if (parameter is ulong id)
                CopyRequested?.Invoke(id);
        });
        _list.ItemTemplate = CommandTemplate;
        Width = PreferredWidth;
        var surface = new Border
        {
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
        Visibility = Visibility.Collapsed;
        surface.Background = ThemeBrush("SolidBackgroundFillColorBaseBrush");
        surface.BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush");
        AutomationProperties.SetAutomationId(this, "NativeTerminalCommandsPanel");
        AutomationProperties.SetName(this, "Commands");

        var title = new TextBlock
        {
            Text = "Commands",
            Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _count.Margin = new Thickness(8, 0, 0, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;

        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711" },
            MinWidth = 32,
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetAutomationId(close, "NativeTerminalCommandsClose");
        AutomationProperties.SetName(close, "Close commands");
        ToolTipService.SetToolTip(close, "Close commands");
        close.Click += (_, _) => CloseRequested?.Invoke();

        var header = new Grid { Margin = new Thickness(4, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(_count, 1);
        header.Children.Add(_count);
        Grid.SetColumn(close, 3);
        header.Children.Add(close);

        AutomationProperties.SetAutomationId(_list, "NativeTerminalCommandsList");
        AutomationProperties.SetName(_list, "Command history");
        _list.SelectionMode = ListViewSelectionMode.Single;
        _list.IsItemClickEnabled = true;
        _list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is CommandItem command)
                JumpRequested?.Invoke(command.Id);
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(header);
        Grid.SetRow(_list, 1);
        layout.Children.Add(_list);
        surface.Child = layout;
        Children.Add(surface);
    }

    internal void SetCommands(
        IReadOnlyList<NativeTerminalApi.MarkRecord> marks,
        Func<ulong, string> textForMark)
    {
        var commands = marks
            .Where(mark => mark.Kind is NativeTerminalApi.MarkKind.ExactCommand or NativeTerminalApi.MarkKind.ApplicationCommand)
            .OrderBy(mark => mark.Row)
            .Select(mark => new CommandItem(mark, textForMark, _copyCommand))
            .ToArray();
        _count.Text = commands.Length == 0 ? string.Empty : commands.Length.ToString();

        _list.Header = commands.Length == 0
            ? new TextBlock
            {
                Text = "No commands yet",
                Margin = new Thickness(8),
            }
            : null;
        _list.ItemsSource = commands;
        if (commands.Length > 0)
            _list.ScrollIntoView(commands[^1]);
    }

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
        private string? _text;
        private Brush? _statusBrush;

        internal CommandItem(
            NativeTerminalApi.MarkRecord mark,
            Func<ulong, string> textForMark,
            ICommand copyCommand)
        {
            _mark = mark;
            _textForMark = textForMark;
            CopyCommand = copyCommand;
        }

        public ulong Id => _mark.Id;
        public string Text => _text ??= ReadText();
        public string StatusName => _mark.ExitCode switch
        {
            0 => "Succeeded",
            not null => "Failed",
            _ => "Status unknown",
        };
        public string AutomationName => $"{Text}, {StatusName.ToLowerInvariant()}";
        public string CopyAutomationId => $"NativeTerminalCopyCommand{Id}";
        public ICommand CopyCommand { get; }
        public Brush StatusBrush => _statusBrush ??=
            new SolidColorBrush(NativeTerminalRuler.FromColorRef(_mark.Color));

        private string ReadText()
        {
            var text = _textForMark(Id);
            return string.IsNullOrWhiteSpace(text) ? $"Line {_mark.Row + 1}" : text;
        }
    }

    private static readonly DataTemplate CommandTemplate = (DataTemplate)XamlReader.Load(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid Padding="4,2,4,2"
                AutomationProperties.Name="{Binding AutomationName}">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto" />
              <ColumnDefinition Width="*" />
              <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <Ellipse Width="8"
                     Height="8"
                     Margin="0,0,8,0"
                     VerticalAlignment="Center"
                     Fill="{Binding StatusBrush}"
                     AutomationProperties.Name="{Binding StatusName}" />
            <TextBlock Grid.Column="1"
                       Text="{Binding Text}"
                       TextTrimming="CharacterEllipsis"
                       VerticalAlignment="Center" />
            <Button Grid.Column="2"
                    Command="{Binding CopyCommand}"
                    CommandParameter="{Binding Id}"
                    MinWidth="28"
                    Width="28"
                    Height="28"
                    Margin="8,0,0,0"
                    AutomationProperties.Name="Copy output"
                    AutomationProperties.AutomationId="{Binding CopyAutomationId}">
              <FontIcon Glyph="&#xE8C8;" FontSize="12" />
            </Button>
          </Grid>
        </DataTemplate>
        """);

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80));

}
