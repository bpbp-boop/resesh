using System.Text.RegularExpressions;
using Sessions.Core.Models;
using Sessions.Core.Storage;

namespace Sessions.Core.Tests;

public sealed class HighlightsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sessions-tests-" + Guid.NewGuid());
    private string StorePath => Path.Combine(_dir, "highlights.json");

    private HighlightsStore NewStore()
    {
        var store = new HighlightsStore(StorePath);
        store.Load();
        return store;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- defaults ----

    [Fact]
    public void FreshStore_ExposesBuiltinsWithDefaults()
    {
        var store = NewStore();
        Assert.Equal(BuiltinHighlights.Rules.Count, store.AllRules.Count);
        Assert.True(store.AllRules.First(r => r.Id == "state-negative").Enabled);
        Assert.False(store.AllRules.First(r => r.Id == "number").Enabled); // noisy pack off by default
    }

    [Fact]
    public void BuiltinIds_AreUnique()
    {
        Assert.Equal(
            BuiltinHighlights.Rules.Count,
            BuiltinHighlights.Rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
    }

    // ---- global deltas ----

    [Fact]
    public void SetEnabled_Builtin_PersistsAsDeltaAndReloads()
    {
        var store = NewStore();
        store.SetEnabled("state-negative", false); // default-on -> disabled delta
        store.SetEnabled("number", true);          // default-off -> enabled delta

        var reloaded = NewStore();
        Assert.False(reloaded.AllRules.First(r => r.Id == "state-negative").Enabled);
        Assert.True(reloaded.AllRules.First(r => r.Id == "number").Enabled);
    }

    [Fact]
    public void SetEnabled_BackToDefault_RemovesDelta()
    {
        var store = NewStore();
        store.SetEnabled("ipv4", false);
        store.SetEnabled("ipv4", true); // matches the shipped default again

        var json = File.ReadAllText(StorePath);
        Assert.DoesNotContain("ipv4", json);
    }

    [Fact]
    public void SetEnabled_UnknownId_IsIgnored()
    {
        var store = NewStore();
        store.SetEnabled("no-such-rule", false);
        Assert.False(File.Exists(StorePath)); // nothing changed, nothing saved
    }

    // ---- custom rules ----

    [Fact]
    public void CustomRules_RoundTrip()
    {
        var store = NewStore();
        store.SaveCustom(new HighlightRule
        {
            Id = "custom-1", Name = "VRF names", Pattern = @"\bVRF-\w+\b",
            Color = "#ff00ff", Bold = true, Underline = true, MatchCase = true,
        });

        var reloaded = NewStore();
        var rule = reloaded.AllRules.Single(r => r.Id == "custom-1");
        Assert.Equal("VRF names", rule.Name);
        Assert.Equal("custom", rule.Pack);
        Assert.True(rule.Bold);
        Assert.True(rule.Underline);
        Assert.True(rule.MatchCase);
        Assert.True(rule.Enabled);
        Assert.False(rule.IsBuiltin);
    }

    [Fact]
    public void SaveCustom_SameId_Replaces()
    {
        var store = NewStore();
        store.SaveCustom(new HighlightRule { Id = "custom-1", Name = "old", Pattern = "a" });
        store.SaveCustom(new HighlightRule { Id = "custom-1", Name = "new", Pattern = "b" });
        Assert.Equal("new", store.AllRules.Single(r => r.Id == "custom-1").Name);
    }

    [Fact]
    public void RemoveCustom_Removes()
    {
        var store = NewStore();
        store.SaveCustom(new HighlightRule { Id = "custom-1", Name = "x", Pattern = "x" });
        Assert.True(store.RemoveCustom("custom-1"));
        Assert.False(store.RemoveCustom("custom-1"));
        Assert.DoesNotContain(NewStore().AllRules, r => r.Id == "custom-1");
    }

    [Fact]
    public void SetEnabled_CustomRule_RewritesRule()
    {
        var store = NewStore();
        store.SaveCustom(new HighlightRule { Id = "custom-1", Name = "x", Pattern = "x" });
        store.SetEnabled("custom-1", false);
        Assert.False(NewStore().AllRules.Single(r => r.Id == "custom-1").Enabled);
    }

    // ---- corrupt file ----

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ not json");
        var store = NewStore();
        Assert.Equal(BuiltinHighlights.Rules.Count, store.AllRules.Count);
    }

    // ---- per-session resolution ----

    [Fact]
    public void ResolveForSession_NoOverrides_ReturnsGloballyEnabled()
    {
        var store = NewStore();
        var resolved = store.ResolveForSession(null);
        Assert.Contains(resolved, r => r.Id == "state-negative");
        Assert.DoesNotContain(resolved, r => r.Id == "number");
    }

    [Fact]
    public void ResolveForSession_DeltasWinOverGlobal()
    {
        var store = NewStore();
        store.SetEnabled("ipv4", false); // globally off now

        var overrides = new TerminalOverrides
        {
            EnabledRules = ["ipv4", "number"],
            DisabledRules = ["state-negative"],
        };
        var resolved = store.ResolveForSession(overrides);

        Assert.Contains(resolved, r => r.Id == "ipv4");            // session re-enables
        Assert.Contains(resolved, r => r.Id == "number");          // session enables default-off
        Assert.DoesNotContain(resolved, r => r.Id == "state-negative"); // session disables
        Assert.Contains(resolved, r => r.Id == "state-positive");  // untouched rules inherit
    }

    [Fact]
    public void OverridesIsEmpty_TracksHighlightDeltas()
    {
        Assert.True(new TerminalOverrides { EnabledRules = [], DisabledRules = [] }.IsEmpty);
        Assert.False(new TerminalOverrides { DisabledRules = ["ipv4"] }.IsEmpty);
    }

    // ---- builtin pattern sanity (compiled with .NET; the page runs the same subset in JS) ----

    private static IEnumerable<string> MatchesOf(string ruleId, string input)
    {
        var rule = BuiltinHighlights.Rules.Single(r => r.Id == ruleId);
        var options = rule.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
        return new Regex(rule.Pattern, options, TimeSpan.FromSeconds(1))
            .Matches(input).Select(m => m.Value);
    }

    [Fact]
    public void AllBuiltinPatterns_CompileInDotNet()
    {
        foreach (var rule in BuiltinHighlights.Rules)
            _ = new Regex(rule.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("iface-cisco", "GigabitEthernet0/0/1 is up", "GigabitEthernet0/0/1")]
    [InlineData("iface-cisco", "int Gi0/0/1.100", "Gi0/0/1.100")]
    [InlineData("iface-cisco", "Port-channel1 Vlan100", "Port-channel1")]
    [InlineData("iface-linux", "inet on eth0 and ens192", "eth0")]
    [InlineData("ipv4", "src 10.1.2.3/24 dst 192.168.0.1", "10.1.2.3/24")]
    [InlineData("ipv6", "addr 2001:db8:0:1::1/64", "2001:db8:0:1::1/64")]
    [InlineData("ipv6", "gateway fe80::1", "fe80::1")]
    [InlineData("mac", "bia 00:1a:2b:3c:4d:5e", "00:1a:2b:3c:4d:5e")]
    [InlineData("mac", "cisco 001a.2b3c.4d5e", "001a.2b3c.4d5e")]
    [InlineData("state-positive", "line protocol is up", "up")]
    [InlineData("state-negative", "administratively down", "down")]
    [InlineData("state-negative", "port is err-disabled", "err-disabled")]
    [InlineData("proto-routing", "router ospf 10", "ospf")]
    [InlineData("services", "sshd[1234]: accepted", "sshd")]
    [InlineData("duration", "uptime is 1w2d3h", "1w2d3h")]
    [InlineData("duration", "elapsed 01:23:45", "01:23:45")]
    public void BuiltinPatterns_MatchExpected(string ruleId, string input, string expected)
    {
        Assert.Contains(expected, MatchesOf(ruleId, input));
    }

    [Theory]
    [InlineData("ipv6", "std::vector and boost::asio")] // C++ scope tokens are not addresses
    [InlineData("ipv6", "log at 12:34:56 today")]       // hh:mm:ss is a duration, not IPv6
    public void BuiltinPatterns_AvoidFalsePositives(string ruleId, string input)
    {
        Assert.Empty(MatchesOf(ruleId, input));
    }
}
