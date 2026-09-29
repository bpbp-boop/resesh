using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Ssh;

namespace Resesh.App.Dialogs;

/// <summary>One keyboard-interactive challenge and the user's explicit answer to it.</summary>
public sealed class ChallengeResponse(KeyboardInteractivePrompt prompt)
{
    public string Prompt { get; } = prompt.Text;
    public bool IsSecret { get; } = prompt.IsSecret;
    public bool IsText => !IsSecret;
    public string Response { get; set; } = "";
}

/// <summary>Shows each keyboard-interactive challenge of one authentication round.</summary>
public sealed partial class KeyboardInteractiveDialog : ContentDialog
{
    public IReadOnlyList<ChallengeResponse> Challenges { get; }

    public KeyboardInteractiveDialog(string title, IReadOnlyList<KeyboardInteractivePrompt> prompts)
    {
        Challenges = prompts.Select(prompt => new ChallengeResponse(prompt)).ToList();
        InitializeComponent();
        Title = title;
    }

    /// <summary>The answers in challenge order, read after the dialog closes.</summary>
    public IReadOnlyList<string> Responses => Challenges.Select(challenge => challenge.Response).ToList();
}
