using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Resesh.App.ViewModels;
using Windows.Foundation;

namespace Resesh.App;

// The workspace hover card: a rail button's name, state, background alert and shortcut,
// beside the button the moment the pointer or keyboard focus reaches it. A tooltip waits
// before it opens, which is slow for scanning a column of icons. The card follows the item
// while it is shown, so a switch or an alert updates it in place.
public sealed partial class MainWindow
{
    private const double HoverCardGap = 6;

    private WorkspaceRailItemViewModel? _hoverCardItem;
    private FrameworkElement? _hoverCardAnchor;

    private void WorkspaceRailItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Touch has no hover; a press would leave the card up.
        if (e.Pointer.PointerDeviceType is PointerDeviceType.Touch)
            return;
        if (sender is FrameworkElement { DataContext: WorkspaceRailItemViewModel item } anchor)
            ShowWorkspaceHoverCard(anchor, item);
    }

    private void WorkspaceRailItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _hoverCardAnchor))
            HideWorkspaceHoverCard();
    }

    private void WorkspaceRail_GotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ListViewItem { FocusState: FocusState.Keyboard, Content: WorkspaceRailItemViewModel item } container)
            ShowWorkspaceHoverCard(container, item);
    }

    private void WorkspaceRail_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, _hoverCardAnchor))
            HideWorkspaceHoverCard();
    }

    private void WorkspaceRail_DragItemsStarting(object sender, DragItemsStartingEventArgs e) => HideWorkspaceHoverCard();

    private void ShowWorkspaceHoverCard(FrameworkElement anchor, WorkspaceRailItemViewModel item)
    {
        if (!ReferenceEquals(item, _hoverCardItem))
        {
            if (_hoverCardItem is not null)
                _hoverCardItem.PropertyChanged -= HoverCardItem_PropertyChanged;
            item.PropertyChanged += HoverCardItem_PropertyChanged;
            _hoverCardItem = item;
        }
        _hoverCardAnchor = anchor;
        FillWorkspaceHoverCard(item);
        PlaceWorkspaceHoverCard();
        WorkspaceHoverCard.IsOpen = true;
    }

    private void HideWorkspaceHoverCard()
    {
        WorkspaceHoverCard.IsOpen = false;
        if (_hoverCardItem is not null)
            _hoverCardItem.PropertyChanged -= HoverCardItem_PropertyChanged;
        _hoverCardItem = null;
        _hoverCardAnchor = null;
    }

    private void HoverCardItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WorkspaceRailItemViewModel item || !ReferenceEquals(item, _hoverCardItem))
            return;
        FillWorkspaceHoverCard(item);
        PlaceWorkspaceHoverCard(); // an alert line changes the card's height
    }

    private void FillWorkspaceHoverCard(WorkspaceRailItemViewModel item)
    {
        HoverCardName.Text = item.Name;
        HoverCardStatus.Text = item.StatusText;
        HoverCardShortcut.Text = item.ShortcutText;
        HoverCardAlert.Visibility = PresentationValues.Visible(item.HasAlert);
        HoverCardAlertText.Text = item.AlertText;
        HoverCardAlertDot.Fill = new SolidColorBrush(PresentationValues.WorkspaceAlertColor(item.Alert));
    }

    /// <summary>Beside the rail, centred on the button and kept inside the window.</summary>
    private void PlaceWorkspaceHoverCard()
    {
        if (_hoverCardAnchor is not { IsLoaded: true } anchor)
            return;
        Point origin;
        try
        {
            origin = anchor.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        }
        catch (ArgumentException)
        {
            return; // left the tree (recycled while the card was up)
        }
        WorkspaceHoverCardBody.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = WorkspaceHoverCardBody.DesiredSize;
        var top = origin.Y + (anchor.ActualHeight - size.Height) / 2;
        WorkspaceHoverCard.HorizontalOffset = origin.X + anchor.ActualWidth + HoverCardGap;
        WorkspaceHoverCard.VerticalOffset = Math.Clamp(top, HoverCardGap, Math.Max(HoverCardGap, Root.ActualHeight - size.Height - HoverCardGap));
    }
}
