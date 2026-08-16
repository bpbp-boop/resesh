using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sessions.Core.Ssh;

namespace Sessions.App.Dialogs;

/// <summary>Small code-built dialogs used during the connect workflow.</summary>
public static class ConnectDialogs
{
    private sealed record TmuxChoice(string Label, int Slot);

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
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? (passwordBox.Password, saveCheck.IsChecked == true)
            : null;
    }

    /// <summary>Lets the user choose an existing persistent shell or start a new one.</summary>
    public static async Task<int?> SelectTmuxSessionAsync(
        XamlRoot xamlRoot, IReadOnlyList<TmuxSessionInfo> sessions, int newSlot)
    {
        var choices = sessions.Select(session => new TmuxChoice(
                $"{SlotLabel(session.Slot)} — {PathLabel(session.CurrentPath)} — {AttachmentLabel(session.AttachedClients)}",
                session.Slot))
            .Append(new TmuxChoice($"Start a new persistent session ({SlotLabel(newSlot)})", newSlot))
            .ToList();
        var picker = new ComboBox
        {
            Header = "Persistent session",
            ItemsSource = choices,
            DisplayMemberPath = nameof(TmuxChoice.Label),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 460,
            Children =
            {
                new TextBlock
                {
                    Text = "More than one saved persistent session is available for this connection. Select the shell to resume, or start a new shell.",
                    TextWrapping = TextWrapping.Wrap,
                },
                picker,
            },
        };
        var dialog = new ContentDialog
        {
            Title = "Select Persistent Session",
            Content = content,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            && picker.SelectedItem is TmuxChoice choice
                ? choice.Slot
                : null;
    }

    private static string SlotLabel(int slot) => slot == 0 ? "Primary" : $"Session {slot + 1}";

    private static string PathLabel(string path) =>
        string.IsNullOrWhiteSpace(path) ? "path unavailable" : path;

    private static string AttachmentLabel(int count) => count switch
    {
        0 => "detached",
        1 => "attached by 1 client",
        _ => $"attached by {count} clients",
    };

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
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
