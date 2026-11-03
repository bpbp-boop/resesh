using Resesh.Core.Agents;

namespace Resesh.Core.Tests;

public class AgentOscTests
{
    [Fact]
    public void ParsesStructuredEvent()
    {
        var e = AgentOsc.Parse(AgentOsc.SessionsCode, "agent;id=claude;state=needs-approval;label=Run%20tests");
        Assert.NotNull(e);
        Assert.Equal("claude", e.Key);
        Assert.Equal(AgentAttention.NeedsApproval, e.Attention);
        Assert.Equal("Run tests", e.Label);
        Assert.Equal(AgentSource.Structured, e.Source);
        Assert.False(e.Ended);
    }

    [Theory]
    [InlineData("working", AgentAttention.Working)]
    [InlineData("busy", AgentAttention.Working)]
    [InlineData("permission", AgentAttention.NeedsApproval)]
    [InlineData("question", AgentAttention.NeedsAnswer)]
    [InlineData("done", AgentAttention.Complete)]
    [InlineData("error", AgentAttention.Failed)]
    [InlineData("something-new", AgentAttention.Idle)]  // unknown states are never an alert
    public void MapsStateAliases(string state, AgentAttention expected) =>
        Assert.Equal(expected, AgentOsc.ParseStructured($"agent;id=claude;state={state}")?.Attention);

    [Fact]
    public void StateOnlyEventKeepsIdentityDecisionToTheTracker()
    {
        var e = AgentOsc.ParseStructured("agent;state=working");
        Assert.NotNull(e);
        Assert.Null(e.Key);
    }

    [Fact]
    public void UnknownAgentIdBecomesTheGenericAgent() =>
        Assert.Equal(AgentIdentities.Generic, AgentOsc.ParseStructured("agent;id=some-new-cli;state=working")?.Key);

    [Fact]
    public void ExitIsReportedAsEnded()
    {
        var e = AgentOsc.ParseStructured("agent;id=claude;state=exit");
        Assert.NotNull(e);
        Assert.True(e.Ended);
        Assert.Equal(AgentAttention.None, e.Attention);
    }

    [Theory]
    [InlineData("")]
    [InlineData("notagent;id=claude")]
    [InlineData("agent")]
    [InlineData("agent;garbage")]
    public void IgnoresForeignOrEmptyPayloads(string payload) =>
        Assert.Null(AgentOsc.ParseStructured(payload));

    [Fact]
    public void GenericNotificationIsALowConfidenceSignal()
    {
        var e = AgentOsc.Parse(AgentOsc.NotifyCode, "Build finished");
        Assert.NotNull(e);
        Assert.Equal(AgentAttention.Signal, e.Attention);
        Assert.Null(e.Key);
        Assert.Equal("Build finished", e.Label);
    }

    [Theory]
    [InlineData("4;1;")]
    [InlineData("4;3;0")]
    [InlineData("4")]
    public void ConEmuProgressIsNotANotification(string payload) =>
        // PowerShell 7 emits OSC 9;4 for ordinary progress bars.
        Assert.Null(AgentOsc.Parse(AgentOsc.NotifyCode, payload));

    [Fact]
    public void ParsesRxvtNotify()
    {
        var e = AgentOsc.Parse(AgentOsc.RxvtNotifyCode, "notify;Agent;waiting for you");
        Assert.Equal(AgentAttention.Signal, e?.Attention);
        Assert.Equal("Agent — waiting for you", e?.Label);
        Assert.Null(AgentOsc.Parse(AgentOsc.RxvtNotifyCode, "something;else"));
    }

    [Theory]
    [InlineData("working", AgentAttention.Working)]
    [InlineData("needs-approval", AgentAttention.NeedsApproval)]
    [InlineData("needs-answer", AgentAttention.NeedsAnswer)]
    [InlineData("complete", AgentAttention.Complete)]
    [InlineData("failed", AgentAttention.Failed)]
    [InlineData("idle", AgentAttention.Idle)]
    public void ReservedNotifyTitleParsesStructuredStates(string state, AgentAttention expected)
    {
        var e = AgentOsc.Parse(777, $"notify;resesh-agent;id=cl%61ude;state={state};label=Run%20tests%3B%20caf%C3%A9");
        Assert.NotNull(e);
        Assert.Equal("claude", e.Key);
        Assert.Equal(expected, e.Attention);
        Assert.Equal("Run tests; café", e.Label);
        Assert.Equal(AgentSource.Structured, e.Source);
        Assert.False(e.Ended);
    }

