using System.Text.Json;
using Sessions.Core.Agents;

namespace Sessions.Core.Tests;

public class AgentAdaptersTests
{
    [Fact]
    public void CodexAdapterUsesOfficialLifecycleEventsForPreciseStates()
    {
        var adapter = AgentAdapters.Codex();
        using var document = JsonDocument.Parse(adapter.Text);
        var hooks = document.RootElement.GetProperty("hooks");

        AssertState(hooks, "SessionStart", "idle");
        AssertState(hooks, "UserPromptSubmit", "working");
        AssertState(hooks, "PermissionRequest", "needs-approval");
        AssertState(hooks, "PostToolUse", "working");
        AssertState(hooks, "Stop", "complete");
        AssertState(hooks, "SessionEnd", "exit");
    }

    [Fact]
    public void CodexAdapterReportsOnlyAndNeverMakesAnApprovalDecision()
    {
        var adapter = AgentAdapters.Codex();

        Assert.Contains("/dev/tty", adapter.Text, StringComparison.Ordinal);
        Assert.Contains("CONOUT$", adapter.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("permissionDecision", adapter.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"behavior\"", adapter.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_input", adapter.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodexAdapterIsTheFirstRecommendedAdapter()
    {
        Assert.StartsWith("Codex", AgentAdapters.All[0].Title, StringComparison.Ordinal);
    }

    private static void AssertState(JsonElement hooks, string eventName, string state)
    {
        var handler = hooks.GetProperty(eventName)[0].GetProperty("hooks")[0];
        var posix = handler.GetProperty("command").GetString();
        var windows = handler.GetProperty("commandWindows").GetString();

        Assert.Contains($"id=codex;state={state}", posix, StringComparison.Ordinal);
        Assert.Contains($"id=codex;state={state}", windows, StringComparison.Ordinal);
        Assert.Equal(3, handler.GetProperty("timeout").GetInt32());
    }
}
