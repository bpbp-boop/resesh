using Resesh.App.ViewModels;
using Resesh.Core.Agents;
using Resesh.Core.Backend;
using Resesh.Core.Models;

namespace Resesh.AppLogic.Tests;

public sealed class CommandCompletionTests
{
    private static TabViewModel Tab() => new(new Session(), new ViewModelEnvironment
    { CurrentTheme=()=>"dark", ResolveTheme=t=>t, ShowAgentIcons=()=>true, IsSessionVisible=_=>true, ApplySessionSettings=_=>{}, ReportError=_=>{} });

    [Fact]
    public void NotificationIsExplicitPerExecutionAndKeepsReadableLastResult()
    {
        var tab=Tab(); var results=new List<CommandCompletion>(); tab.CompletionRequested+=results.Add;
        tab.ObserveCommandExecution(1,"true",false,null); Assert.False(tab.CanNotifyCommandCompletion);
        tab.State=TabConnectionState.Connected;
        tab.ObserveCommandExecution(1,"python3 private.py",false,null);
        Assert.True(tab.ToggleCompletionNotificationCommand.CanExecute(null));
        tab.ToggleCompletionNotificationCommand.Execute(null);
        Assert.True(tab.IsCompletionNotificationArmed);
        tab.ObserveCommandExecution(2,"",true,0); Assert.Empty(results);
        tab.ObserveCommandExecution(1,"",true,1);
        Assert.Equal("python3",Assert.Single(results).ProgramName);
        Assert.Contains("exit 1",tab.CompletionNotificationTooltip);
        Assert.DoesNotContain("private",tab.CompletionNotificationTooltip);
        Assert.False(tab.CanNotifyCommandCompletion);
        tab.ObserveCommandExecution(1,"",true,1); Assert.Single(results);
    }

    [Fact]
    public void NotificationButtonIsHiddenWhileAnAgentIsDetected()
    {
        var tab = Tab();
        tab.State = TabConnectionState.Connected;
        tab.ObserveCommandExecution(1, "python3 private.py", false, null);
        Assert.True(tab.ShowCompletionNotificationButton);

        var changes = new List<string?>();
        tab.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        tab.Agent = new AgentSnapshot("codex", AgentAttention.Working, null, AgentSource.Command);

        Assert.False(tab.ShowCompletionNotificationButton);
        Assert.Contains(nameof(TabViewModel.ShowCompletionNotificationButton), changes);
    }

    [Fact]
    public void DisconnectCancelsAndLateCompletionCannotNotify()
    {
        var tab=Tab(); var results=new List<CommandCompletion>(); tab.CompletionRequested+=results.Add;
        tab.State=TabConnectionState.Connected; tab.ObserveCommandExecution(1,"sleep 5",false,null);
        tab.ToggleCompletionNotificationCommand.Execute(null); tab.State=TabConnectionState.Disconnected;
        tab.ObserveCommandExecution(1,"",true,0); Assert.Empty(results);
        Assert.False(tab.IsCompletionNotificationArmed);
    }
}
