using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Ssh;
using System.Runtime.CompilerServices;

namespace Resesh.App.Dialogs;

/// <summary>The user's answer to the persistent-session picker.</summary>
public abstract record TmuxChoice
{
    private TmuxChoice() { }
    public sealed record Resume(int Slot) : TmuxChoice;
    public sealed record StartNew : TmuxChoice;
    public sealed record EndDetachedAndStartNew : TmuxChoice;
}

/// <summary>Small code-built dialogs used during the connect workflow.</summary>
public static class ConnectDialogs
{
    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> DialogGates = new();

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        var xamlRoot = dialog.XamlRoot
            ?? throw new InvalidOperationException("Connect dialogs require a XamlRoot.");
        var dialogGate = DialogGates.GetValue(xamlRoot, _ => new SemaphoreSlim(1, 1));
        await dialogGate.WaitAsync();
        try
        {
            return await dialog.ShowModalAsync();
        }
        finally
        {
            dialogGate.Release();
        }
    }

    /// <summary>Prompts for a password/passphrase. Returns (secret, save) or null on cancel.</summary>
    public static async Task<(string Secret, bool Save)?> PromptCredentialAsync(
        XamlRoot xamlRoot, string title, string prompt)
    {
        var passwordBox = new PasswordBox { Header = prompt };
        var saveCheck = new CheckBox { Content = "Save in Windows Credential Manager", IsChecked = true };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel { Spacing = 12, MinWidth = 360, Children = { passwordBox, saveCheck } },
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary
            ? (passwordBox.Password, saveCheck.IsChecked == true)
            : null;
    }

    /// <summary>Shows each keyboard-interactive challenge and returns its explicit response.</summary>
    public static async Task<IReadOnlyList<string>?> PromptKeyboardInteractiveAsync(
        XamlRoot xamlRoot,
        string title,
        IReadOnlyList<KeyboardInteractivePrompt> prompts)
    {
        var inputs = new List<Control>();
        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        foreach (var prompt in prompts)
        {
            Control input = prompt.IsSecret
                ? new PasswordBox { Header = prompt.Text }
                : new TextBox { Header = prompt.Text };
            inputs.Add(input);
            panel.Children.Add(input);
        }
        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary)
            return null;
        return inputs.Select(input => input switch
        {
            PasswordBox password => password.Password,
            TextBox text => text.Text,
            _ => "",
        }).ToList();
    }

    /// <summary>Warns when a registered key path now contains a different public key.</summary>
    public static async Task<bool> ConfirmChangedPrivateKeyAsync(
        XamlRoot xamlRoot, SshKeyChangedException change)
    {
        var confirm = new TextBox
        {
            Header = $"Type the key name ({change.KeyName}) to accept the replacement",
            PlaceholderText = change.KeyName,
        };
        var dialog = new ContentDialog
        {
            Title = "SSH Key Has Changed",
            Content = new StackPanel
            {
                Spacing = 8,
                MinWidth = 460,
                Children =
                {
                    new TextBlock
                    {
                        Text = "The file at the registered path now contains a different public key. "
                            + "Only continue if you expected this key rotation.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock { Text = $"Previous: {change.PreviousFingerprint}", IsTextSelectionEnabled = true },
                    new TextBlock { Text = $"Current:  {change.CurrentFingerprint}", IsTextSelectionEnabled = true },
                    confirm,
                },
            },
            PrimaryButtonText = "Accept New Key",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = xamlRoot,
        };
        confirm.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
            confirm.Text.Trim().Equals(change.KeyName, StringComparison.Ordinal);
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Lets the user resume one of the connection's running shells, start another, or clear
    /// out the detached ones and start fresh. Returns null on cancel. Double-click resumes
    /// (or starts) the clicked row.
    /// </summary>
    public static async Task<TmuxChoice?> SelectTmuxSessionAsync(
        XamlRoot xamlRoot, string profileName, IReadOnlyList<TmuxSessionInfo> sessions, int newSlot)
    {
        var detached = sessions.Count(session => session.AttachedClients == 0);
        var picker = new ListView
        {
            MaxHeight = 360,
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "Persistent session");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(picker, "ConnectRemoteSessionsList");
        foreach (var session in sessions)
            picker.Items.Add(TmuxSessionRow.Create(session));
        picker.Items.Add(TmuxSessionRow.CreateNew(newSlot, "Start a new session"));
        picker.SelectedIndex = 0;

        var summary = detached == sessions.Count
            ? $"{sessions.Count} earlier shells are still running on this host."
            : detached == 0
                ? $"{sessions.Count} shells are running and attached somewhere else."
                : $"{sessions.Count} shells are running: {detached} detached, {sessions.Count - detached} attached somewhere else.";
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = summary + " Resume one, or start a new session.",
                    TextWrapping = TextWrapping.Wrap,
                },
                picker,
            },
        };
        // ContentDialog footer buttons share equal widths, so the long bulk action lives in
        // the content where it can size to its label. Attached shells are someone's live
        // terminal, so it never touches them.
        var endDetached = new Button
        {
            Content = detached == 1 ? "End the detached shell and start new" : $"End {detached} detached shells and start new",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = detached == 0 ? Visibility.Collapsed : Visibility.Visible,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(endDetached, "EndDetachedAndStartNew");
        content.Children.Add(endDetached);
        content.Children.Add(new TextBlock
        {
            Text = "To skip this question, change “When earlier shells are running” in Session Settings › Terminal.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SessionTreeMutedForegroundBrush"],
        });
        var dialog = new ContentDialog
        {
            Title = $"Resume a Session — {profileName}",
            Content = content,
            PrimaryButtonText = "Resume",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        TmuxSessionRow.SizeDialog(dialog, content, xamlRoot);
        picker.SelectionChanged += (_, _) => dialog.PrimaryButtonText =
            (picker.SelectedItem as ListViewItem)?.Tag is TmuxSessionInfo ? "Resume" : "Start New";
        var doubleTapped = false;
        picker.DoubleTapped += (_, _) =>
        {
            doubleTapped = true;
            dialog.Hide();
        };
        var endRequested = false;
        endDetached.Click += (_, _) =>
        {
            endRequested = true;
            dialog.Hide();
        };

        var result = await ShowAsync(dialog);
        if (endRequested)
            return new TmuxChoice.EndDetachedAndStartNew();
        if (result != ContentDialogResult.Primary && !doubleTapped)
            return null;
        return (picker.SelectedItem as ListViewItem)?.Tag switch
        {
            TmuxSessionInfo session => new TmuxChoice.Resume(session.Slot),
            int => new TmuxChoice.StartNew(),
            _ => null,
        };
    }

    /// <summary>Host key confirmation: first connect, or a changed key (typed confirmation required).</summary>
    public static Task<bool> ConfirmHostKeyAsync(XamlRoot xamlRoot, HostKeyInfo info) =>
        info.Verdict == HostKeyVerdict.Mismatch
            ? ConfirmChangedHostKeyAsync(xamlRoot, info)
            : ConfirmFirstHostKeyAsync(xamlRoot, info);

    private static async Task<bool> ConfirmFirstHostKeyAsync(XamlRoot xamlRoot, HostKeyInfo info)
    {
        var dialog = new ContentDialog
        {
            Title = "Verify Host Key",
            Content = new StackPanel
            {
                Spacing = 8,
                MinWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"First connection to {info.Host}:{info.Port}. Verify the host key fingerprint before trusting it.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock { Text = $"Key type: {info.KeyType}", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") },
                    new TextBlock
                    {
                        Text = $"SHA256:{info.Sha256Fingerprint}",
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                },
            },
            PrimaryButtonText = "Accept and Connect",
            CloseButtonText = "Reject",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private static async Task<bool> ConfirmChangedHostKeyAsync(XamlRoot xamlRoot, HostKeyInfo info)
    {
        static TextBlock Mono(string text) => new()
        {
            Text = text,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        var confirmBox = new TextBox
        {
            Header = $"Type the host name ({info.Host}) to confirm replacing the trusted key",
            PlaceholderText = info.Host,
        };
        var dialog = new ContentDialog
        {
            Title = "⚠ Host Key Has Changed",
            Content = new StackPanel
            {
                Spacing = 8,
                MinWidth = 460,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"The host key for {info.Host}:{info.Port} does not match the one previously trusted. " +
                               "This can mean the server was reinstalled or its key rotated — but it can also mean " +
                               "a man-in-the-middle attack. Only continue if you can explain the change.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock { Text = "Previously trusted:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    Mono(info.Previous is { } prev
                        ? $"{prev.KeyType} SHA256:{prev.Sha256}"
                        : "(unavailable)"),
                    new TextBlock { Text = "Offered now:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    Mono($"{info.KeyType} SHA256:{info.Sha256Fingerprint}"),
                    confirmBox,
                },
            },
            PrimaryButtonText = "Replace Key and Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = xamlRoot,
        };
        confirmBox.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled =
                string.Equals(confirmBox.Text.Trim(), info.Host, StringComparison.OrdinalIgnoreCase);
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
