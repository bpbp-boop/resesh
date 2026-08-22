using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Resesh.App.Controls;

public sealed class CommandPaletteEntry
{
    public required string Title { get; init; }
    public required string Category { get; init; }
    public string Keywords { get; init; } = "";
    public string Shortcut { get; init; } = "";
    public required Func<Task> ExecuteAsync { get; init; }
    public bool KeepActionFocus { get; init; }

    internal bool Matches(string term) =>
        Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Category.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Keywords.Contains(term, StringComparison.OrdinalIgnoreCase);
}

public sealed partial class CommandPaletteView : UserControl
{
    private IReadOnlyList<CommandPaletteEntry> _commands = [];

    public event Action? CloseRequested;
    public event Action<CommandPaletteEntry>? CommandInvoked;

    public bool IsOpen => Visibility == Visibility.Visible;

    public CommandPaletteView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(Palette_KeyDown), handledEventsToo: true);
    }

    public void Open(IReadOnlyList<CommandPaletteEntry> commands)
    {
        _commands = commands;
        SearchBox.Text = "";
        RefreshResults();
        Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() =>
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        });
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        SearchBox.Text = "";
        CommandList.ItemsSource = null;
        _commands = [];
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshResults();

    private void RefreshResults()
    {
        var query = SearchBox.Text.Trim();
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = _commands
            .Where(command => terms.All(command.Matches))
            .OrderBy(command => Score(command, query))
            .ToList();

        CommandList.ItemsSource = matches;
        CommandList.SelectedIndex = matches.Count > 0 ? 0 : -1;
        CommandList.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResultsText.Visibility = matches.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static int Score(CommandPaletteEntry command, string query)
    {
        if (query.Length == 0)
            return 0;
        if (command.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (command.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (command.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        return 3;
    }

    private void Palette_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                CloseRequested?.Invoke();
                break;
            case VirtualKey.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                InvokeSelected();
                break;
        }
    }

    private void MoveSelection(int offset)
    {
        if (CommandList.Items.Count == 0)
            return;

        var current = Math.Max(0, CommandList.SelectedIndex);
        var next = Math.Clamp(current + offset, 0, CommandList.Items.Count - 1);
        CommandList.SelectedIndex = next;
        CommandList.ScrollIntoView(CommandList.Items[next]);
    }

    private void InvokeSelected()
    {
        if (CommandList.SelectedItem is CommandPaletteEntry command)
            CommandInvoked?.Invoke(command);
    }

    private void CommandList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommandPaletteEntry command)
            CommandInvoked?.Invoke(command);
    }

    private void Backdrop_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
