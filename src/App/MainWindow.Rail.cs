using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Resesh.App.Dialogs;
using Resesh.App.Terminal;
using Resesh.App.ViewModels;
using Resesh.Core.Backup;
using Resesh.Core.Input;
using Resesh.Core.Layout;
using Resesh.Core.Models;
using Resesh.Core.Recording;
using Resesh.Core.Storage;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Resesh.App;

// The compact rail shown when the sessions pane is collapsed.
public sealed partial class MainWindow
{
    // ---- compact sessions rail ----

    private void InitializeSessionsRail()
    {
        var settings = App.Settings.Current;
        _sessionsPaneWidth = Math.Clamp(settings.TreePaneWidth ?? 280, 180, 800);
        _selectedRailTab = NormalizeRailTab(settings.SessionsRailTab);
        _sessionsPaneOpen = settings.SessionsPaneOpen;
        ApplySessionsRailLayout();
        ViewModel.RefreshRecentSessions();
        if (_sessionsPaneOpen && _selectedRailTab == "recordings")
            _ = ViewModel.RefreshRecordingsAsync();
    }

    /// <summary>Re-reads the Recordings list when it is on screen (a recording started or
    /// stopped); a hidden list is read when it is next shown.</summary>
    internal void RefreshRecordingsIfShown()
    {
        if (!_windowClosed && SessionsPaneShown && _selectedRailTab == "recordings")
            _ = ViewModel.RefreshRecordingsAsync();
    }

    private static string NormalizeRailTab(string? tab) => tab switch
    {
        "workspaces" => "workspaces",
        "recent" => "recent",
        "recordings" => "recordings",
        _ => "sessions",
    };

    private void ApplySessionsRailLayout()
    {
        var shown = SessionsPaneShown;
        var paneAction = shown ? "Hide sessions pane" : "Show sessions pane";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SessionsPaneToggleButton, paneAction);
        ToolTipService.SetToolTip(SessionsPaneToggleButton, paneAction);
        var visible = shown ? Visibility.Visible : Visibility.Collapsed;
        SessionsPane.Visibility = _selectedRailTab == "sessions" ? visible : Visibility.Collapsed;
        WorkspacesPane.Visibility = _selectedRailTab == "workspaces" ? visible : Visibility.Collapsed;
        RecentPane.Visibility = _selectedRailTab == "recent" ? visible : Visibility.Collapsed;
        RecordingsPane.Visibility = _selectedRailTab == "recordings" ? visible : Visibility.Collapsed;

        // A floating pane takes no column; it spans the tab area above the dismiss layer.
        var docked = shown && !_paneOverlay;
        TreeColumn.MinWidth = docked ? 180 : 0;
        TreeColumn.Width = new GridLength(docked ? _sessionsPaneWidth : 0);
        TreeSplitterColumn.Width = new GridLength(docked ? 1 : 0);
        TreeSplitter.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        TreeSplitterLine.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        PaneDismissLayer.Visibility = shown && _paneOverlay ? Visibility.Visible : Visibility.Collapsed;
        var overlayWidth = OverlayPaneWidth();
        foreach (var pane in SessionsPanes())
        {
            Grid.SetColumnSpan(pane, _paneOverlay ? 3 : 1);
            pane.Width = _paneOverlay ? overlayWidth : double.NaN;
            pane.HorizontalAlignment = _paneOverlay ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            Canvas.SetZIndex(pane, _paneOverlay ? 4 : 0);
            pane.Background = _paneOverlay ? (Brush)Application.Current.Resources["SessionShellBrush"] : null;
            pane.BorderBrush = _paneOverlay ? (Brush)Application.Current.Resources["SessionChromeFrameBrush"] : null;
            pane.BorderThickness = new Thickness(0, 0, _paneOverlay ? 1 : 0, 0);
        }

