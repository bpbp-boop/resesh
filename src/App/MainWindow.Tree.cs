using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Resesh.App.Controls;
using Resesh.App.Dialogs;
using Resesh.App.Terminal;
using Resesh.App.ViewModels;
using Resesh.Core.Input;
using Resesh.Core.Layout;
using Resesh.Core.Models;
using Resesh.Core.Storage;
using Windows.Graphics;
using Windows.System;

namespace Resesh.App;

// The session tree: pane sizing, filtering, selection, context menu, expansion, drag and drop.
public sealed partial class MainWindow
{
    // ---- tree pane persistence ----

    private readonly Dictionary<CommunityToolkit.WinUI.Controls.GridSplitter, Border> _splitterLines = [];
    private readonly Dictionary<Border, HashSet<TabGroupViewModel>> _paneBoundaries = [];
    private readonly HashSet<CommunityToolkit.WinUI.Controls.GridSplitter> _activeSplitters = [];
    private readonly Dictionary<SplitLayoutBranch<TabGroupViewModel>, IReadOnlyList<double>> _savedPaneSizes = [];

    private void ConfigureSplitter(CommunityToolkit.WinUI.Controls.GridSplitter splitter, Border line)
    {
        _splitterLines[splitter] = line;
        splitter.PointerEntered += (_, _) => SetSplitterActive(splitter, active: true);
        splitter.PointerExited += (_, _) => SetSplitterActive(splitter, active: false);
        splitter.ManipulationStarted += (_, _) => SetSplitterActive(splitter, active: true);
        splitter.ManipulationCompleted += (_, _) => SetSplitterActive(splitter, active: false);
    }

