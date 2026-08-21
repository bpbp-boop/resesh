using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Ssh;
using System.Runtime.CompilerServices;

namespace Resesh.App.Dialogs;

/// <summary>Small code-built dialogs used during the connect workflow.</summary>
public static class ConnectDialogs
{
    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> DialogGates = new();

    private sealed record TmuxChoice(string Label, int Slot);

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        var xamlRoot = dialog.XamlRoot
            ?? throw new InvalidOperationException("Connect dialogs require a XamlRoot.");
        var dialogGate = DialogGates.GetValue(xamlRoot, _ => new SemaphoreSlim(1, 1));
        await dialogGate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
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
        return await ShowAsync(dialog) == ContentDialogResult.Primary
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
