using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Resesh.App.Controls;
using Resesh.App.Dialogs;
using Resesh.App.Terminal;
using Resesh.App.ViewModels;
using Resesh.Core.Backup;
using Resesh.Core.Input;
using Resesh.Core.Layout;
using Resesh.Core.Models;
using Resesh.Core.Storage;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.System;

namespace Resesh.App;

// Quick connect: the title-bar search box that matches saved sessions or connects ad hoc.
public sealed partial class MainWindow
{
    // ---- Quick connect ----

    private void QuickConnect_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        UpdateQuickConnectHint();
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        var text = sender.Text.Trim();
        var items = new List<QuickConnectSuggestion>();
        if (QuickConnectTarget.TryParse(text, Environment.UserName, out var adhoc))
        {
            items.Add(new QuickConnectSuggestion
            {
                Display = adhoc.IsTelnet
                    ? $"Connect to {adhoc.Host}:{adhoc.Port} over telnet"
                    : $"Connect to {adhoc.Username}@{adhoc.Host}" + (adhoc.Port != 22 ? $":{adhoc.Port}" : ""),
                Detail = adhoc.IsTelnet ? "new connection · unencrypted" : "new connection",
                Glyph = "\uE768",
                Session = adhoc,
            });
        }
        if (text.Length > 0)
        {
            items.AddRange(ViewModel.RankedMatches(text).Select(s => new QuickConnectSuggestion
            {
                Display = s.Name,
                Detail = s.IsLocal
                    ? s.Local?.Executable ?? "local shell"
                    : s.IsTelnet
                        ? $"telnet {s.Host}" + (s.Port != 23 ? $":{s.Port}" : "")
                        : $"{s.Username}@{s.Host}" + (s.Port != 22 ? $":{s.Port}" : ""),
                Glyph = s.IsLocal ? "\uE7F8" : "\uEDA2",
                Session = s,
            }));
        }
        sender.ItemsSource = items;
    }

    private void QuickConnect_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var target = (args.ChosenSuggestion as QuickConnectSuggestion)?.Session;
        if (target is null)
        {
            var text = args.QueryText.Trim();
            if (text.Length == 0)
                return;
            target = QuickConnectTarget.TryParse(text, Environment.UserName, out var adhoc)
                ? adhoc
                : ViewModel.RankedMatches(text).FirstOrDefault();
        }
        if (target is null)
            return;
        ConnectSession(target);
        sender.Text = "";
        sender.ItemsSource = null;
    }

    private void QuickConnect_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            QuickConnectBox.Text = "";
            QuickConnectBox.ItemsSource = null;
            e.Handled = true;
        }
    }

    private void QuickConnect_FocusChanged(object sender, RoutedEventArgs e)
    {
        UpdateQuickConnectHint();
        CollapseQuickConnectIfIdle();
    }

    private void UpdateQuickConnectHint()
    {
        QuickConnectBox.PlaceholderText = QuickConnectHost.ActualWidth < 360
            ? "user@host" : "ssh user@host, telnet host, or search…";
        QuickConnectHint.Visibility =
            QuickConnectHost.ActualWidth >= 440 &&
            QuickConnectBox.Text.Length == 0 && QuickConnectBox.FocusState == FocusState.Unfocused
                ? Visibility.Visible
                : Visibility.Collapsed;
    }
}