    private void TreeSplitter_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        SetSplitterActive(sender, active: false);
        SaveTreePaneWidth();
    }

    private void SetSplitterActive(object sender, bool active)
    {
        if (sender is CommunityToolkit.WinUI.Controls.GridSplitter splitter
            && _splitterLines.TryGetValue(splitter, out var line))
        {
            if (active)
                _activeSplitters.Add(splitter);
            else
                _activeSplitters.Remove(splitter);
            line.Background = SplitterBrush(line, active);
        }
    }

    /// <summary>Pane focus is carried by the one-pixel gutter the two panes already share.
    /// A border drawn inside each pane would stack with it, so the seam is the border:
    /// gutters touching the focused pane take the theme's focus color, the rest stay
    /// neutral. Same-orientation branches are always flattened, so every leaf under a
    /// boundary's two subtrees really does touch that boundary.</summary>
    private Microsoft.UI.Xaml.Media.Brush SplitterBrush(Border line, bool active)
    {
        if (active)
            return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SessionSplitterHoverBrush"];

        if (!_paneBoundaries.TryGetValue(line, out var groups))
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(_themePalette.Divider); // the tree splitter

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(
            groups.Contains(ViewModel.FocusedGroup) ? _themePalette.PaneFocusBorder : _themePalette.PaneBorder);
    }

    private void SaveTreePaneWidth()
    {
        if (!_sessionsPaneOpen || TreeColumn.ActualWidth < 180)
            return;
        _sessionsPaneWidth = TreeColumn.ActualWidth;
        App.SaveSettings(App.Settings.Current with { TreePaneWidth = _sessionsPaneWidth });
    }

    /// <summary>
    /// TreeViewItem ignores IsExpanded applied while its children aren't realized yet
    /// (container recycling), so after each rebuild we push the view-model state onto the
    /// realized containers, re-queueing until the tree settles (expanding a node realizes
    /// its children on a later tick).
    /// </summary>
    private void ScheduleExpansionSync(int remainingPasses = 10)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var changed = false;

            void Sync(IEnumerable<TreeNodeViewModel> nodes)
            {
                foreach (var node in nodes.Where(n => n.IsFolder))
                {
                    if (SessionTree.ContainerFromItem(node) is TreeViewItem container)
                    {
                        if (container.IsExpanded != node.IsExpanded)
                        {
                            Trace($"sync: '{node.FolderPath}' container={container.IsExpanded} vm={node.IsExpanded} -> pushing");
                            container.IsExpanded = node.IsExpanded;
                            changed = true;
                        }
                        Sync(node.Children);
                    }
                    else if (node.IsExpanded)
                    {
                        Trace($"sync: '{node.FolderPath}' no container yet");
                        changed = true; // container not realized yet; try again next pass
                    }
                }
            }

            Sync(ViewModel.RootNodes);
            Trace($"sync pass done, changed={changed}, remaining={remainingPasses}");
            if (changed && remainingPasses > 0)
            {
                // Containers realize lazily across layout passes; space retries out in time
                // rather than burning them all within the same tick.
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(50);
                timer.IsRepeating = false;
                timer.Tick += (_, _) => ScheduleExpansionSync(remainingPasses - 1);
                timer.Start();
            }
        });
    }

    /// <summary>
    /// Applies expansion state and replaces WinUI's generous per-level indentation with a
    /// compact equivalent. Binding the scaled padding keeps it correct when drag-and-drop
    /// changes a realized node's depth.
    /// </summary>
    private void TreeViewItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item)
            return;

        if (FindTemplateElement(item, "MultiSelectGrid") is Grid row)
        {
            row.SetBinding(Grid.PaddingProperty, new Microsoft.UI.Xaml.Data.Binding
            {
                Source = item,
                Path = new PropertyPath("TreeViewItemTemplateSettings.Indentation"),
                Converter = CompactTreeIndentationConverter.Instance,
                Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay,
            });
        }

        if (item.DataContext is TreeNodeViewModel { IsFolder: true } node
            && item.IsExpanded != node.IsExpanded)
        {
            Trace($"realized: '{node.FolderPath}' container={item.IsExpanded} vm={node.IsExpanded} -> pushing");
            item.IsExpanded = node.IsExpanded;
        }
    }

    private static TreeNodeViewModel? NodeOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as TreeNodeViewModel;

    // ---- Filter ----

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearFilterButton.Visibility = string.IsNullOrEmpty(FilterBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        _filterDebounce ??= CreateFilterDebounce();
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void FilterBox_Loaded(object sender, RoutedEventArgs e)
    {
        // The stock TextBox template shows its own clear button while focused.
        // This field has a persistent clear button, so remove the template button
        // to avoid two slightly offset X glyphs occupying the same space.
        if (FindTemplateElement(FilterBox, "DeleteButton") is Button deleteButton)
        {
            deleteButton.Opacity = 0;
            deleteButton.IsHitTestVisible = false;
            deleteButton.Width = 0;
            deleteButton.MinWidth = 0;
        }
    }

    private static FrameworkElement? FindTemplateElement(DependencyObject root, string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement { Name: var childName } element && childName == name)
                return element;

            if (FindTemplateElement(child, name) is { } descendant)
                return descendant;
        }

        return null;
    }

    private sealed class CompactTreeIndentationConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public static CompactTreeIndentationConverter Instance { get; } = new();

        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is Thickness indentation
                ? new Thickness(
                    indentation.Left * TreeIndentationScale,
                    indentation.Top,
                    indentation.Right,
                    indentation.Bottom)
                : value;

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    private void ClearFilterButton_Click(object sender, RoutedEventArgs e) => ClearFilter();

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateFilterDebounce()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => ApplyFilterNow();
        return timer;
    }

    private void ApplyFilterNow()
    {
        _filterDebounce?.Stop();
        ViewModel.SearchText = FilterBox.Text;
    }

    private void ClearFilter()
    {
        FilterBox.Text = "";
        ApplyFilterNow();
        FilterBox.Focus(FocusState.Programmatic);
    }

    private void FilterBox_GotFocus(object sender, RoutedEventArgs e) =>
        FilterFieldBorder.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SessionSplitterHoverBrush"];

    private void FilterBox_LostFocus(object sender, RoutedEventArgs e) =>
        FilterFieldBorder.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(_themePalette.Divider);

    private void FilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            // Filtering narrows the view only; Enter must never launch a session.
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            if (FilterBox.Text.Length > 0)
                ClearFilter();
            else
                FocusFirstTreeItem();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Down)
        {
            FocusFirstTreeItem();
            e.Handled = true;
        }
    }

    private void FocusFirstTreeItem()
    {
        if (ViewModel.RootNodes.FirstOrDefault() is { } first
            && SessionTree.ContainerFromItem(first) is TreeViewItem item)
            item.Focus(FocusState.Keyboard);
        else
            SessionTree.Focus(FocusState.Keyboard);
    }

    // ---- Tree selection (Explorer-style: click, Ctrl+click toggle, Shift+click range) ----

    private OrderedSelection<TreeNodeViewModel> TreeSelection => ViewModel.TreeSelection;
    private IReadOnlyList<Session> _dragSessions = [];

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void ClearSelection() => TreeSelection.Clear();

    private void SelectOnly(TreeNodeViewModel node) => TreeSelection.SelectOnly(node);

    private void ToggleSelection(TreeNodeViewModel node) => TreeSelection.Toggle(node);

    /// <summary>Range select over the flattened visible tree, from the anchor to the clicked node.</summary>
    private void SelectRangeTo(TreeNodeViewModel node) =>
        TreeSelection.SelectRangeTo(node, ViewModel.VisibleNodes().ToList());

    /// <summary>True when the tap landed on the expand/collapse chevron rather than the row content.</summary>
    private static bool IsChevronHit(object originalSource)
    {
        var current = originalSource as DependencyObject;
        while (current is not null and not TreeViewItem)
        {
            if (current is FrameworkElement { Name: "ExpandCollapseChevron" })
                return true;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void TreeNode_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { FocusState: FocusState.Keyboard } item
            || !ReferenceEquals(item, FocusManager.GetFocusedElement(Root.XamlRoot))
            || NodeOf(item) is not { } node)
        {
            return;
        }

        var extendRange = IsKeyDown(VirtualKey.Shift);
        TreeSelection.SelectForKeyboardFocus(
            node,
            ViewModel.VisibleNodes().ToList(),
            extendRange,
            preserveSelection: IsKeyDown(VirtualKey.Control) && !extendRange);
    }

    private void TreeNode_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node || IsChevronHit(e.OriginalSource))
            return;
        // SelectionMode=None leaves focus management to us too. Without this, the clicked
        // row paints as selected but Enter is dispatched to whichever control held focus.
        if (sender is TreeViewItem item)
            item.Focus(FocusState.Pointer);
        e.Handled = true; // taps bubble to ancestor folder items; only the innermost row counts
        if (IsKeyDown(VirtualKey.Shift))
            SelectRangeTo(node);
        else if (IsKeyDown(VirtualKey.Control))
            ToggleSelection(node);
        else
            SelectOnly(node);
    }

    private void SessionRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
            node.IsPointerOver = true;
    }

    private void SessionRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
            node.IsPointerOver = false;
    }

    private void SessionNode_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (NodeOf(sender)?.Session is { } session)
        {
            ConnectSession(session);
            e.Handled = true;
        }
    }

    private void SessionTree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TreeSelection.Items.Count == 0
            || KeyBindings.Find((int)e.Key, AppShortcuts.CurrentModifiers(), ShortcutScope.SessionTree)
                is not var (binding, _))
        {
            return;
        }

        IRelayCommand? command = binding.Id switch
        {
            ShortcutIds.OpenSelection => ViewModel.OpenSelectionCommand,
            ShortcutIds.EditSelection => ViewModel.EditSelectionCommand,   // F2: rename a folder or edit a session
            ShortcutIds.DeleteSelection => ViewModel.DeleteSelectionCommand, // each path confirms first
            _ => null,
        };
        var selection = TreeSelection.Items.ToList();
        if (command?.CanExecute(selection) != true)
            return;
        command.Execute(selection);
        e.Handled = true;
    }

    // ---- Tree context menu (built per selection: session, folder, or multi) ----

    private void TreeNode_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (NodeOf(sender) is not { } node)
            return;
        args.Handled = true; // keep the TreeView's background flyout from also opening
        if (!node.IsSelected)
            SelectOnly(node); // right-click outside the selection retargets it, as in Explorer
        if (BuildSelectionMenu() is not { } menu)
            return;
        if (args.TryGetPosition(sender, out var point))
            menu.ShowAt(sender, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        else
            menu.ShowAt((FrameworkElement)sender);
    }

    private MenuFlyout? BuildSelectionMenu()
    {
        var entries = ViewModel.BuildSelectionMenu();
        if (entries.Count == 0)
            return null;
        var menu = new MenuFlyout();
        foreach (var entry in entries)
        {
            menu.Items.Add(entry.IsSeparator
                ? new MenuFlyoutSeparator()
                : new MenuFlyoutItem { Text = entry.Text, Command = entry.Command, CommandParameter = entry.Parameter });
        }
        return menu;
    }

    private void ConnectInNewWindow(IReadOnlyList<Session> sessions)
    {
        if (sessions.Count == 0)
            return;
        var window = App.OpenNewWindow();
        foreach (var session in sessions)
            window.ConnectSession(session);
    }

    // ---- Tree expansion bookkeeping ----

    private void SessionTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is TreeNodeViewModel node)
        {
            Trace($"Expanding '{node.FolderPath}'");
            ViewModel.NoteExpansion(node, expanded: true);
        }
    }

    private void SessionTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (args.Item is TreeNodeViewModel node)
        {
            Trace($"Collapsed '{node.FolderPath}'");
            ViewModel.NoteExpansion(node, expanded: false);
        }
    }

    // ---- Drag and drop ----

    private void SessionTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        _dragSessions = [];
        if (args.Items.OfType<TreeNodeViewModel>().FirstOrDefault() is not { } draggedNode)
        {
            args.Cancel = true;
            return;
        }

        var draggedSelection = TreeSelection.BeginDrag(draggedNode);
        // Folders are deliberately immovable. A mixed custom selection is one drag unit,
        // so do not silently move only its session leaves.
        if (draggedSelection.Any(node => node.IsFolder))
        {
            args.Cancel = true;
            return;
        }

        _dragSessions = draggedSelection
            .Select(node => node.Session)
            .OfType<Session>()
            .ToList();
        if (_dragSessions.Count == 0)
            args.Cancel = true;
    }

    private void SessionTree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        var draggedSessions = _dragSessions;
        _dragSessions = [];

        // Dropping onto a session targets that session's folder; onto nothing targets the
        // SSH root. Local and SSH are separate scopes: cross-boundary drops are discarded.
        var (targetFolder, targetKind) = args.NewParentItem switch
        {
            TreeNodeViewModel { IsFolder: true } folder =>
                (folder.FolderPath, folder.IsLocalScope ? SessionKind.Local : SessionKind.Ssh),
            TreeNodeViewModel sessionNode =>
                (sessionNode.FolderPath, sessionNode.IsLocalScope ? SessionKind.Local : SessionKind.Ssh),
            _ => ("", SessionKind.Ssh),
        };
        var plan = SessionDropPlanner.Plan(
            args.DropResult == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move,
            draggedSessions,
            targetFolder,
            targetKind);
        if (!plan.Accepted)
            return;

        // WinUI has updated its transient hierarchy when it raises this event, but the
        // underlying drag operation is still unwinding. Replacing RootNodes synchronously
        // here lets that remaining work mutate the replacement tree. Persist after the event
        // returns, then rebuild once from the store so the visual and saved trees agree.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (plan.SessionIds.Count > 0)
                    ViewModel.MoveSessionsToFolder(plan.SessionIds, targetFolder);
                else
                    ViewModel.RebuildTree();
            });
    }
}
