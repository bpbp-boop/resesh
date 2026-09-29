using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Agents;
using Windows.ApplicationModel.DataTransfer;

namespace Resesh.App.Controls;

/// <summary>One adapter snippet as the Agents section lists it.</summary>
public sealed class AgentAdapterItem(AgentAdapterSnippet snippet, int index)
{
    public string Title => snippet.Title;
    public string Target => snippet.Target;
    public string Description => snippet.Description;
    public string Text => snippet.Text;
    public string AutomationId => $"SettingsAgentAdapter_{index}";
    public string CopyAutomationId => $"SettingsAgentAdapterCopy_{index}";
}

/// <summary>
/// The opt-in adapter snippets, in the Settings page's Agents section. resesh deliberately
/// installs nothing: the exact text is shown here, the user copies it to a target they
/// choose, and removing it is deleting the lines again. An adapter's only power is to emit
/// one escape sequence describing what the agent is doing — it can never send input or
/// approve anything.
/// </summary>
public sealed partial class AgentAdaptersView : UserControl
{
    public IReadOnlyList<AgentAdapterItem> Adapters { get; } =
        AgentAdapters.All.Select((snippet, index) => new AgentAdapterItem(snippet, index)).ToList();

    public string ProtocolReference => AgentAdapters.SequenceReference;

    public AgentAdaptersView() => InitializeComponent();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AgentAdapterItem adapter } button)
            return;
        var package = new DataPackage();
        package.SetText(adapter.Text);
        Clipboard.SetContent(package);
        button.Content = "Copied";
    }
}
