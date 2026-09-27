using Resesh.App.Interop;

namespace Resesh.AppLogic.Tests;

public sealed class TaskbarRelaunchPlanTests
{
    private static string Of(int length) => new('a', length);

    [Fact]
    public void Short_command_and_icon_are_both_set()
    {
        var plan = TaskbarRelaunchPlan.For(@"""C:\Program Files\Resesh\Resesh.App.exe""", @"C:\Program Files\Resesh\Assets\app.ico,0");
        Assert.True(plan.SetRelaunchCommand);
        Assert.True(plan.SetRelaunchIcon);
    }

    [Fact]
    public void Command_at_the_shell_limit_is_set_and_one_past_it_is_skipped()
    {
        Assert.True(TaskbarRelaunchPlan.For(Of(255), "i,0").SetRelaunchCommand);
        var over = TaskbarRelaunchPlan.For(Of(256), "i,0");
        Assert.False(over.SetRelaunchCommand);
        Assert.False(over.SetRelaunchIcon); // an icon without a command means nothing
    }

    [Fact]
    public void Oversized_icon_is_skipped_without_losing_the_command()
    {
        var plan = TaskbarRelaunchPlan.For("resesh.exe", Of(256));
        Assert.True(plan.SetRelaunchCommand);
        Assert.False(plan.SetRelaunchIcon);
    }

    [Fact]
    public void The_crash_repro_command_is_skipped()
    {
        // Deep build output plus a --data-dir path: 271 chars, which crashed startup.
        var directory = @"C:\Users\Boden\AppData\Local\Temp\claude\C--Users-Boden-Sessions\147625c5-75e5-47ab-b969-2b940b9615a5\scratchpad";
        var command = $@"""{directory}\appbuild\Resesh.App.exe"" --data-dir ""{directory}\data""";
        Assert.False(TaskbarRelaunchPlan.For(command, "i,0").SetRelaunchCommand);
    }
}