    [Theory]
    [InlineData("id=claude;state=exit")]
    [InlineData("agent=claude;status=ended")]
    public void ReservedNotifyTitleReportsExit(string fields)
    {
        var e = AgentOsc.ParseRxvtNotify("notify;resesh-agent;" + fields);
        Assert.NotNull(e);
        Assert.Equal("claude", e.Key);
        Assert.True(e.Ended);
        Assert.Equal(AgentAttention.None, e.Attention);
        Assert.Equal(AgentSource.Structured, e.Source);
    }

    [Theory]
    [InlineData("Resesh-agent")]
    [InlineData("resesh-agent ")]
    [InlineData(" resesh-agent")]
    [InlineData("resesh-agent-extra")]
    [InlineData("resesh%2Dagent")]
    public void NotifyTitleLookalikesStayGeneric(string title)
    {
        var e = AgentOsc.ParseRxvtNotify($"notify;{title};id=claude;state=needs-approval");
        Assert.NotNull(e);
        Assert.Equal(AgentSource.Notification, e.Source);
        Assert.Equal(AgentAttention.Signal, e.Attention);
        Assert.Null(e.Key);
        Assert.False(e.Ended);
    }

    [Theory]
    [InlineData("notify;resesh-agent")]
    [InlineData("notify;resesh-agent;")]
    [InlineData("notify;resesh-agent;garbage")]
    public void MalformedReservedNotificationsAreIgnored(string payload) =>
        Assert.Null(AgentOsc.ParseRxvtNotify(payload));

    [Fact]
    public void ReservedNotificationUsesTheSameStructuredFieldRules()
    {
        const string fields = "status=question;message=Ask%00%1B%0Auser;unknown=ignored";
        Assert.Equal(AgentOsc.ParseStructured("agent;" + fields),
            AgentOsc.ParseRxvtNotify("notify;resesh-agent;" + fields));
    }

    [Fact]
    public void BellCarriesNoInformation()
    {
        var e = AgentOsc.Bell();
        Assert.Equal(AgentAttention.Signal, e.Attention);
        Assert.Null(e.Key);
        Assert.Null(e.Label);
    }

    [Fact]
    public void UnrelatedOscCodesAreIgnored() =>
        Assert.Null(AgentOsc.Parse(133, "A"));

    [Fact]
    public void LabelsFromTheWireAreSanitized()
    {
        // Terminal output is untrusted: control characters must never reach the UI.
        var raw = "hello\0\u001b\r\n  world";
        Assert.Equal("hello world", AgentText.Sanitize(raw, 80));
        Assert.Null(AgentText.Sanitize(" ", 80));
        Assert.Null(AgentText.Sanitize(null, 80));
        Assert.Equal("abcde…", AgentText.Sanitize("abcdefghij", 5));
    }

    [Theory]
    [InlineData("🚀 prod", "🚀 prod")]
    [InlineData("𠜎 build", "𠜎 build")]
    [InlineData("👨\u200D👩\u200D👧 family", "👨\u200D👩\u200D👧 family")]
    [InlineData("evil\u202Etxt.exe", "eviltxt.exe")] // bidi override
    [InlineData("x\uE000\U000F0000\U00100000y", "xy")] // private use, BMP and supplementary
    [InlineData("tag\U000E0041s", "tags")] // tag characters (Format)
    [InlineData("csi\u009B31m here", "csi 31m here")] // C1 control
    [InlineData("\u200Dleading", "leading")] // a joiner joining nothing
    public void SanitizeKeepsCharactersAndDropsInvisibleOnes(string raw, string expected) =>
        Assert.Equal(expected, AgentText.Sanitize(raw, 80));

    [Fact]
    public void SanitizeDropsLoneSurrogates() =>
        // Built at run time: attribute strings cannot hold a lone surrogate.
        Assert.Equal("abc", AgentText.Sanitize(new string(['a', '\uD800', 'b', '\uDC00', 'c']), 80));

    [Fact]
    public void SanitizeNeverCutsASurrogatePair() =>
        Assert.Equal("ab…", AgentText.Sanitize("ab🚀cd", 3));

    [Fact]
    public void StructuredLabelsAreTruncatedToTheDocumentedLimit()
    {
        var label = AgentOsc.ParseStructured("agent;id=claude;state=working;label=" + new string('x', 500))?.Label;
        Assert.NotNull(label);
        Assert.True(label.Length <= AgentOsc.MaxLabelLength + 1, $"label was {label.Length} chars");
    }
}