        SessionsRail.SelectedItem = _selectedRailTab switch
        {
            "workspaces" => WorkspacesRailItem,
            "recent" => RecentRailItem,
            "recordings" => RecordingsRailItem,
            _ => SessionsRailItem,
        };
        SessionsPaneMenuItem.IsChecked = shown;
    }

    private void SelectSessionsRailTab(string tab)
    {
        var normalized = NormalizeRailTab(tab);
        var tabChanged = normalized != _selectedRailTab;
        var wasOpen = SessionsPaneShown;
        if (wasOpen && !_paneOverlay && TreeWidthIsUserChosen)
            _sessionsPaneWidth = TreeColumn.ActualWidth;

        _selectedRailTab = normalized;
        // A floating pane is temporary; the saved docked state stays as it was.
        if (_paneOverlay)
            _overlayPaneShown = true;
        else
            _sessionsPaneOpen = true;
        ApplySessionsRailLayout();
        PersistSessionsRailState();

        if (!wasOpen)
            StartSessionsPaneAnimation(opening: true);
        else if (tabChanged)
            StartSessionsPaneAnimation(opening: true, distance: 24, animateChrome: false);

        if (_selectedRailTab == "recent")
            ViewModel.RefreshRecentSessions();
        else if (_selectedRailTab == "recordings")
            _ = ViewModel.RefreshRecordingsAsync();
    }

    private void SetSessionsPaneOpen(bool open)
    {
        if (SessionsPaneShown == open)
        {
            SessionsPaneMenuItem.IsChecked = open;
            return;
        }

        if (_paneOverlay)
        {
            _overlayPaneShown = open;
            // Let clicks reach the tabs while the pane slides away.
            if (!open)
                PaneDismissLayer.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (_sessionsPaneOpen && !open && TreeWidthIsUserChosen)
                _sessionsPaneWidth = TreeColumn.ActualWidth;
            _sessionsPaneOpen = open;
            PersistSessionsRailState();
        }

        if (open)
        {
            ApplySessionsRailLayout();
            StartSessionsPaneAnimation(opening: true);
            if (_selectedRailTab == "recordings")
                _ = ViewModel.RefreshRecordingsAsync();
        }
        else
        {
            SessionsPaneMenuItem.IsChecked = false;
            StartSessionsPaneAnimation(opening: false);
        }
    }

    private FrameworkElement SelectedSessionsPane() => _selectedRailTab switch
    {
        "workspaces" => WorkspacesPane,
        "recent" => RecentPane,
        "recordings" => RecordingsPane,
        _ => SessionsPane,
    };

    /// <summary>Stops the sessions pane animation, if one is running, and puts back every
    /// value it touches: the pane's opacity and slide, and the docked gutter's opacity, which
    /// an opening animation starts from 0 as a local value. Stopping the storyboard alone
    /// leaves those, so a pane-mode change mid-animation left the gutter invisible. Every
    /// stop goes through here.</summary>
    private void CancelSessionsPaneAnimation()
    {
        // A storyboard that was stopped must not run its completion if it still fires.
        _sessionsPaneAnimationVersion++;
        _sessionsPaneStoryboard?.Stop();
        _sessionsPaneStoryboard = null;
        if (_animatedSessionsPane is { } pane)
        {
            pane.Opacity = 1;
            if (pane.RenderTransform is TranslateTransform transform)
                transform.X = 0;
            _animatedSessionsPane = null;
        }
        TreeSplitter.Opacity = 1;
        TreeSplitterLine.Opacity = 1;
    }

    private void StartSessionsPaneAnimation(
        bool opening,
        double? distance = null,
        bool animateChrome = true)
    {
        CancelSessionsPaneAnimation();
        if (!AppMotion.Enabled)
        {
            // No slide: land where the animation's completion would.
            if (!opening && !SessionsPaneShown)
                ApplySessionsRailLayout();
            return;
        }
        var animationVersion = ++_sessionsPaneAnimationVersion;

        var pane = SelectedSessionsPane();
        var transform = pane.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            pane.RenderTransform = transform;
        }
        _animatedSessionsPane = pane;

        var travel = distance ?? Math.Min(_sessionsPaneWidth, 320);
        var duration = TimeSpan.FromMilliseconds(opening ? 200 : 170);
        var easing = new CubicEase
        {
            EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn,
        };

        // Keep the content column at an endpoint while the pane animates. Changing it every
        // frame forces live terminals to reflow repeatedly and makes their ruler jump.
        transform.X = opening ? -travel : 0;
        pane.Opacity = opening ? 0.7 : 1;
        if (animateChrome)
        {
            TreeSplitter.Opacity = opening ? 0 : 1;
            TreeSplitterLine.Opacity = opening ? 0 : 1;
        }

        var slide = new DoubleAnimation
        {
            From = transform.X,
            To = opening ? 0 : -travel,
            Duration = duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.X));

        var fade = new DoubleAnimation
        {
            From = pane.Opacity,
            To = opening ? 1 : 0.7,
            Duration = duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(fade, pane);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        if (animateChrome)
        {
            var chromeFade = new DoubleAnimation
            {
                From = TreeSplitter.Opacity,
                To = opening ? 1 : 0,
                Duration = duration,
                EasingFunction = easing,
            };
            Storyboard.SetTarget(chromeFade, TreeSplitter);
            Storyboard.SetTargetProperty(chromeFade, nameof(UIElement.Opacity));
            storyboard.Children.Add(chromeFade);

            var lineFade = new DoubleAnimation
            {
                From = TreeSplitterLine.Opacity,
                To = opening ? 1 : 0,
                Duration = duration,
                EasingFunction = easing,
            };
            Storyboard.SetTarget(lineFade, TreeSplitterLine);
            Storyboard.SetTargetProperty(lineFade, nameof(UIElement.Opacity));
            storyboard.Children.Add(lineFade);
        }

        storyboard.Completed += (_, _) =>
        {
            if (animationVersion != _sessionsPaneAnimationVersion)
                return;
            if (animateChrome && !_paneOverlay)
            {
                TreeColumn.Width = new GridLength(opening ? _sessionsPaneWidth : 0);
                TreeColumn.MinWidth = opening ? 180 : 0;
            }
            // Stopping drops the animated values; restore the local ones they would reveal.
            CancelSessionsPaneAnimation();
            if (!opening && !SessionsPaneShown)
                ApplySessionsRailLayout();
        };
        _sessionsPaneStoryboard = storyboard;
        storyboard.Begin();
    }

    private void PersistSessionsRailState() =>
        App.SaveSettings(App.Settings.Current with
        {
            TreePaneWidth = _sessionsPaneWidth,
            SessionsPaneOpen = _sessionsPaneOpen,
            SessionsRailTab = _selectedRailTab,
        });

    private void SessionsRail_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItemContainer?.Tag is string tab && tab != _selectedRailTab)
            SelectSessionsRailTab(tab);
    }

    /// <summary>The selected tool's icon raises no SelectionChanged, so it opens its pane
    /// here when the pane is hidden, and closes a floating pane like a toggle.</summary>
    private void SessionsRail_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs e)
    {
        if (e.InvokedItemContainer?.Tag is not string tab || tab != _selectedRailTab)
            return;
        if (!SessionsPaneShown)
            SelectSessionsRailTab(tab);
        else if (_paneOverlay)
            SetSessionsPaneOpen(false);
    }

    // ListView has no item-click command; each rail list forwards its click to one.

    private void RecentSessionList_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.ConnectRecentCommand.Execute(e.ClickedItem);

    private void RecordingList_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.OpenRecordingCommand.Execute(e.ClickedItem);
}
